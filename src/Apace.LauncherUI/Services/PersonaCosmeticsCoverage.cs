using System.Text.Json;

namespace Apace.LauncherUI.Services;

// Panel-side "Cosmetics coverage" reader (Marketplace Management page): per
// dressing-room tab, how many pieces the synced live index holds, how many
// have a servable binary on disk and how many would serve — the funnel the
// empty face tabs made visible (the index carried hundreds of face pieces,
// the volume had no binaries for them, the tabs rendered empty). Reads the
// same volume files the ApiServer serves from (separate processes share the
// volume, not the code): index.json, the binary mirrors (persona-catalog/
// binaries plus the legacy playfab/persona mirror) and packs.json.
//
// ON DEMAND by design: the page calls Read on button click only — never on
// navigation, never on a timer — because the first read parses the whole
// (megabyte-scale) index; stamp-cached so repeated clicks are free while
// nothing changed on disk.
//
// "Serves" is an APPROXIMATION of the ApiServer's live offer for a player
// with the SAVED DEFAULT packs (per-player grants add more): binary present,
// non-emote, the pack gate on theme tags (skins and face-craft types are
// exempt — keep in sync with PersonaPieceTypes.IsAlwaysAllowedFaceCraftPieceType
// on the ApiServer) and — while "only supported cosmetics" is on — no risky
// compat verdict (CosmeticCompatIndex, same hide-only-positive-evidence rule
// as the gate). NOT applied: the grouped per-category limit, the blank-slot
// filler and the flatOnly custom-skeleton hide (a piece badged
// auto-repairable here can still stay hidden under flatOnly).
public static class PersonaCosmeticsCoverage
{
    private const string BinariesDirName = "binaries"; // the ApiServer's LivePersonaCatalog.BinariesDirName
    private const string LegacyPersonaRoot = "playfab"; // the legacy piece mirror (<mojang-assets>/playfab/persona/<itemId>.zip)
    private const string LegacyPersonaDirName = "persona";

    /// <summary>One dressing-room tab's funnel: index / on disk / in offer,
    /// summed over the piece types the tab queries.</summary>
    public sealed record CoverageTab(string Tab, int IndexItems, int WithBinaryOnDisk, int OfferedNow);

    /// <summary>The whole snapshot: one row per dressing-room tab (layout
    /// order, then unknown-type "Other" last), the emote count (never shown
    /// in a tab) and an error text when the index could not be read at all
    /// (null = healthy read).</summary>
    public sealed record CoverageSnapshot(List<CoverageTab> Tabs, int EmoteItems, string? Error);

    // The committed dressing-room layout (PersonaQueryManifest_V1.0, layout
    // item a2e12d4d-3f07-4d47-8f85-2d5b9a44c101): one tab per sub category,
    // each querying the piece types below — same grouping the ApiServer's
    // PersonaPieceTypes.ParseCategoryGroups derives (separate processes share
    // the volume, not the code — keep both in sync with the layout item).
    // Order = the order the client renders the tabs in.
    private static readonly (string Tab, string[] Types)[] TabLayout =
    [
        ("Tops", ["persona_top"]),
        ("Bottoms", ["persona_bottom", "persona_high_pants", "persona_dress"]),
        ("Outerwear", ["persona_outerwear", "persona_hood"]),
        ("Headwear", ["persona_head"]),
        ("Gloves", ["persona_hand"]),
        ("Shoes", ["persona_feet"]),
        ("Back", ["persona_back"]),
        ("Face accessories", ["persona_face_accessory"]),
        ("Skins", ["persona_skin"]),
        ("Hair", ["persona_hair"]),
        ("Eyes", ["persona_eyes"]),
        ("Mouth", ["persona_mouth"]),
        ("Facial hair", ["persona_facial_hair"]),
        ("Arms", ["persona_arms"]),
        ("Legs", ["persona_legs"]),
        ("Body", ["persona_body"]),
    ];

    // The face-craft categories the ApiServer never gates (pack selection and
    // compat alike) — a mirror of PersonaPieceTypes.IsAlwaysAllowedFaceCraftPieceType.
    private static bool IsFaceCraftType(string pieceType)
        => pieceType is "persona_skin" or "persona_eyes" or "persona_hair"
            or "persona_mouth" or "persona_facial_hair" or "persona_face_accessory";

    // The packs.json halves this approximation needs (the panel writes the
    // file itself — PersonaPacks.razor's PacksConfig). Missing keys fall back
    // to the server defaults: onlySupported on, no default packs, legacy
    // mirror on.
    private sealed class PacksKnobs
    {
        public bool? OnlySupported { get; set; }
        public List<string>? DefaultPacks { get; set; }
        public bool? EnableLegacyCosmetics { get; set; }
    }

    private sealed class Counters
    {
        public int Index;
        public int Disk;
        public int Offer;

        public CoverageTab ToTab(string tab) => new(tab, Index, Disk, Offer);
    }

    private static readonly object CacheLock = new();
    private static (DateTime IndexWrite, long IndexLength, DateTime PacksWrite, long PacksLength)? _cacheStamp;
    private static CoverageSnapshot? _cache;

    /// <summary>The coverage snapshot for the current volume state. Runs the
    /// index parse on the first call (or after an index/packs change) — call
    /// from a button handler on the thread pool, never per render.</summary>
    public static CoverageSnapshot Read()
    {
        string indexPath = LivePersonaCatalogSync.IndexPath;
        string packsPath = CosmeticCompatIndex.PacksPath;
        FileInfo indexInfo = new(indexPath);
        FileInfo packsInfo = new(packsPath);
        (DateTime, long, DateTime, long) stamp = (
            indexInfo.Exists ? indexInfo.LastWriteTimeUtc : DateTime.MinValue,
            indexInfo.Exists ? indexInfo.Length : -1,
            packsInfo.Exists ? packsInfo.LastWriteTimeUtc : DateTime.MinValue,
            packsInfo.Exists ? packsInfo.Length : -1);

        lock (CacheLock)
        {
            if (_cache is { } cached && _cacheStamp == stamp)
            {
                return cached;
            }
        }

        CoverageSnapshot snapshot = ReadUncached(packsPath);

        lock (CacheLock)
        {
            _cacheStamp = stamp;
            _cache = snapshot;
        }

        return snapshot;
    }

    private static CoverageSnapshot ReadUncached(string packsPath)
    {
        try
        {
            var index = JsonSerializer.Deserialize<LivePersonaCatalogSync.SyncIndex>(
                File.ReadAllText(LivePersonaCatalogSync.IndexPath), PersonaPackIndex.JsonWebOptions);
            if (index?.Items is not { Count: > 0 })
            {
                return new CoverageSnapshot([], 0, "Marketplace catalog not indexed yet — run a sync first.");
            }

            PacksKnobs knobs = ReadPacksKnobs(packsPath);
            HashSet<string> binaries = GuidZipIds(Path.Combine(LivePersonaCatalogSync.CatalogDir, BinariesDirName));
            HashSet<string> legacy = knobs.EnableLegacyCosmetics ?? true
                ? GuidZipIds(Path.Combine(MojangAssetPaths.MojangAssetsDir, LegacyPersonaRoot, LegacyPersonaDirName))
                : [];
            bool onlySupported = knobs.OnlySupported ?? true;
            var defaultPacks = new HashSet<string>(knobs.DefaultPacks ?? [], StringComparer.OrdinalIgnoreCase);
            Dictionary<string, CosmeticCompatIndex.CosmeticCompatInfo> compat = onlySupported ? CosmeticCompatIndex.ReadAll() : [];

            // Type-ish tags (the piece type restated as tag.hair etc.) are
            // descriptions, not gating packs — the same rule as
            // PersonaPackIndex.ReadThemePacks and the ApiServer's offer.
            var typeSuffixes = index.Items
                .Where(item => !item.IsEmote && item.PieceType.StartsWith("persona_", StringComparison.Ordinal))
                .Select(item => item.PieceType["persona_".Length..])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool IsThemeTag(string tag)
            {
                return tag.StartsWith("tag.", StringComparison.OrdinalIgnoreCase)
                    && !typeSuffixes.Contains(tag["tag.".Length..]);
            }

            var byType = new Dictionary<string, Counters>(StringComparer.Ordinal);
            int emotes = 0;
            foreach (LivePersonaCatalogSync.SyncIndexItem item in index.Items)
            {
                if (item.IsEmote)
                {
                    emotes++;
                    continue; // the dressing room has no emote tab — excluded everywhere
                }

                Counters counters = GetCounters(byType, item.PieceType);
                counters.Index++;

                bool onDisk = binaries.Contains(item.Id) || legacy.Contains(item.Id);
                if (!onDisk)
                {
                    continue; // hidden — the download would hang the client, never offered
                }

                counters.Disk++;

                // The offer approximation: face-craft types and skins serve
                // ungated; accessories need a theme tag inside the saved
                // default packs (an empty selection = 0 accessories, exactly
                // like the ApiServer gate), and while "only supported" is on
                // a risky compat verdict hides the piece (face-craft exempt).
                bool serves = IsFaceCraftType(item.PieceType)
                    || (defaultPacks.Count > 0 && (item.Tags ?? []).Any(tag => IsThemeTag(tag) && defaultPacks.Contains(tag)));
                if (serves && onlySupported && !IsFaceCraftType(item.PieceType)
                    && CosmeticCompatIndex.Lookup(compat, item.Id).Kind == CosmeticCompatIndex.CosmeticCompatKind.Risky)
                {
                    serves = false;
                }

                if (serves)
                {
                    counters.Offer++;
                }
            }

            var rows = new List<CoverageTab>(TabLayout.Length + 1);
            var usedTypes = new HashSet<string>(StringComparer.Ordinal);
            foreach ((string tab, string[] types) in TabLayout)
            {
                var merged = new Counters();
                foreach (string type in types)
                {
                    usedTypes.Add(type);
                    Counters counters = GetCounters(byType, type);
                    merged.Index += counters.Index;
                    merged.Disk += counters.Disk;
                    merged.Offer += counters.Offer;
                }

                rows.Add(merged.ToTab(tab));
            }

            // piece types the committed layout does not query get one shared
            // row at the end (the ApiServer buckets them the same way — no
            // client tab can ever show them)
            int otherIndex = 0, otherDisk = 0, otherOffer = 0;
            foreach (var kv in byType)
            {
                if (!usedTypes.Contains(kv.Key))
                {
                    otherIndex += kv.Value.Index;
                    otherDisk += kv.Value.Disk;
                    otherOffer += kv.Value.Offer;
                }
            }

            if (otherIndex > 0)
            {
                rows.Add(new CoverageTab("Other (no client tab)", otherIndex, otherDisk, otherOffer));
            }

            return new CoverageSnapshot(rows, emotes, null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new CoverageSnapshot([], 0, $"Reading the catalog index failed: {ex.Message}");
        }
    }

    private static PacksKnobs ReadPacksKnobs(string packsPath)
    {
        try
        {
            return File.Exists(packsPath)
                ? JsonSerializer.Deserialize<PacksKnobs>(File.ReadAllText(packsPath), PersonaPackIndex.JsonWebOptions) ?? new PacksKnobs()
                : new PacksKnobs();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new PacksKnobs(); // server defaults — onlySupported on, no packs, legacy on
        }
    }

    // Catalog item ids with a zip on disk — the same rule as the ApiServer's
    // BinaryIdsSnapshot/GetLocalPersonaBinaries: *.zip files whose name parses
    // as a GUID. Listing a couple of thousand filenames is milliseconds; no
    // caching needed beyond the index/packs stamp above.
    private static HashSet<string> GuidZipIds(string dir)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(dir))
            {
                foreach (string file in Directory.EnumerateFiles(dir, "*.zip"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (Guid.TryParse(name, out _))
                    {
                        ids.Add(name);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // unreadable dir — nothing counts as mirrored from it
        }

        return ids;
    }

    private static Counters GetCounters(Dictionary<string, Counters> byType, string pieceType)
    {
        if (byType.TryGetValue(pieceType, out Counters? counters) && counters is not null)
        {
            return counters;
        }

        Counters created = new();
        byType[pieceType] = created;
        return created;
    }
}
