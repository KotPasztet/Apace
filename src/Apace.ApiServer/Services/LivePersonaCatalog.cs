using Serilog;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using ILogger = Serilog.ILogger;
using CItem = Apace.StaticData.Playfab.Item;

namespace Apace.ApiServer.Services;

// The live persona catalog.
//
// The panel's LivePersonaCatalogSync (Apace.LauncherUI) walks the still-live
// PlayFab marketplace of the Minecraft Earth title (20ca2) — ~40k PersonaDurable
// entries, ~12k unique character creator pieces incl. emotes — and caches the
// metadata in the persistent data volume:
//
//   <mojang-assets>/persona-catalog/index.json          deduped per-piece summary (facts)
//   <mojang-assets>/persona-catalog/items/<id>.json     raw PlayFab item metadata (facts)
//
// Nothing from the catalog is ever committed to the repo: metadata are cached
// runtime facts in the volume, binaries are only downloaded on demand (below).
//
// This service is the ApiServer side: it turns the cached metadata into the
// catalog offer served to the client (Catalog/Search + Catalog/GetPublishedItem)
// and owns the lazy binary/thumbnail liveness machinery:
//
//   - Offer build ("unlock-all"): every offered item is a plain free item —
//     achievement lock tags stripped, price 0, purchasable true — mirroring what
//     update_apace_items.py did for the legacy 231 items. The strip happens on
//     the OUTPUT only; the raw cache keeps the live metadata verbatim.
//   - MIRROR-FIRST OFFER: the offer contains ONLY pieces whose binary exists
//     on disk (the volume cache or the mojang-assets mirror dir) — the client
//     never sees a piece it cannot download, so nothing hangs on
//     "wczytywanie…" and nothing renders empty. The BinaryWarmUp below feeds
//     the disk: it resolves URLs in batches (Catalog/GetItems, one call per
//     20 ids) and downloads in parallel, so the selected bundles' pieces
//     mirror in minutes. A piece that is not mirrored (probe-dead, not yet
//     reached, outside the selected packs) simply does not list.
//   - Lazy probe: the client never sees a live CDN URL — the offer advertises
//     /playfab/persona/<itemId>.zip (PersonaCdnController). On the first client
//     request for a piece binary the controller resolves the real URL
//     (Catalog/GetItem — Search never returns Contents), HEAD-probes it
//     (404 → the same GUID re-hosted on the new PlayFab content CDN
//     content1/content2.prod.catalog.playfab.com → else dead), downloads the
//     piece into <mojang-assets>/persona-catalog/binaries/ and serves it.
//     That lazy pipeline stays as the fallback for pieces a warm-up pass has
//     not reached yet (they are offer-hidden until their binary lands).
//   - Thumbnails are resolved the same lazy way by MojangImagesCdnController
//     (cache → live CDN URL → Bedrock-Persona mirror URL → 404).
//
// The old "deliberately NO bulk probing" rule is GONE: the upstream content CDN
// is dying (2026-11-25 — a sampled dressing-room session lost 12/12 cold fetches
// and direct probes confirmed 404 on the primary AND both alternate hosts, i.e.
// the binaries are gone globally, not blocked per-IP). BinaryWarmUp below now
// mirrors every servable piece into the volume in the background BEFORE the
// client asks — batch URL resolve (Catalog/GetItems) + parallel CDN downloads,
// so the selected bundles' pieces land in minutes; the lazy per-request
// pipeline stays as the fallback for pieces a pass has not reached yet.
public static class LivePersonaCatalog
{
    public const string CatalogDirName = "persona-catalog";
    public const string IndexFileName = "index.json";
    public const string ProbeStateFileName = "probe-state.json";
    public const string ItemsDirName = "items";
    public const string BinariesDirName = "binaries";
    public const string ThumbnailsDirName = "thumbnails";

    // MCE PlayFab namespace — the path segment of the persona asset URLs.
    public const string MceNamespace = "b63a0803d3653643";
    public const string PlayFabApi = "https://20ca2.playfabapi.com";
    public const string Scid = "4fc10100-5f7a-4470-899b-280835760c07";

    // The PlayFab content CDN hosts the same namespace/<guid>/primary.zip path
    // the old xforge CDN served — the 22.11 probe found new catalog items alive
    // there while the xforge originals are dark (generate_mojang_asset_manifest.py
    // probes the same shapes for the manifest).
    public static readonly string[] AlternateCdnHosts =
    [
        "content1.prod.catalog.playfab.com",
        "content2.prod.catalog.playfab.com",
    ];

    // Runtime thumbnail mirror: raw.githubusercontent.com serves the mirrored
    // Bedrock persona images, flat assets/images/persona_piece/<thumbnailId>.png
    // (research/bedrock-persona-report.md: the repo mirrors exactly the 20ca2
    // Catalog/Search scope, keyed by thumbnail id — verified 23.11, live URL).
    // Nothing from it is stored in the repo.
    public const string ThumbnailMirrorBase = "https://raw.githubusercontent.com/Daniel-Ric/Bedrock-Persona/main/assets/images/persona_piece";

    // unlock-all: tags the client reads as achievement locks (the MCE client shows
    // those items as locked behind achievements and refuses to acquire them —
    // same list scripts/update_apace_items.py stripped from the legacy items).
    public static readonly string[] LockTags =
    [
        "is_achievement",
        "earth_achievement",
        "vanilla_achievement",
    ];

    // hidden_offer items never appeared in the dressing room (StoreConfig
    // globalNotTags excludes them from every query) — keep it that way.
    public const string HiddenOfferTag = "hidden_offer";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim StateLock = new(1, 1);

    public static string CatalogDir => Path.Combine(Program.MojangAssetsDir, CatalogDirName);
    public static string IndexPath => Path.Combine(CatalogDir, IndexFileName);
    public static string ProbeStatePath => Path.Combine(CatalogDir, ProbeStateFileName);
    public static string ItemsDir => Path.Combine(CatalogDir, ItemsDirName);
    public static string BinariesDir => Path.Combine(CatalogDir, BinariesDirName);
    public static string ThumbnailsDir => Path.Combine(CatalogDir, ThumbnailsDirName);

    /// <summary>Panel toggle (--live-persona-catalog).</summary>
    public static bool Enabled { get; set; }

    /// <summary>True when enabled AND a usable synced index exists in the volume.</summary>
    public static bool IsAvailable => Enabled && Index is not null;

    /// <summary>Index stamp — changes on every re-sync; offer caches key off it.</summary>
    public static long IndexStamp { get; private set; }

    public static DateTimeOffset? LastSyncAt => Index?.SyncedAt;
    public static int IndexedItemCount => Index?.Items.Count ?? 0;
    public static int EmoteCount => Index?.EmoteCount ?? 0;

    // ---------------------------------------------------------------------------
    // Index loading (hot-reloads when the panel re-syncs)
    // ---------------------------------------------------------------------------

    public sealed record LiveIndexItem(
        string Id,
        string Title,
        string Description,
        Dictionary<string, List<string>>? Keywords,
        List<string> Tags,
        string PieceType,
        Guid PieceId,
        string PieceVersion,
        string Rarity,
        string? CreatorName,
        string? CreatorEntityId,
        Guid? OfferId,
        List<LiveItemReference> ItemReferences,
        string? ThumbnailId,
        string? ThumbnailUrl,
        DateTime CreationDate,
        DateTime LastModifiedDate,
        DateTime? StartDate,
        bool IsEmote)
    {
        [JsonIgnore]
        public bool HasThumbnail => !string.IsNullOrWhiteSpace(ThumbnailId);
    }

    public sealed record LiveItemReference(Guid Id, int Amount);

    // One thematic skin pack (MarketplaceDurableCatalog_V1.2 bundle) — mirrors
    // LivePersonaCatalogSync.SyncSkinPack (separate processes share the volume).
    public sealed record LiveSkinPack(
        string Id,
        string Title,
        string PackUuid,
        string PackVersion,
        string? CreatorName,
        int SkinCount,
        string? ThumbnailId,
        string? ThumbnailUrl,
        List<string> Tags,
        List<LiveItemReference> ItemReferences,
        DateTime CreationDate,
        DateTime LastModifiedDate);

    public sealed record LiveIndex(DateTimeOffset? SyncedAt, int RawItemCount, int EmoteCount, List<LiveIndexItem> Items, List<LiveSkinPack> SkinPacks);

    private static LiveIndex? _index;

    private static LiveIndex? Index
    {
        get
        {
            if (!Enabled)
            {
                return null;
            }

            try
            {
                if (File.Exists(IndexPath))
                {
                    long stamp = File.GetLastWriteTimeUtc(IndexPath).Ticks;
                    if (_index is null || stamp != IndexStamp)
                    {
                        var loaded = JsonSerializer.Deserialize<LiveIndex>(File.ReadAllText(IndexPath), JsonOptions);
                        if (loaded?.Items is { Count: > 0 })
                        {
                            // indexes written before the skin pack walk carry no skinPacks array
                            _index = loaded with { SkinPacks = loaded.SkinPacks ?? [] };
                            IndexStamp = stamp;
                            Log.Information(
                                "Live persona catalog index loaded from {Path} — {Unique} unique piece(s) from {Raw} raw item(s), emotes: {Emotes}, synced {Synced}",
                                IndexPath, loaded.Items.Count, loaded.RawItemCount, loaded.EmoteCount, loaded.SyncedAt);
                        }
                        else
                        {
                            Log.Warning("Live persona catalog index at {Path} has no items — live offer disabled", IndexPath);
                        }
                    }
                }
                else if (_index is not null)
                {
                    Log.Information("Live persona catalog index gone from {Path} — live offer disabled", IndexPath);
                    _index = null;
                    IndexStamp = 0;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not load the live persona catalog index from {Path}", IndexPath);
            }

            return _index;
        }
    }

    // ---------------------------------------------------------------------------
    // The offer (unlock-all strip applied here, never to the raw cache)
    // ---------------------------------------------------------------------------

    /// <summary>The cached metadata as catalog items ready for the client: one item per
    /// persona piece (deduped by the sync, defensively here too), emotes skipped
    /// (see docs/live-persona-catalog.md — the MCE dressing room layout has no
    /// emote category), achievement/lock fields neutralized, Contents pointing at
    /// the local persona proxy. Items probed dead drop out until they revive.</summary>
    public static IReadOnlyList<CItem> GetOfferItems() => GetOffer().Items;

    public sealed record LiveOffer(IReadOnlyList<CItem> Items, IReadOnlyList<IReadOnlyList<string>> ThemeTags);

    // The theme-tag suffixes that merely restate the piece's own category
    // (tag.hair on hair pieces, tag.eyes on eye pieces…) — descriptions, not
    // themes, so they must never form a gating pack. Derived from the index's
    // piece types; PersonaPackIndex (panel) applies the same rule when it lists
    // the packs (separate processes share the volume, not the code).
    private static HashSet<string> TypeTagSuffixes(LiveIndex index)
        => index.Items
            .Where(item => !item.IsEmote && item.PieceType.StartsWith("persona_", StringComparison.Ordinal))
            .Select(item => item.PieceType["persona_".Length..])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // A piece's gating themes: its marketplace theme tags minus the type-ish ones.
    private static List<string> ThemeTagsOf(LiveIndexItem entry, IReadOnlySet<string> typeSuffixes)
        => [.. entry.Tags
            .Where(tag => tag.StartsWith("tag.", StringComparison.OrdinalIgnoreCase)
                && !typeSuffixes.Contains(tag["tag.".Length..]))
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>A piece's gating theme tags — the same tags the offer's pack gate
    /// matches against (marketplace tag.* minus the type-ish suffixes).</summary>
    public static IReadOnlyList<string> ThemeTagsOf(LiveIndexItem entry)
    {
        var index = Index;
        return index is null ? [] : ThemeTagsOf(entry, TypeTagSuffixes(index));
    }

    /// <summary>
    /// The offer plus each item's gating theme tags, aligned with Items. The
    /// marketplace has no accessory pack entities — every PersonaDurable entry
    /// carries exactly one packIdentity, the piece itself (probe:
    /// scripts/probe_persona_tags.py) — so the pack gate runs on the pieces'
    /// marketplace theme tags (tag.*): a pack spans every piece category the tag
    /// appears on, accessories need one of their tags in the allowed
    /// set (tagless accessories never serve; skins are exempt), and the
    /// filtering itself happens per request in the controllers.
    /// MIRROR-FIRST: a piece only OFFERS once its binary exists on disk (the
    /// warm-up mirrors the selected bundles' pieces into the volume; the panel's
    /// mirror dir counts too). Pieces without a binary would hang the client on
    /// "wczytywanie…" and then render nothing — they never enter the offer.
    /// </summary>
    public static LiveOffer GetOffer()
    {
        var index = Index;
        if (index is null)
        {
            return new LiveOffer([], []);
        }

        var hidden = GetHiddenItemIds();
        var typeSuffixes = TypeTagSuffixes(index);
        var items = new List<CItem>(index.Items.Count);
        var themeTags = new List<IReadOnlyList<string>>(index.Items.Count);
        var seenPieces = new HashSet<Guid>();
        foreach (var entry in index.Items)
        {
            if (entry.IsEmote || hidden.Contains(entry.Id))
            {
                continue;
            }

            if (!seenPieces.Add(entry.PieceId))
            {
                continue;
            }

            items.Add(BuildOfferItem(entry));
            themeTags.Add(ThemeTagsOf(entry, typeSuffixes));
        }

        return new LiveOffer(items, themeTags);
    }

    public static CItem? GetOfferItem(string itemId)
    {
        var index = Index;
        if (index is null)
        {
            return null;
        }

        var entry = index.Items.FirstOrDefault(item => item.Id == itemId);
        return entry is not null && !entry.IsEmote && !GetHiddenItemIds().Contains(entry.Id) ? BuildOfferItem(entry) : null;
    }

    /// <summary>The raw index entry for one piece id, or null when the id is not
    /// indexed (ids outside the index are never pack-gated).</summary>
    public static LiveIndexItem? GetIndexEntry(string itemId)
        => Index?.Items.FirstOrDefault(item => item.Id == itemId);

    /// <summary>Every piece uuid in the raw live index — probe-dead, offer-hidden
    /// and emote entries included. The receipt must cover uuids a client may
    /// still hold from earlier sessions: a dead or unoffered piece still
    /// receives a stub, and a stub whose uuid carries no receipt entitlement
    /// hangs the client's import forever.</summary>
    public static IReadOnlyList<Guid> GetAllIndexPieceIds()
        => Index?.Items.Select(item => item.PieceId).Distinct().ToList() ?? [];

    /// <summary>unlock-all: the offer item is a plain free item, whatever the marketplace says.</summary>
    public static CItem BuildOfferItem(LiveIndexItem entry)
    {
        // Tags: drop the achievement lock tags verbatim; hidden_offer entries were
        // already dropped by the sync (belt and braces here).
        var tags = entry.Tags.Where(tag => !LockTags.Contains(tag, StringComparer.OrdinalIgnoreCase)).ToList();

        return new CItem(
            true, // Purchasable — reward-only items were not acquirable; every offer is claimable
            new CItem.PersonaData(
                entry.PieceType,
                entry.PieceId,
                entry.OfferId ?? entry.PieceId,
                entry.PieceVersion,
                entry.Rarity,
                0, // Price — 0 = free
                entry.CreatorName ?? "Minecraft",
                null // AssetFileName — binaries ride the Contents personabinary entry below
            ),
            entry.Title,
            string.IsNullOrWhiteSpace(entry.Description) ? "§" : entry.Description,
            entry.ThumbnailId,
            entry.CreationDate,
            entry.LastModifiedDate,
            entry.StartDate ?? entry.CreationDate,
            Guid.Parse(entry.Id),
            entry.OfferId ?? entry.PieceId,
            "B63A0803D3653643",
            entry.CreatorEntityId ?? "301F442C3B63DC20",
            tags,
            entry.Keywords?.Count is > 0
                ? entry.Keywords.ToDictionary(kv => kv.Key, kv => new CItem.KeywordValues(kv.Value))
                : new Dictionary<string, CItem.KeywordValues>(),
            [
                // the client downloads every piece from the item's personabinary
                // content — always through OUR proxy path; the real URL is resolved
                // server-side on the first request (lazy probe below)
                new CItem.QueryManifestContent(
                    $"{entry.PieceId}_personabinary",
                    $"/playfab/persona/{entry.Id}.zip",
                    "65535.65535.65535",
                    "1.13.0",
                    [],
                    "personabinary"
                ),
            ],
            entry.ItemReferences.Select(reference => new CItem.ItemReference(reference.Id, reference.Amount)).ToList(),
            new Dictionary<string, string>(),
            new Dictionary<string, string>()
        );
    }

    /// <summary>The indexed thematic skin packs (MarketplaceDurableCatalog_V1.2 bundles).</summary>
    public static IReadOnlyList<LiveSkinPack> GetSkinPacks() => Index?.SkinPacks ?? [];

    /// <summary>
    /// The offer item for one thematic skin pack: a ClientConfig bundle entry the
    /// client parses itself (same shape as the committed "Standard" pack) —
    /// unlock-all pricing, tags kept verbatim (store searches filter on them).
    /// Packs the client does not have built in are imported by fetching the offer's
    /// skinbinary Contents entry (docs/custom-persona-items.md "Downloadable skin
    /// packs") — it points at the proxy path SkinPackCdnController serves, and the
    /// zip behind it is resolved and downloaded lazily on the first client request
    /// (<see cref="TryEnsureSkinPackZipAsync"/>), like the persona piece binaries.
    /// </summary>
    public static CItem BuildSkinPackOfferItem(LiveSkinPack pack)
    {
        var tags = pack.Tags.Where(tag => !LockTags.Contains(tag, StringComparer.OrdinalIgnoreCase)).ToList();
        // the store and the featured-row queries filter on these two
        if (!tags.Contains("skinpack", StringComparer.OrdinalIgnoreCase))
        {
            tags.Add("skinpack");
        }

        if (!tags.Contains("has_skinpack", StringComparer.OrdinalIgnoreCase))
        {
            tags.Add("has_skinpack");
        }

        // the Classic Skins tab's own search (StoreConfig dressingRoomClassicSkinsSearch)
        // looks for tag.cat — the committed skin pack offers carry it for the same
        // reason, and a live pack without it never surfaces in that search
        if (!tags.Contains("tag.cat", StringComparer.OrdinalIgnoreCase))
        {
            tags.Add("tag.cat");
        }

        Guid id = Guid.Parse(pack.Id);
        var displayProperties = JsonSerializer.SerializeToElement(new
        {
            creatorName = pack.CreatorName ?? "Minecraft",
            offerId = pack.Id,
            price = 0,
            purchasable = true,
            packIdentity = new[] { new { type = "skinpack", uuid = pack.PackUuid, version = pack.PackVersion } },
            skinPack = new { count = pack.SkinCount },
        });

        return new CItem(
            true, // Purchasable — unlock-all: every offer is claimable
            new CItem.ClientConfigData(
                "MarketplaceDurableCatalog_V1.2",
                "bundle",
                "1.0.0",
                "65535.65535.65535",
                false, // IsStackable — like the committed skin pack offers
                0, // Price — 0 = free
                displayProperties
            ),
            pack.Title,
            "§",
            pack.ThumbnailId,
            pack.CreationDate,
            pack.LastModifiedDate,
            pack.CreationDate,
            id,
            id,
            "B63A0803D3653643",
            "301F442C3B63DC20",
            tags,
            new Dictionary<string, CItem.KeywordValues>(),
            [
                // the client imports a pack it does not have built in from the item's
                // skinbinary content — always through OUR proxy path; the real URL is
                // resolved server-side on the first request (TryEnsureSkinPackZipAsync
                // below). Same shape as the committed skin pack offers: Contents entry
                // id = the offer id, MinClientVersion 1.0.0.
                new CItem.QueryManifestContent(
                    pack.Id,
                    $"/playfab/skinpacks/{pack.Id}.zip",
                    "65535.65535.65535",
                    "1.0.0",
                    [],
                    "skinbinary"
                ),
            ],
            pack.ItemReferences.Select(reference => new CItem.ItemReference(reference.Id, reference.Amount)).ToList(),
            new Dictionary<string, string>(),
            new Dictionary<string, string>()
        );
    }

    // ---------------------------------------------------------------------------
    // Probe state (lazy liveness cache)
    // ---------------------------------------------------------------------------

    public sealed record ProbeEntry(
        string State, // "live" | "dead"
        string? ResolvedUrl,
        DateTimeOffset CheckedAt,
        string? Source);

    /// <summary>
    /// Geometry compatibility facts for one piece binary, captured by opening the
    /// zip (see <see cref="InspectGeometryCompat"/>): the WORST case across every
    /// geometry variant the piece carries — <see cref="FormatVersion"/> is the
    /// highest parsed format_version, <see cref="LowestFormatVersion"/> the
    /// lowest (a single out-of-band body-size variant moves one of them), the
    /// effect flags are OR-ed in from all candidates. Null fields are unknown —
    /// only clear signals ever set true/false, and a parse failure leaves
    /// everything null.
    /// </summary>
    public sealed record GeometryCompatInfo(
        string? FormatVersion,
        bool? HasAnimations,
        bool? HasParticles,
        // Forward-compat part of the pipeline contract — the panel's
        // CosmeticCompatIndex reads the same keys and its risky rule includes
        // render controllers and face bones; the inspect pass records them
        // once it scans for them, and a file without the keys leaves them
        // null (null = not scanned, not disqualifying on their own).
        bool? HasRenderControllers = null,
        bool? HasFaceBones = null,
        string? LowestFormatVersion = null,
        float? MaxVisibleBoundsWidth = null,
        float? MaxVisibleBoundsHeight = null,
        bool? HasNonstandardBones = null,
        bool? HasTextureBindingIssue = null);

    private sealed class ProbeStateFile
    {
        public Dictionary<string, ProbeEntry> Items { get; set; } = [];

        // Inspector schema stamp for the geometry facts: bumped whenever
        // InspectCompatEntries changes what it records. A file stamped with an
        // older version is not loaded — every piece re-inspects lazily from its
        // binary on the first touch, so derived verdicts recompute once after
        // the upgrade instead of resting on stale/partial facts.
        public int? GeometrySchemaVersion { get; set; }

        // Parallel to Items and persisted in the same file, keyed by item id:
        // compat facts are independent of the probe verdict (a mirror-served piece
        // is alive without ever being probed upstream, and vice versa).
        public Dictionary<string, GeometryCompatInfo> Geometry { get; set; } = [];
    }

    private static readonly Dictionary<string, ProbeEntry> ProbeStates = [];
    private static readonly Dictionary<string, GeometryCompatInfo> GeometryCompatStates = [];
    private static bool _probeStatesLoaded;
    private static bool _probeStateDirty;
    private static DateTime _probeStateLastSave = DateTime.MinValue;

    private static void LoadProbeStates()
    {
        if (_probeStatesLoaded)
        {
            return;
        }

        _probeStatesLoaded = true;
        try
        {
            if (!File.Exists(ProbeStatePath))
            {
                return;
            }

            var file = JsonSerializer.Deserialize<ProbeStateFile>(File.ReadAllText(ProbeStatePath), JsonOptions);
            if (file?.Items is not null)
            {
                foreach (var (itemId, entry) in file.Items)
                {
                    ProbeStates[itemId] = entry;
                }
            }

            if (file?.Geometry is not null)
            {
                if (file.GeometrySchemaVersion == GeometryCompatSchemaVersion)
                {
                    foreach (var (itemId, compat) in file.Geometry)
                    {
                        GeometryCompatStates[itemId] = compat;
                    }
                }
                else
                {
                    Log.Information("Persona compat facts in {Path} were recorded by an older inspector (schema {Recorded} != {Current}) — every piece re-inspects from its binary on first touch",
                        ProbeStatePath, file.GeometrySchemaVersion, GeometryCompatSchemaVersion);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load the live persona probe state from {Path} — every item starts unchecked", ProbeStatePath);
        }
    }

    // A dead verdict from BEFORE the last-but-one sync is not trusted forever — a
    // Mojang CDN migration can revive pieces, so stale dead entries (older than
    // 24h) fall back to unchecked and get re-probed on the next client request.
    private static bool IsStaleDead(ProbeEntry entry)
        => entry.State == "dead" && entry.CheckedAt < (LastSyncAt ?? DateTimeOffset.UtcNow.AddDays(-1)) - TimeSpan.FromHours(24);

    // ids of the pieces that have a servable binary on disk right now (volume
    // cache ∪ mojang-assets mirror), cached per (binaries dir stamp, mirror dir
    // stamp, landed counter) — the dirs only change when files land or are
    // removed, so every request is a pair of stat calls, not a 12k-file scan.
    // The volume cache dir grows while the warm-up runs; each landed file moves
    // the stamp and the next snapshot picks the piece up.
    //
    // The dir mtime ALONE proved useless as the change signal: on the field
    // install (Docker volume) it never moved while the warm-up downloaded
    // hundreds of binaries, so the offer cache froze at the pre-warm-up set
    // (offer=257 while 759 files landed; the filler pool stayed at 255
    // candidates; every face tab queried "matched no items"). MarkBinaryLanded
    // — called by every path that writes a binary — is the reliable signal.
    private static long _binaryIdsStamp = -1;
    private static HashSet<string> _binaryIds = new(StringComparer.OrdinalIgnoreCase);
    private static int _binaryLandedVersion;

    /// <summary>Records that a piece binary just landed in the volume cache —
    /// bumps the change counter BinaryIdsSnapshot consults, so the hidden-id
    /// set (and with it the offer, the filler pool and every face tab) refresh
    /// on the next request without waiting for a dir mtime that may never
    /// move.</summary>
    public static void MarkBinaryLanded() => Interlocked.Increment(ref _binaryLandedVersion);

    // Caller must hold StateLock.
    private static HashSet<string> BinaryIdsSnapshot()
    {
        long stamp = 0;
        try
        {
            if (Directory.Exists(BinariesDir))
            {
                stamp = Directory.GetLastWriteTimeUtc(BinariesDir).ToUniversalTime().Ticks;
            }
        }
        catch (Exception)
        {
            stamp = 0;
        }

        Dictionary<string, string> local = GetLocalPersonaBinaries(); // stamp-cached itself
        long combined = HashCode.Combine(stamp, _localPersonaStamp, Volatile.Read(ref _binaryLandedVersion));
        if (combined == _binaryIdsStamp)
        {
            return _binaryIds;
        }

        var ids = new HashSet<string>(local.Keys, StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(BinariesDir))
            {
                foreach (string file in Directory.EnumerateFiles(BinariesDir, "*.zip"))
                {
                    string itemId = Path.GetFileNameWithoutExtension(file);
                    if (Guid.TryParse(itemId, out _))
                    {
                        ids.Add(itemId);
                    }
                }
            }
        }
        catch (Exception)
        {
            // unreadable dir — the mirror keys still count
        }

        _binaryIds = ids;
        _binaryIdsStamp = combined;
        return ids;
    }

    // The hidden-id set, cached per (index stamp, binary-disk stamp) — the
    // dressing room fires many searches per page and each one checks ids
    // against this set, so a rebuild happens only when the index re-syncs or
    // a binary lands/leaves the disk, not per request.
    private static long _hiddenStamp = -1;
    private static HashSet<string> _hiddenIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Item ids the offer and the catalog searches must hide: every
    /// live item WITHOUT a servable binary on disk — a fresh probe-dead verdict
    /// (its binary 404s), a stale verdict and an item the warm-up has not
    /// mirrored yet all behave the same for the client: the download would hang
    /// on "wczytywanie…" and then render nothing, so none of them may list.
    /// The controllers skip these ids per request, so a piece whose binary
    /// lands (warm-up progress, a manual mirror) appears without waiting for
    /// the next index re-sync. Treat the returned set as read-only — it is
    /// shared between callers until the next stamp change.
    /// A locally mirrored file in the mojang-assets primary dir (<see
    /// cref="GetLocalPersonaBinaries"/>) makes the item servable whatever an
    /// earlier upstream probe concluded.</summary>
    public static HashSet<string> GetHiddenItemIds()
    {
        lock (StateLock)
        {
            LoadProbeStates();
            HashSet<string> binaryIds = BinaryIdsSnapshot();
            long stamp = HashCode.Combine(IndexStamp, _binaryIdsStamp);
            if (stamp == _hiddenStamp)
            {
                return _hiddenIds;
            }

            var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in Index?.Items ?? [])
            {
                if (!binaryIds.Contains(entry.Id))
                {
                    hidden.Add(entry.Id);
                }
            }

            _hiddenIds = hidden;
            _hiddenStamp = stamp;
            return hidden;
        }
    }

    // Panel counter: live / dead / unchecked. A dead verdict whose binary is
    // present in the mojang-assets primary dir counts as live (mirror override,
    // same rule as GetDeadItemIds).
    public static (int Live, int Dead, int Unchecked, int CachedBinaries, int CachedThumbnails) GetProbeSummary()
    {
        lock (StateLock)
        {
            LoadProbeStates();
            Dictionary<string, string> local = GetLocalPersonaBinaries();
            int live = 0, dead = 0;
            foreach (var kv in ProbeStates)
            {
                if (IsStaleDead(kv.Value))
                {
                    continue;
                }

                if (kv.Value.State == "live" || local.ContainsKey(kv.Key))
                {
                    live++;
                }
                else if (kv.Value.State == "dead")
                {
                    dead++;
                }
            }

            int cachedBinaries = 0;
            if (Directory.Exists(BinariesDir))
            {
                cachedBinaries = Directory.EnumerateFiles(BinariesDir, "*.zip").Count();
            }

            int cachedThumbnails = 0;
            if (Directory.Exists(ThumbnailsDir))
            {
                cachedThumbnails = Directory.EnumerateFiles(ThumbnailsDir).Count();
            }

            return (live, dead, Math.Max(0, IndexedItemCount - live - dead), cachedBinaries, cachedThumbnails);
        }
    }

    private static void SaveProbeStates(ILogger logger)
    {
        try
        {
            Directory.CreateDirectory(CatalogDir);
            string tempPath = ProbeStatePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(new ProbeStateFile { Items = ProbeStates, GeometrySchemaVersion = GeometryCompatSchemaVersion, Geometry = GeometryCompatStates }, JsonOptions));
            File.Move(tempPath, ProbeStatePath, overwrite: true);
            _probeStateDirty = false;
            _probeStateLastSave = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Could not save the live persona probe state to {Path}", ProbeStatePath);
        }
    }

    // Probe state writes are per-item and can burst (a player browsing a category);
    // persist at most every 15 seconds so the bursts do not rewrite the file constantly.
    private static void MarkProbeStateDirty(ILogger logger)
    {
        _probeStateDirty = true;
        if ((DateTime.UtcNow - _probeStateLastSave).TotalSeconds >= 15)
        {
            SaveProbeStates(logger);
        }
    }

    /// <summary>Flushes a pending probe-state write (called from the controllers after bursts).</summary>
    public static void FlushProbeStates(ILogger logger)
    {
        lock (StateLock)
        {
            if (_probeStateDirty)
            {
                SaveProbeStates(logger);
            }
        }
    }

    // ---------------------------------------------------------------------------
    // Background binary warm-up: mirror every servable piece into the volume
    // ---------------------------------------------------------------------------

    // The upstream content CDN is dying (see the header). A fresh install used
    // to meet every piece cold: each une mirrored piece cost the client a
    // resolve → probe → (404 everywhere) → placeholder round-trip — seconds of
    // "wczytywanie…" per tap, and a piece that renders nothing afterwards: the
    // "most things cannot be equipped" report. This loop mirrors every piece
    // the binary path can serve (pack-selected accessories + filler pieces +
    // skins + the thematic skin packs) into the volume in the background, one
    // item at a time through the same ≤2 req/s API pacer the lazy pipeline
    // uses. Live verdicts persist in probe-state (repeat passes are cheap:
    // mirrored pieces and fresh verdicts are skipped), dead verdicts drop the
    // pieces from the offer before the client ever sees them, and still-alive
    // pieces are captured permanently before the CDN loses them too. The pack
    // gate decides the scope: accessories outside the allowed pack set are
    // refused by the binary path anyway, so warming them is pointless — when
    // the pack selection changes (ConfigStamp), the newly allowed pieces get
    // warmed on the next pass.
    private static long _warmUpIndexStamp = -1;
    private static long _warmUpGateStamp = -1;
    private static int _warmUpLoopStarted;

    /// <summary>Starts (once per process) the background loop that mirrors every
    /// servable live-catalog piece into the volume. Safe to call from any
    /// startup path; a no-op when the live catalog is disabled.</summary>
    public static void EnsureBinaryWarmUpStarted(ILogger logger)
    {
        if (!Enabled || Interlocked.Exchange(ref _warmUpLoopStarted, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() => WarmUpLoopAsync(logger));
    }

    private static async Task WarmUpLoopAsync(ILogger logger)
    {
        // let the server finish booting (staticdata load, DB open, config) before
        // the first pass competes for the network pacer
        await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        while (true)
        {
            try
            {
                long indexStamp = IndexStamp;
                long gateStamp = PersonaPackGate.ConfigStamp;
                if (IsAvailable && (indexStamp != _warmUpIndexStamp || gateStamp != _warmUpGateStamp))
                {
                    _warmUpIndexStamp = indexStamp;
                    _warmUpGateStamp = gateStamp;
                    await RunWarmUpPassAsync(logger).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "Live persona binary warm-up pass failed — the loop keeps running");
            }

            // 60 s cadence: the idle check is two long compares + (when the stamp
            // moved) candidate enumeration, so a hot loop would buy nothing
            await Task.Delay(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
        }
    }

    private static async Task RunWarmUpPassAsync(ILogger logger)
    {
        var index = Index;
        if (index is null)
        {
            return;
        }

        // everything the binary path can actually serve — accessories with a
        // theme tag inside the allowed pack set (the selected bundles), skins
        // (gate-exempt) and — while "Fill blank slots with random cosmetics"
        // is ON — EVERY accessory (the filler draws from the whole mirrored
        // catalog, so the download scope follows the same switch). Anything
        // else is refused by the pack gate before the pipeline runs. THE OFFER
        // ONLY CONTAINS PIECES WITH A BINARY ON DISK, so this pass is what
        // grows the dressing room: resolve URLs in batches (Catalog/GetItems —
        // one call per 20 ids), then download in parallel.
        IReadOnlySet<string> allowed = await PersonaPackGate.AllAllowedPackUuidsAsync(CancellationToken.None).ConfigureAwait(false);
        bool fillAll = PersonaPackGate.FillBlankSlots;
        List<string> candidates = [];
        lock (StateLock)
        {
            LoadProbeStates();
            var localMirrors = GetLocalPersonaBinaries();
            foreach (var entry in index.Items)
            {
                if (entry.IsEmote)
                {
                    continue; // never offered — the dressing room has no emote tab
                }

                string itemId = entry.Id;
                if (localMirrors.ContainsKey(itemId))
                {
                    continue; // mirrored binary present — alive with no upstream access
                }

                bool servable = PersonaPieceTypes.IsAlwaysAllowedFaceCraftPieceType(entry.PieceType)
                    || entry.PieceType == "persona_skin"
                    || fillAll
                    || (allowed.Count > 0 && ThemeTagsOf(entry).Any(allowed.Contains));
                if (!servable)
                {
                    continue;
                }

                if (FileExistsNonEmpty(BinaryPath(itemId)))
                {
                    continue; // mirrored during an earlier pass — the offer picks it up from disk
                }

                if (ProbeStates.TryGetValue(itemId, out var state) && state.State == "dead" && !IsStaleDead(state))
                {
                    continue; // freshly probed dead — nothing to fetch
                }

                candidates.Add(itemId);
            }
        }

        // thematic skin packs after the pieces (skins serve regardless of the
        // gate, the packs are the client's Classic Skins imports)
        List<string> packCandidates = [.. index.SkinPacks
            .Where(pack => !FileExistsNonEmpty(SkinPackZipPath(pack.Id)))
            .Select(pack => pack.Id)];

        if (candidates.Count == 0 && packCandidates.Count == 0)
        {
            logger.Debug("Live persona binary warm-up: nothing to do ({Indexed} indexed piece(s) all fresh)", index.Items.Count);
            return;
        }

        logger.Information("Live persona binary warm-up starting: {Pieces} piece(s) + {Packs} skin pack(s) to mirror (index stamp {Stamp})",
            candidates.Count, packCandidates.Count, IndexStamp);

        // STREAMING pipeline: resolve one GetItems chunk (20 ids, paced like
        // every API call; a batch that fails — one stale republication id in
        // the set, the endpoint is all-or-nothing — bisects down to singles)
        // and IMMEDIATELY dispatch its downloads before moving to the next
        // chunk. The first resolve-all-then-download-all design delayed the
        // FIRST binary by the whole resolve phase (minutes to hours with the
        // paced singles fallback), which read as "why are so few binaries
        // there" while the pass was still resolving. Here binaries start
        // landing within the first minute and the offer grows continuously.
        int live = 0, dead = 0, processed = 0;
        var downloadGate = new SemaphoreSlim(6, 6);
        var downloads = new List<Task>(candidates.Count);
        for (int offset = 0; offset < candidates.Count; offset += 20)
        {
            List<string> chunk = candidates.Skip(offset).Take(20).ToList();
            foreach (var (itemId, url) in await BatchResolveBinaryUrlsAsync(chunk, logger, CancellationToken.None).ConfigureAwait(false))
            {
                downloads.Add(DownloadAndCountAsync(itemId, url));
            }
        }

        await Task.WhenAll(downloads).ConfigureAwait(false);

        async Task DownloadAndCountAsync(string itemId, string? url)
        {
            bool ok;
            if (url is null)
            {
                RecordProbe(itemId, "dead", null, "no-contents", logger);
                ok = false;
            }
            else
            {
                ok = await WarmUpDownloadAsync(itemId, url, logger, CancellationToken.None).ConfigureAwait(false);
            }

            await downloadGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                int done = Interlocked.Increment(ref processed);
                if (ok)
                {
                    Interlocked.Increment(ref live);
                }
                else
                {
                    Interlocked.Increment(ref dead);
                }

                if (done % 50 == 0)
                {
                    logger.Information("Live persona binary warm-up: {Processed}/{Total} — {Live} mirrored, {Dead} dead",
                        done, candidates.Count, Interlocked.CompareExchange(ref live, 0, 0), Interlocked.CompareExchange(ref dead, 0, 0));
                    FlushProbeStates(logger);
                }
            }
            finally
            {
                downloadGate.Release();
            }
        }

        foreach (string offerId in packCandidates)
        {
            bool ok = await TryEnsureSkinPackZipAsync(offerId, logger, CancellationToken.None).ConfigureAwait(false);
            if (ok)
            {
                live++;
            }
            else
            {
                dead++;
            }
        }

        FlushProbeStates(logger);
        logger.Information("Live persona binary warm-up pass finished: {Processed} piece(s) + {Packs} pack(s) — {Live} mirrored, {Dead} dead/unavailable",
            processed, packCandidates.Count, live, dead);
    }

    /// <summary>Resolves personabinary URLs for many items at once via
    /// Catalog/GetItems (Search never returns Contents; GetItems does — the
    /// batch form of the per-item GetItem). Chunks of 20, one paced API call
    /// each. GetItems is all-or-nothing on unknown ids, so a failing chunk
    /// bisects recursively; a single-id chunk falls back to the retrying
    /// per-item resolve. Returns (itemId → url or null for confirmed
    /// no-contents) for every requested id.</summary>
    private static async Task<IReadOnlyList<(string Id, string? Url)>> BatchResolveBinaryUrlsAsync(
        IReadOnlyList<string> itemIds, ILogger logger, CancellationToken cancellationToken)
    {
        var results = new List<(string Id, string? Url)>(itemIds.Count);

        async Task ResolveChunkAsync(IReadOnlyList<string> chunk)
        {
            if (chunk.Count == 1)
            {
                results.Add((chunk[0], await ResolveBinaryUrlAsync(chunk[0], logger, cancellationToken).ConfigureAwait(false)));
                return;
            }

            try
            {
                string token = await GetMasterTokenAsync(logger, cancellationToken).ConfigureAwait(false);
                await PaceRemoteAsync(cancellationToken).ConfigureAwait(false);
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{PlayFabApi}/Catalog/GetItems")
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new { Ids = chunk, Scid = Scid }),
                        System.Text.Encoding.UTF8, "application/json"),
                };
                request.Headers.Add("X-EntityToken", token);
                using var response = await HttpClient.SendAsync(request, cancellationToken);
                if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                {
                    InvalidateMasterToken();
                    throw new HttpRequestException($"auth rejected with HTTP {(int)response.StatusCode}");
                }

                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                var byId = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        string? id = item.TryGetProperty("Id", out var idProp) ? idProp.GetString() : null;
                        string? url = null;
                        if (item.TryGetProperty("Contents", out var contents) && contents.ValueKind == JsonValueKind.Array && contents.GetArrayLength() > 0)
                        {
                            url = contents[0].TryGetProperty("Url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String ? urlProp.GetString() : null;
                        }

                        if (id is not null)
                        {
                            byId[id] = string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? null : url;
                        }
                    }
                }

                foreach (string id in chunk)
                {
                    results.Add((id, byId.GetValueOrDefault(id)));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // all-or-nothing: one unknown id fails the whole chunk — bisect
                logger.Debug("Live persona batch resolve of {Count} item(s) failed ({Reason}) — bisecting", chunk.Count, ex.Message);
                int mid = chunk.Count / 2;
                await ResolveChunkAsync(chunk.Take(mid).ToList()).ConfigureAwait(false);
                await ResolveChunkAsync(chunk.Skip(mid).ToList()).ConfigureAwait(false);
            }
        }

        const int BatchSize = 20;
        for (int offset = 0; offset < itemIds.Count; offset += BatchSize)
        {
            await ResolveChunkAsync(itemIds.Skip(offset).Take(BatchSize).ToList()).ConfigureAwait(false);
        }

        return results;
    }

    // The warm-up's download half: resolve is already done, so this just fetches
    // the binary (no HEAD probe — the GET result IS the verdict) and records it.
    // Runs under the per-item lock so a client request for the same piece never
    // races this (the client's lazy pipeline wins the lock and serves it).
    private static async Task<bool> WarmUpDownloadAsync(string itemId, string url, ILogger logger, CancellationToken cancellationToken)
    {
        var gate = GetItemLock(itemId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = BinaryPath(itemId);
            if (FileExistsNonEmpty(path))
            {
                return true; // a client request or an earlier download got it first
            }

            if (!_binariesLayoutCreated)
            {
                Directory.CreateDirectory(BinariesDir);
                _binariesLayoutCreated = true;
            }

            string partPath = path + ".part";
            Exception? lastError = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    // the resolved URL can point at the DEAD xforge CDN while
                    // the same GUID still lives on the PlayFab content CDN —
                    // the lazy client path probes the alternates, so the
                    // warm-up download must too (skipping them marked whole
                    // swaths of the catalog dead that were actually alive —
                    // the "why are there so few binaries" report)
                    foreach (string candidateUrl in CandidateDownloadUrls(url))
                    {
                        using var response = await HttpClient.GetAsync(candidateUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
                        {
                            continue; // try the next CDN shape before concluding dead
                        }

                        if (!response.IsSuccessStatusCode)
                        {
                            lastError = new HttpRequestException($"HTTP {(int)response.StatusCode}");
                            break;
                        }

                        await using (var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                        await using (var fileStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                        {
                            await content.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                        }

                        File.Move(partPath, path, overwrite: true);
                        MarkBinaryLanded();
                        RecordProbe(itemId, "live", candidateUrl, "warmup", logger);
                        EnsureGeometryCompatCaptured(itemId, logger, path);
                        return true;
                    }

                    // every CDN shape answered 404/410 — definitive, no retry
                    RecordProbe(itemId, "dead", url, "warmup-404", logger);
                    return false;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // transport-level failure — the verdict stays whatever it
                    // is, so the piece retries on the next pass
                    lastError = ex;
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken).ConfigureAwait(false);
                }
            }

            logger.Debug("Live persona warm-up download failed for {Item} from {Url} after all retries: {Reason}", itemId, url, lastError?.Message);
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    // The resolved URL first, then the same payload re-hosted on the PlayFab
    // content CDN (content1/content2 — the AlternateCdnUrls shapes the lazy
    // probe walks). Deduplicated to skip a re-GET of the same address.
    private static IEnumerable<string> CandidateDownloadUrls(string url)
    {
        yield return url;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { url };
        foreach (string alternate in AlternateCdnUrls(url))
        {
            if (seen.Add(alternate))
            {
                yield return alternate;
            }
        }
    }

    // ---------------------------------------------------------------------------
    // Remote access: pacer + anonymous PlayFab login + URL discovery
    // ---------------------------------------------------------------------------

    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly SemaphoreSlim RemoteGate = new(1, 1);

    // Raw concurrency cap for CDN file downloads (thumbnails) — the polite
    // alternative to the API pacer for content-CDN GETs: a cold dressing room
    // asks for a whole page of thumbnails at once and they download in
    // parallel (bounded) instead of two-per-second.
    private static readonly SemaphoreSlim ThumbDownloadSlot = new(12, 12);
    private static DateTime _lastRemoteCall = DateTime.MinValue;
    private static readonly SemaphoreSlim LoginLock = new(1, 1);

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        _ = client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "ApaceProject/Apace live-persona-catalog");
        return client;
    }

    // Polite limiter for the PlayFab API surface this service calls (probe,
    // GetItem discovery): at most ~2 requests per second. CDN FILE downloads
    // (thumbnails, binaries) do NOT go through this — they hit the content CDN,
    // not the API, and pacing them at 2/s serialized a cold dressing room's
    // whole page of thumbnails (60+ images × 0.5s each = every first visit
    // crawled). See ThumbDownloadSlot for the thumbnail concurrency cap.
    private static async Task PaceRemoteAsync(CancellationToken cancellationToken)
    {
        await RemoteGate.WaitAsync(cancellationToken);
        try
        {
            TimeSpan wait = TimeSpan.FromMilliseconds(500) - (DateTime.UtcNow - _lastRemoteCall);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken);
            }

            _lastRemoteCall = DateTime.UtcNow;
        }
        finally
        {
            RemoteGate.Release();
        }
    }

    private static string? _masterToken;
    private static DateTimeOffset _masterTokenIssued;

    // Anonymous AndroidDeviceID login (same flow as scripts/fetch_persona_catalog.py):
    // client entity token exchanged for a master_player_account token, the only kind
    // Catalog/Search and Catalog/GetItem accept. Re-issued after 12h (PlayFab entity
    // tokens live ~24h) or when PlayFab answers with an auth error.
    private static async Task<string> GetMasterTokenAsync(ILogger logger, CancellationToken cancellationToken)
    {
        if (_masterToken is not null && DateTimeOffset.UtcNow - _masterTokenIssued < TimeSpan.FromHours(12))
        {
            return _masterToken;
        }

        await LoginLock.WaitAsync(cancellationToken);
        try
        {
            if (_masterToken is not null && DateTimeOffset.UtcNow - _masterTokenIssued < TimeSpan.FromHours(12))
            {
                return _masterToken;
            }

            await PaceRemoteAsync(cancellationToken);
            var loginBody = JsonSerializer.Serialize(new
            {
                TitleId = "20CA2",
                AndroidDeviceId = $"apace-{Environment.MachineName}",
                AndroidDevice = "Apace",
                OS = "Linux",
                CreateAccount = true,
            });
            using var loginResponse = await HttpClient.PostAsync(
                $"{PlayFabApi}/Client/LoginWithAndroidDeviceID",
                new StringContent(loginBody, System.Text.Encoding.UTF8, "application/json"), cancellationToken);
            loginResponse.EnsureSuccessStatusCode();
            using var loginDoc = JsonDocument.Parse(await loginResponse.Content.ReadAsStringAsync(cancellationToken));
            string clientToken = loginDoc.RootElement.GetProperty("data").GetProperty("EntityToken").GetProperty("EntityToken").GetString()
                ?? throw new InvalidOperationException("login returned no entity token");
            string playFabId = loginDoc.RootElement.GetProperty("data").GetProperty("PlayFabId").GetString() ?? "";

            await PaceRemoteAsync(cancellationToken);
            var exchangeBody = JsonSerializer.Serialize(new
            {
                Entity = new { Id = playFabId, Type = "master_player_account" },
            });
            using var exchangeRequest = new HttpRequestMessage(HttpMethod.Post, $"{PlayFabApi}/Authentication/GetEntityToken")
            {
                Content = new StringContent(exchangeBody, System.Text.Encoding.UTF8, "application/json"),
            };
            exchangeRequest.Headers.Add("X-EntityToken", clientToken);
            using var exchangeResponse = await HttpClient.SendAsync(exchangeRequest, cancellationToken);
            exchangeResponse.EnsureSuccessStatusCode();
            using var exchangeDoc = JsonDocument.Parse(await exchangeResponse.Content.ReadAsStringAsync(cancellationToken));
            _masterToken = exchangeDoc.RootElement.GetProperty("data").GetProperty("EntityToken").GetRawText().Trim('"');
            _masterTokenIssued = DateTimeOffset.UtcNow;
            logger.Information("Live persona catalog: logged in to PlayFab 20ca2 anonymously ({PlayFabId})", playFabId);
            return _masterToken;
        }
        finally
        {
            LoginLock.Release();
        }
    }

    private static void InvalidateMasterToken() => _masterToken = null;

    /// <summary>
    /// Catalog/GetItem — the only endpoint that returns the real personabinary
    /// Contents entry (Search never does). Returns Contents[0].Url or null.
    /// </summary>
    public static async Task<string?> ResolveBinaryUrlAsync(string itemId, ILogger logger, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                string token = await GetMasterTokenAsync(logger, cancellationToken);
                await PaceRemoteAsync(cancellationToken);
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{PlayFabApi}/Catalog/GetItem")
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new { Id = itemId, Scid = Scid }),
                        System.Text.Encoding.UTF8, "application/json"),
                };
                request.Headers.Add("X-EntityToken", token);
                using var response = await HttpClient.SendAsync(request, cancellationToken);
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    InvalidateMasterToken();
                    throw new HttpRequestException($"auth rejected with HTTP {(int)response.StatusCode}");
                }

                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (!doc.RootElement.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("Item", out var item) ||
                    !item.TryGetProperty("Contents", out var contents) ||
                    contents.ValueKind != JsonValueKind.Array ||
                    contents.GetArrayLength() == 0)
                {
                    return null; // no contents at all — the item has no downloadable binary
                }

                string? url = contents[0].TryGetProperty("Url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String
                    ? urlProp.GetString()
                    : null;
                return string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? null : url;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < 3)
            {
                logger.Warning("Live persona catalog GetItem attempt {Attempt}/3 failed for {Item}: {Reason}", attempt, itemId, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken);
            }
        }
    }

    /// <summary>The same payload on the PlayFab content CDN (content1/content2).</summary>
    public static IEnumerable<string> AlternateCdnUrls(string url)
    {
        static bool Guidish(string segment)
            => segment.Length is >= 32 and <= 36 && segment.All(c => char.IsAsciiHexDigit(c) || c == '-');

        if (url.Contains("/pf-namespace-", StringComparison.Ordinal))
        {
            string tail = url.Split("/pf-namespace-", 2)[1];
            if (tail.Length == 0 || tail.Contains("://", StringComparison.Ordinal))
            {
                yield break;
            }

            foreach (string host in AlternateCdnHosts)
            {
                yield return $"https://{host}/pf-namespace-{tail}";
            }
        }
        else if (url.Contains("/pf-title-", StringComparison.Ordinal))
        {
            string[] segments = [.. url.Split('/', StringSplitOptions.RemoveEmptyEntries)];
            if (segments.Length >= 2 && Guidish(segments[^2]))
            {
                string tail = $"{segments[^2]}/{segments[^1]}";
                foreach (string host in AlternateCdnHosts)
                {
                    yield return $"https://{host}/pf-namespace-{MceNamespace}/{tail}";
                }
            }
        }
    }

    // HEAD (fallback GET range 0-0) — the xforge/content CDNs do not always honour
    // HEAD, so the range GET is the authoritative fallback.
    private static async Task<int?> ProbeUrlAsync(string url, CancellationToken cancellationToken)
    {
        await PaceRemoteAsync(cancellationToken);
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
        try
        {
            using var headResponse = await HttpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return (int)headResponse.StatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // HEAD not honoured / transport error — retry as a 1-byte GET
        }

        try
        {
            using var getRequest = new HttpRequestMessage(HttpMethod.Get, url);
            getRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var getResponse = await HttpClient.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return (int)getResponse.StatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null; // unreachable
        }
    }

    // ---------------------------------------------------------------------------
    // Lazy binary ensure (PersonaCdnController miss path)
    // ---------------------------------------------------------------------------

    private static readonly Dictionary<string, SemaphoreSlim> ItemLocks = [];
    private static bool _binariesLayoutCreated;

    private static SemaphoreSlim GetItemLock(string itemId)
    {
        lock (StateLock)
        {
            if (!ItemLocks.TryGetValue(itemId, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                ItemLocks[itemId] = gate;
            }

            return gate;
        }
    }

    public static string BinaryPath(string itemId) => Path.Combine(BinariesDir, $"{itemId}.zip");

    /// <summary>
    /// The mojang-assets primary persona path (mirror-backed legacy binaries land
    /// here when the volume carries them):
    /// <c>&lt;MojangAssetsDir&gt;/playfab/persona/&lt;itemId&gt;.zip</c>. PersonaCdnController
    /// checks this dir before the lazy pipeline, and a dead probe verdict from
    /// before the mirror landed must not keep such a piece hidden.
    /// </summary>
    public static string LocalPersonaPath(string itemId) => Path.Combine(Program.MojangAssetsDir, "playfab", "persona", $"{itemId}.zip");

    // item id → file path of persona zips present in the mojang-assets primary
    // dir. Recomputed only when the directory changes (one stat per check):
    // binaries land there lazily (panel file management / mirror restores can
    // happen long after any probe verdict), so this map is the dead→alive bridge.
    private static long _localPersonaStamp = -1;
    private static readonly Dictionary<string, string> LocalPersonaBinaries = new(StringComparer.OrdinalIgnoreCase);

    // Caller must hold StateLock.
    private static Dictionary<string, string> GetLocalPersonaBinaries()
    {
        string dir = Path.Combine(Program.MojangAssetsDir, "playfab", "persona");
        long stamp;
        try
        {
            stamp = Directory.Exists(dir) ? Directory.GetLastWriteTimeUtc(dir).ToUniversalTime().Ticks : 0;
        }
        catch (Exception)
        {
            stamp = 0;
        }

        if (stamp == _localPersonaStamp)
        {
            return LocalPersonaBinaries;
        }

        _localPersonaStamp = stamp;
        LocalPersonaBinaries.Clear();
        try
        {
            if (Directory.Exists(dir))
            {
                foreach (string file in Directory.EnumerateFiles(dir, "*.zip"))
                {
                    string itemId = Path.GetFileNameWithoutExtension(file);
                    if (Guid.TryParse(itemId, out _))
                    {
                        LocalPersonaBinaries[itemId] = file;
                    }
                }
            }
        }
        catch (Exception)
        {
            // unreadable dir — no local overrides this round
        }

        return LocalPersonaBinaries;
    }

    private static bool FileExistsNonEmpty(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static readonly HashSet<string> PackGateRefusalsLogged = [];

    // One line per refused item id per run (same dedup as
    // PersonaPieceTypes.LogCompatVerdictOnce) — the client retries the same
    // binary, the refusal must not flood the log.
    private static void LogPackGateRefusalOnce(string itemId, string reason, ILogger logger)
    {
        lock (PackGateRefusalsLogged)
        {
            if (!PackGateRefusalsLogged.Add(itemId))
            {
                return;
            }
        }

        logger.Information("Pack gate refuses live accessory {Item}: {Reason} — binaries of accessories outside the selected packs are never fetched", itemId, reason);
    }

    /// <summary>
    /// The client asked for a live piece binary: serve from the mojang-assets
    /// mirror dir or the volume cache, else run the lazy pipeline — resolve the
    /// URL (GetItem), probe it (404 → alternate CDN shapes → dead), download to
    /// the volume cache. Live accessories outside the selected packs are refused
    /// on the lazy path regardless of creator (skins exempt — see PersonaPackGate).
    /// Returns false when the item is dead, is refused by the pack gate, or the
    /// pipeline failed after all retries — never throws (except on cancellation):
    /// the controller answers the deterministic placeholder so the frozen client's
    /// download retry loop terminates with an importable zip instead of an error
    /// answer that keeps the piece stuck on "loading" forever. A pipeline failure
    /// leaves the probe verdict untouched, so a transient CDN hiccup recovers on
    /// the next request.
    /// </summary>
    public static async Task<bool> TryEnsureBinaryAsync(string itemId, ILogger logger, CancellationToken cancellationToken)
    {
        var gate = GetItemLock(itemId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            // mirror-first: a mirrored binary can appear in
            // <mojang-assets>/playfab/persona/<itemId>.zip long AFTER an upstream probe
            // declared the item dead — a locally mirrored binary makes the item ALIVE
            // again with no upstream access, overriding any stale verdict (RecordProbe
            // refreshes it so the offer and the panel counters follow).
            string? localMirrorPath = null;
            lock (StateLock)
            {
                LoadProbeStates();
                if (GetLocalPersonaBinaries().TryGetValue(itemId, out string? mirrorPath) && FileExistsNonEmpty(mirrorPath))
                {
                    localMirrorPath = mirrorPath;
                    if (!ProbeStates.TryGetValue(itemId, out var mirrored) || mirrored.State != "live")
                    {
                        RecordProbe(itemId, "live", null, "local", logger);
                        logger.Information("Live persona piece {Item} is alive locally — mirrored binary present in {Path}, no upstream probe needed", itemId, localMirrorPath);
                    }
                }
            }

            if (localMirrorPath is not null)
            {
                ImportLocalBinary(itemId, localMirrorPath, logger);
                EnsureGeometryCompatCaptured(itemId, logger, localMirrorPath, BinaryPath(itemId));
                return true;
            }

            // pack gate for live accessories (PersonaCdnController has already served
            // the mirror/staticdata dirs — pre-existing files and customs are never
            // gated): a live piece that is not a skin downloads only when its theme
            // tags intersect the allowed set (defaults ∪ every player's grants) —
            // an empty set or no matching tag refuses, tagless accessories included
            // and creatorless entries gated like any other accessory. Skins
            // (persona_skin) are exempt — and so is every face-craft category
            // (eyes, hair, mouth, facial hair, face items): always allowed, never
            // gated (PersonaPieceTypes.IsAlwaysAllowedFaceCraftPieceType).
            if (GetIndexEntry(itemId) is { } entry
                && entry.PieceType is not "persona_skin"
                && !PersonaPieceTypes.IsAlwaysAllowedFaceCraftPieceType(entry.PieceType))
            {
                IReadOnlySet<string> allowed = await PersonaPackGate.AllAllowedPackUuidsAsync(cancellationToken);
                // filler pieces were OFFERED to the player (CatalogController
                // mirrors its pool into PersonaPackGate.FillerExemptItemIds) —
                // their binaries download like any offered piece; gating them
                // here again would force every filler piece outside the
                // selected packs onto the gray placeholder stub
                if (!PersonaPackGate.FillerExemptItemIds.Contains(itemId)
                    && (allowed.Count == 0 || !ThemeTagsOf(entry).Any(allowed.Contains)))
                {
                    LogPackGateRefusalOnce(itemId, allowed.Count == 0 ? "no packs selected" : "theme tags outside the selected packs", logger);
                    return false;
                }
            }

            string path = BinaryPath(itemId);
            if (ProbeStates.TryGetValue(itemId, out var state))
            {
                if (state.State == "live" && FileExistsNonEmpty(path))
                {
                    EnsureGeometryCompatCaptured(itemId, logger, path);
                    return true; // cached from an earlier request
                }

                if (state.State == "dead" && !IsStaleDead(state))
                {
                    return false; // freshly probed dead — nothing to fetch
                }
            }

            if (!_binariesLayoutCreated)
            {
                Directory.CreateDirectory(BinariesDir);
                _binariesLayoutCreated = true;
            }

            // 1. resolve the real URL (Search never returns Contents; GetItem does)
            string? url = await ResolveBinaryUrlAsync(itemId, logger, cancellationToken);
            if (url is null)
            {
                RecordProbe(itemId, "dead", null, "no-contents", logger);
                return false;
            }

            // 2. probe: primary first, then the same GUID on the new content CDN
            int? status = await ProbeUrlAsync(url, cancellationToken);
            string source = "primary";
            string resolved = url;
            if (status is not (200 or 206))
            {
                foreach (string alternate in AlternateCdnUrls(url))
                {
                    int? alternateStatus = await ProbeUrlAsync(alternate, cancellationToken);
                    if (alternateStatus is 200 or 206)
                    {
                        resolved = alternate;
                        source = alternate.Split('/')[2];
                        status = alternateStatus;
                        break;
                    }
                }
            }

            if (status is not (200 or 206))
            {
                RecordProbe(itemId, "dead", url, $"probe-{status ?? 0}", logger);
                logger.Information("Live persona piece {Item} probed dead (primary {Primary}, alternates tried) — dropped from the offer until it revives",
                    itemId, status ?? 0);
                return false;
            }

            RecordProbe(itemId, "live", resolved, source, logger);

            // 3. download into the volume cache (retry/backoff pacer shared with the probes)
            await DownloadBinaryAsync(resolved, path, logger, cancellationToken);
            logger.Information("Live persona piece {Item} downloaded from {Source} into the volume cache", itemId, source);
            EnsureGeometryCompatCaptured(itemId, logger, path);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // the caller (client / shutdown) walked away — nothing to serve or record
        }
        catch (Exception ex)
        {
            // all retries exhausted (GetItem resolve, binary download): degrade to
            // "not ensured" — the controller serves the deterministic placeholder
            // instead of an error answer, which would keep the frozen client stuck
            // on "loading" (it retries a failed piece download until its own
            // timeout). The probe verdict stays whatever it is — "live" without a
            // file re-runs the pipeline on the next request, so a transient CDN
            // hiccup still recovers.
            logger.Warning(ex, "Live persona fetch failed for {Item} after all retries — the controller serves the placeholder piece instead of an error", itemId);
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    // Warm the binaries cache from the mojang-assets mirror copy so every caller
    // that resolves BinaryPath afterwards (and the panel's cached-binaries counter)
    // sees the piece through the same path upstream downloads land in. Best effort:
    // the primary-dir copy stays the servable source even when the import fails
    // (PersonaCdnController checks that dir first anyway). Runs under the item gate.
    private static void ImportLocalBinary(string itemId, string sourcePath, ILogger logger)
    {
        try
        {
            string destPath = BinaryPath(itemId);
            if (FileExistsNonEmpty(destPath))
            {
                return;
            }

            Directory.CreateDirectory(BinariesDir);
            File.Copy(sourcePath, destPath, overwrite: true);
            MarkBinaryLanded();
            logger.Debug("Live persona piece {Item} imported from {Source} into the binaries cache", itemId, sourcePath);
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Live persona piece {Item} could not be imported into the binaries cache — the mojang-assets copy stays the servable source", itemId);
        }
    }

    private static void RecordProbe(string itemId, string state, string? resolvedUrl, string source, ILogger logger)
    {
        lock (StateLock)
        {
            ProbeStates[itemId] = new ProbeEntry(state, resolvedUrl, DateTimeOffset.UtcNow, source);
            MarkProbeStateDirty(logger);
        }
    }

    private static async Task DownloadBinaryAsync(string url, string destPath, ILogger logger, CancellationToken cancellationToken)
    {
        string partPath = destPath + ".part";
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await PaceRemoteAsync(cancellationToken);
                using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using (var content = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var fileStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await content.CopyToAsync(fileStream, cancellationToken);
                }

                File.Move(partPath, destPath, overwrite: true);
                MarkBinaryLanded();
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < 3)
            {
                logger.Warning("Live persona binary download attempt {Attempt}/3 failed from {Url}: {Reason}", attempt, url, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken);
            }
        }

        // 3 attempts exhausted — the exception from the last attempt propagates
        // to TryEnsureBinaryAsync, which degrades to false so the controller
        // answers the deterministic placeholder; the probe verdict stays live so
        // a transient CDN hiccup does not kill the item (the next request retries).
    }

    /// <summary>The resolved URL for a live item, if it already probed alive.</summary>
    public static string? GetResolvedUrl(string itemId)
    {
        lock (StateLock)
        {
            LoadProbeStates();
            return ProbeStates.TryGetValue(itemId, out var entry) && entry.State == "live" ? entry.ResolvedUrl : null;
        }
    }

    // ---------------------------------------------------------------------------
    // Lazy skin pack zip ensure (SkinPackCdnController miss path)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// The servable skin pack zip path — the same dir the panel's MojangSkinPackBuilder
    /// writes the committed packs into and SkinPackCdnController serves:
    /// <c>&lt;MojangAssetsDir&gt;/playfab/skinpacks/&lt;offerId&gt;.zip</c>.
    /// </summary>
    public static string SkinPackZipPath(string offerId) => Path.Combine(Program.MojangAssetsDir, "playfab", "skinpacks", $"{offerId}.zip");

    /// <summary>
    /// The client asked for a live skin pack zip (the offer's skinbinary Contents
    /// entry points at <see cref="SkinPackZipPath"/>): serve from the volume when
    /// present (built locally or cached from an earlier request), else run the lazy
    /// pipeline — resolve the real URL (Catalog/GetItem; Search never returns
    /// Contents), probe it (404 → alternate CDN shapes), download into the volume.
    /// Returns false when the pack is not a live catalog bundle or nothing answers
    /// upstream — the controller then answers 404. Unlike persona pieces a failed
    /// pack stays in the offer (no probe-state entry): the next client request
    /// retries, paced like every remote call here, and self-heals when Mojang
    /// re-hosts the pack.
    /// </summary>
    public static async Task<bool> TryEnsureSkinPackZipAsync(string offerId, ILogger logger, CancellationToken cancellationToken)
    {
        var index = Index;
        LiveSkinPack? pack = index?.SkinPacks.FirstOrDefault(entry => string.Equals(entry.Id, offerId, StringComparison.OrdinalIgnoreCase));
        if (pack is null)
        {
            return false; // not a live catalog skin pack (the committed packs are files already)
        }

        var gate = GetItemLock($"skinpack:{offerId}");
        await gate.WaitAsync(cancellationToken);
        try
        {
            string path = SkinPackZipPath(offerId);
            if (FileExistsNonEmpty(path))
            {
                return true; // cached from an earlier request
            }

            // 1. resolve the real URL (Search never returns Contents; GetItem does)
            string? url = await ResolveBinaryUrlAsync(offerId, logger, cancellationToken);
            if (url is null)
            {
                logger.Information("Live skin pack {Pack} ({Title}) has no downloadable content upstream — the client cannot import it", offerId, pack.Title);
                return false;
            }

            // 2. probe: primary first, then the same GUID on the new content CDN
            int? status = await ProbeUrlAsync(url, cancellationToken);
            string resolved = url;
            if (status is not (200 or 206))
            {
                foreach (string alternate in AlternateCdnUrls(url))
                {
                    int? alternateStatus = await ProbeUrlAsync(alternate, cancellationToken);
                    if (alternateStatus is 200 or 206)
                    {
                        resolved = alternate;
                        status = alternateStatus;
                        break;
                    }
                }
            }

            if (status is not (200 or 206))
            {
                logger.Information("Live skin pack {Pack} ({Title}) probed dead (primary {Primary}, alternates tried) — the client cannot import it until a source revives",
                    offerId, pack.Title, status ?? 0);
                return false;
            }

            // 3. download into the servable volume dir (retry/backoff like the persona binaries)
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await DownloadBinaryAsync(resolved, path, logger, cancellationToken);
            logger.Information("Live skin pack {Pack} ({Title}) downloaded from {Source} into the volume cache",
                offerId, pack.Title, resolved.Split('/')[2]);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    // ---------------------------------------------------------------------------
    // Geometry compat capture (frozen-MCE-client compatibility facts)
    // ---------------------------------------------------------------------------

    // The MCE client froze in January 2021: piece binaries using geometry
    // format_version >= 1.16.0 or animated pieces render broken (floating parts).
    // Whenever a binary is downloaded or first served from disk we open the zip
    // once and record the facts in probe-state.json (GeometryCompatInfo, parallel
    // "geometry" dictionary) — CatalogController consumes them via
    // TryGetGeometryCompat. The inspection is WORST-CASE-WINS: real pieces carry
    // one geometry per body size (tall/medium/small/smaller) and every candidate
    // is parsed — a single risky variant marks the piece (the old first-wins scan
    // let a modern variant hide behind the first one read). Lazy and cheap: a
    // binary is parsed at most once per schema version, and files already on
    // disk are picked up on the first touch of their item.
    //
    // Schema of the recorded facts — bump whenever InspectCompatEntries changes
    // what it records so the previous file is discarded (v1: first-wins scan
    // without render-controller / face-bone capture, and the nested ppack0.zip
    // content was unreadable after collection — v2: worst-case aggregation
    // across all geometry candidates + the full effect scans on both zip layers —
    // v3: geometry detected by CONTENT (any .json holding a geometry structure,
    // lenient parse) instead of the *.geometry.json name filter, which missed
    // .geo.json / custom-named geometries and recorded all-unknown facts for
    // perfectly parseable pieces — v4: contents.json decryption: real
    // marketplace pieces are per-file encrypted (meta/geometry/textures carry
    // keys in the encrypted contents.json), so every scan before v3 read
    // ciphertext and recorded all-unknown facts for most real pieces).
    // v5: visible_bounds + custom rig bones recorded — the displaced-render
    // gate for head-family pieces ("Squid head" and friends). Bumping the
    // schema discards every stale fact file so the new facts are re-captured
    // from the binaries on disk.
    // v6: texture binding recorded — a piece whose meta geometry_sources
    // reference a texture file that no zip entry carries renders UNTEXTURED
    // ("losing textures"); recorded as positive evidence only.
    private const int GeometryCompatSchemaVersion = 6;

    private const int MaxCompatEntryBytes = 2 * 1024 * 1024;

    // Bedrock resource JSON is lenient in the wild: // comments and trailing
    // commas are common, and the strict parser threw on them — hiding perfectly
    // parseable geometries behind all-unknown facts. Parse leniently everywhere
    // in the probe.
    private static readonly JsonDocumentOptions LenientJson = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Geometry compatibility facts for a piece binary — worst-case geometry
    /// format_version (highest/lowest across the piece's variants), animation,
    /// particle, render-controller and face-bone presence — captured when the
    /// binary is downloaded or first served, and lazily for binaries already on
    /// disk that no request has touched yet. Returns false when nothing is known
    /// for the item (never seen on disk, or the zip could not be parsed).
    /// </summary>
    public static bool TryGetGeometryCompat(string itemId, out string? formatVersion, out string? lowestFormatVersion, out bool? hasAnimations, out bool? hasParticles, out bool? hasRenderControllers, out bool? hasFaceBones, out float? maxVisibleBoundsWidth, out float? maxVisibleBoundsHeight, out bool? hasNonstandardBones, out bool? hasTextureBindingIssue)
    {
        lock (StateLock)
        {
            LoadProbeStates();
            if (GeometryCompatStates.TryGetValue(itemId, out GeometryCompatInfo? known))
            {
                formatVersion = known.FormatVersion;
                lowestFormatVersion = known.LowestFormatVersion;
                hasAnimations = known.HasAnimations;
                hasParticles = known.HasParticles;
                hasRenderControllers = known.HasRenderControllers;
                hasFaceBones = known.HasFaceBones;
                maxVisibleBoundsWidth = known.MaxVisibleBoundsWidth;
                maxVisibleBoundsHeight = known.MaxVisibleBoundsHeight;
                hasNonstandardBones = known.HasNonstandardBones;
                hasTextureBindingIssue = known.HasTextureBindingIssue;
                return formatVersion is not null || lowestFormatVersion is not null
                    || known.HasAnimations is not null || known.HasParticles is not null
                    || known.HasRenderControllers is not null || known.HasFaceBones is not null
                    || known.MaxVisibleBoundsWidth is not null || known.MaxVisibleBoundsHeight is not null
                    || known.HasNonstandardBones is not null || known.HasTextureBindingIssue is not null;
            }
        }

        // lazy capture: a binary already on disk (mirror copy or cache)
        // that nothing has touched yet — inspect it on the first ask
        string? path = null;
        lock (StateLock)
        {
            if (GetLocalPersonaBinaries().TryGetValue(itemId, out string? localPath) && FileExistsNonEmpty(localPath))
            {
                path = localPath;
            }
        }

        if (path is null)
        {
            string cached = BinaryPath(itemId);
            if (FileExistsNonEmpty(cached))
            {
                path = cached;
            }
        }

        if (path is not null)
        {
            EnsureGeometryCompatCaptured(itemId, Log.Logger, path);
            lock (StateLock)
            {
                if (GeometryCompatStates.TryGetValue(itemId, out GeometryCompatInfo? captured))
                {
                    formatVersion = captured.FormatVersion;
                    lowestFormatVersion = captured.LowestFormatVersion;
                    hasAnimations = captured.HasAnimations;
                    hasParticles = captured.HasParticles;
                    hasRenderControllers = captured.HasRenderControllers;
                    hasFaceBones = captured.HasFaceBones;
                    maxVisibleBoundsWidth = captured.MaxVisibleBoundsWidth;
                    maxVisibleBoundsHeight = captured.MaxVisibleBoundsHeight;
                    hasNonstandardBones = captured.HasNonstandardBones;
                    hasTextureBindingIssue = captured.HasTextureBindingIssue;
                    return formatVersion is not null || lowestFormatVersion is not null
                        || captured.HasAnimations is not null || captured.HasParticles is not null
                        || captured.HasRenderControllers is not null || captured.HasFaceBones is not null
                        || captured.MaxVisibleBoundsWidth is not null || captured.MaxVisibleBoundsHeight is not null
                        || captured.HasNonstandardBones is not null || captured.HasTextureBindingIssue is not null;
                }
            }
        }

        formatVersion = null;
        lowestFormatVersion = null;
        hasAnimations = null;
        hasParticles = null;
        hasRenderControllers = null;
        hasFaceBones = null;
        maxVisibleBoundsWidth = null;
        maxVisibleBoundsHeight = null;
        hasNonstandardBones = null;
        hasTextureBindingIssue = null;
        return false;
    }

    /// <summary>
    /// Captures the geometry compat facts for a piece binary once — the first
    /// candidate path that exists wins. An unparseable zip is cached as an
    /// all-unknown fact so it is not re-parsed on every request;
    /// <see cref="TryGetGeometryCompat"/> still reports false (unknown) for those.
    /// </summary>
    private static void EnsureGeometryCompatCaptured(string itemId, ILogger logger, params string[] candidatePaths)
    {
        lock (StateLock)
        {
            LoadProbeStates();
            if (GeometryCompatStates.ContainsKey(itemId))
            {
                return;
            }
        }

        string? path = candidatePaths.FirstOrDefault(FileExistsNonEmpty);
        if (path is null)
        {
            return; // nothing on disk yet — captured on a later touch
        }

        GeometryCompatInfo info = InspectGeometryCompat(path) ?? new GeometryCompatInfo(null, null, null);
        lock (StateLock)
        {
            GeometryCompatStates[itemId] = info;
            MarkProbeStateDirty(logger);
        }

        logger.Debug(
            "Geometry compat captured for {Item}: formatVersion {FormatVersion} (lowest {LowestVersion}), animations {Animations}, particles {Particles}, render controllers {RenderControllers}, face bones {FaceBones} ({Path})",
            itemId,
            info.FormatVersion ?? "unknown",
            info.LowestFormatVersion ?? "unknown",
            info.HasAnimations?.ToString() ?? "unknown",
            info.HasParticles?.ToString() ?? "unknown",
            info.HasRenderControllers?.ToString() ?? "unknown",
            info.HasFaceBones?.ToString() ?? "unknown",
            path);
    }

    /// <summary>
    /// Opens a piece binary zip and reads the geometry compatibility facts. Real
    /// pieces are an outer zip holding one nested ppack0.zip (flat custom zips are
    /// accepted too) — both layers are searched. The nested layer's art files are
    /// per-file encrypted: the layer's contents.json (256-byte ContentIdentity
    /// header + AES-256-CFB8 body with the client's built-in key) carries every
    /// file key, so each entry is decrypted through its layer's key map before
    /// scanning (PersonaContentCrypto). Geometry files are then detected by
    /// CONTENT, not by name: every .json entry is lenient-parsed (comments and
    /// trailing commas are common in Bedrock files) and counts as geometry when it
    /// holds a minecraft:geometry block, a bones array or a legacy
    /// geometry.&lt;name&gt; wrapper — whatever the file is called (.geometry.json,
    /// .geo.json, custom names). Signals: the highest/lowest parsed geometry
    /// format_version across ALL candidates (one per body size), a non-empty
    /// top-level animations object or *.animation.json entries, particle/flipbook
    /// and render-controller references in geometry/meta/manifest json, and face
    /// bones in the geometry bone lists. Returns null when the zip cannot be
    /// opened at all.
    /// </summary>
    // One scanned zip entry plus the per-file key map of its zip layer — real
    // marketplace pieces carry per-file-encrypted art inside ppack0.zip, and the
    // layer's contents.json holds every file's key (PersonaContentCrypto).
    // Top-layer entries carry no map (raw files).
    private sealed record CompatEntry(ZipArchiveEntry Entry, Dictionary<string, string>? FileKeys);

    private static GeometryCompatInfo? InspectGeometryCompat(string zipPath)
    {
        // every opened archive stays alive until the inspection finished — the
        // collected entries belong to their archive and their content cannot be
        // read once the archive is disposed
        var archives = new List<ZipArchive>();
        try
        {
            archives.Add(ZipFile.OpenRead(zipPath));
            var entries = new List<CompatEntry>();
            CollectZipEntries(archives[0], entries, archives);
            return InspectCompatEntries(entries);
        }
        catch (Exception)
        {
            return null; // unreadable/partial zip — the fields stay unknown
        }
        finally
        {
            foreach (ZipArchive archive in archives)
            {
                archive.Dispose();
            }
        }
    }

    private static void CollectZipEntries(ZipArchive archive, List<CompatEntry> into, List<ZipArchive> archives)
    {
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            into.Add(new CompatEntry(entry, null));
            if (entry.FullName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    ZipArchive nested = new(entry.Open(), ZipArchiveMode.Read, leaveOpen: false);
                    archives.Add(nested);
                    // the nested layer's art files are per-file encrypted — its
                    // contents.json (raw, listed without a key) maps each path
                    // to the key that decrypts it; a layer without a readable
                    // contents.json is scanned raw (custom flat packs)
                    Dictionary<string, string>? fileKeys = null;
                    ZipArchiveEntry? contents = nested.GetEntry("contents.json");
                    if (contents is not null)
                    {
                        fileKeys = PersonaContentCrypto.TryReadContentsKeys(ReadEntryBytes(contents));
                    }

                    CollectNestedZipEntries(nested, into, archives, fileKeys);
                }
                catch (Exception)
                {
                    // not a readable nested zip — its entries are skipped
                }
            }
        }
    }

    private static void CollectNestedZipEntries(ZipArchive archive, List<CompatEntry> into, List<ZipArchive> archives, Dictionary<string, string>? fileKeys)
    {
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            into.Add(new CompatEntry(entry, fileKeys));
            if (entry.FullName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    ZipArchive nested = new(entry.Open(), ZipArchiveMode.Read, leaveOpen: false);
                    archives.Add(nested);
                    CollectNestedZipEntries(nested, into, archives, fileKeys);
                }
                catch (Exception)
                {
                    // not a readable nested zip — its entries are skipped
                }
            }
        }
    }

    private static byte[] ReadEntryBytes(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    // The face-attachment bone names the frozen client misplaces (the persona
    // face-accessory rig of 1.19.50/1.20.10, MCPE-165232 — see the
    // CosmeticCompatIndex risky rule). Exact bone-name match so "forehead" or
    // "arrowhead" cannot trigger it. Recorded as a per-piece FACT — whether it
    // marks the piece risky is the gate's call (PersonaPieceTypes scopes it to
    // face-accessory pieces): hats, hoods and helmets legitimately carry
    // hat/head rig bones the frozen client renders fine. Internal — the
    // serve-time auto-rig repairer (PersonaRigRepairer) classifies bones with
    // the exact same sets, so the two can never drift apart.
    internal static readonly HashSet<string> FaceBoneNames = new(StringComparer.OrdinalIgnoreCase) { "face", "hat", "head" };

    // The standard player-rig bone names the frozen client binds correctly.
    // Pieces carrying ANY bone outside this set (whole-head replacements with
    // invented bone trees — "Squid head", "Jelly UFO head" — custom tentacle
    // chains, floating props) render DISPLACED/STRETCHED on the frozen client:
    // the bones the client cannot bind leave their cubes at raw local
    // coordinates. Recorded as a per-piece FACT — whether it marks the piece
    // risky is the gate's call (PersonaPieceTypes scopes it to head-family
    // types; hair is exempt — ponytails legitimately add bones). Internal — the
    // serve-time auto-rig repairer (PersonaRigRepairer) classifies bones with
    // the exact same sets, so the two can never drift apart.
    internal static readonly HashSet<string> StandardRigBoneNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "root", "waist", "body", "head", "hat", "cape",
        "leftArm", "rightArm", "leftLeg", "rightLeg",
        "leftSleeve", "rightSleeve", "leftPants", "rightPants", "jacket",
    };

    private static GeometryCompatInfo? InspectCompatEntries(List<CompatEntry> entries)
    {
        // Worst case wins across EVERY geometry of the piece — real pieces carry
        // one geometry per body size (tall/medium/small/smaller). Geometry files
        // are detected by CONTENT, not by name: piece zips carry .geometry.json,
        // .geo.json and arbitrary custom names alike, and the old name filter
        // recorded all-unknown facts for parseable pieces. Every .json entry is
        // lenient-parsed (Bedrock files commonly carry comments and trailing
        // commas — the strict parser threw and hid the geometry) and counts as a
        // geometry when it holds a minecraft:geometry block, a bones array or a
        // legacy geometry.<name> wrapper.
        Version? highest = null;
        Version? lowest = null;
        string? highestText = null;
        string? lowestText = null;
        bool? hasAnimations = null;
        bool? hasParticles = null;
        bool? hasRenderControllers = null;
        bool? hasFaceBones = null;
        float? maxBoundsWidth = null;
        float? maxBoundsHeight = null;
        bool? hasNonstandardBones = null;
        bool? hasTextureBindingIssue = null;
        bool sawGeometry = false;
        bool sawMetaOrManifest = false;
        bool sawTextureEntry = false;
        var metaTextureRefs = new List<string>();

        // every zip entry basename (any layer) — a meta geometry_sources
        // "texture" reference that matches NONE of them cannot bind on the
        // client and the piece renders untextured
        var entryBasenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CompatEntry compatEntryEntryName in entries)
        {
            entryBasenames.Add(compatEntryEntryName.Entry.Name);
            if (compatEntryEntryName.Entry.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || compatEntryEntryName.Entry.Name.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)
                || compatEntryEntryName.Entry.Name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || compatEntryEntryName.Entry.Name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                || compatEntryEntryName.Entry.Name.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            {
                sawTextureEntry = true;
            }
        }

        // The bone lists a geometry can carry: the modern minecraft:geometry
        // entries, a root-level "bones" array and the legacy "geometry.<name>"
        // wrapper objects — whichever shape the file uses.
        static IEnumerable<JsonElement> BoneLists(JsonElement root)
        {
            if (root.TryGetProperty("bones", out JsonElement rootBones)
                && rootBones.ValueKind == JsonValueKind.Array)
            {
                yield return rootBones;
            }

            if (root.TryGetProperty("minecraft:geometry", out JsonElement geometries)
                && geometries.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement geometry in geometries.EnumerateArray())
                {
                    if (geometry.ValueKind == JsonValueKind.Object
                        && geometry.TryGetProperty("bones", out JsonElement geometryBones)
                        && geometryBones.ValueKind == JsonValueKind.Array)
                    {
                        yield return geometryBones;
                    }
                }
            }

            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object
                    && property.Value.TryGetProperty("bones", out JsonElement wrapped)
                    && wrapped.ValueKind == JsonValueKind.Array)
                {
                    yield return wrapped;
                }
            }
        }

        bool ScanFaceBones(JsonElement root)
        {
            foreach (JsonElement bones in BoneLists(root))
            {
                foreach (JsonElement bone in bones.EnumerateArray())
                {
                    if (bone.ValueKind == JsonValueKind.Object
                        && bone.TryGetProperty("name", out JsonElement name)
                        && name.ValueKind == JsonValueKind.String
                        && name.GetString() is { Length: > 0 } boneName
                        && FaceBoneNames.Contains(boneName))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        foreach (CompatEntry compatEntry in entries)
        {
            ZipArchiveEntry entry = compatEntry.Entry;
            if (!entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry.Name.EndsWith(".animation.json", StringComparison.OrdinalIgnoreCase))
            {
                hasAnimations = true; // animation files in the zip — a clear signal
                continue;
            }

            bool isMetaOrManifest = entry.Name.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)
                || entry.Name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase);
            if (isMetaOrManifest)
            {
                sawMetaOrManifest = true;
            }

            string? text = TryReadEntryText(compatEntry);
            if (text is null)
            {
                continue;
            }

            if (isMetaOrManifest)
            {
                if (text.Contains("particle", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("flipbook", StringComparison.OrdinalIgnoreCase))
                {
                    hasParticles = true; // particle/flipbook reference in meta/manifest
                }

                if (text.Contains("render_controllers", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("controller.render.", StringComparison.OrdinalIgnoreCase))
                {
                    hasRenderControllers = true; // render-controller reference in meta/manifest
                }

                // the meta's geometry_sources[].texture names are the texture
                // binding the client's persona pipeline loads — a reference no
                // zip entry carries means the piece imports and renders
                // UNTEXTURED (colloquially "losing textures")
                if (entry.Name.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)
                    && text.Contains("geometry_sources", StringComparison.OrdinalIgnoreCase))
                {
                    TryReadMetaTextureRefs(text, metaTextureRefs);
                }
            }

            try
            {
                using var doc = JsonDocument.Parse(text, LenientJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("minecraft:geometry", out _)
                    && !BoneLists(root).Any())
                {
                    continue; // meta, manifest, entity json — no geometry structure
                }

                sawGeometry = true;

                // render-controller references in the geometry json — the frozen
                // client cannot resolve them (untextured/wrong render pass).
                // Deliberately NOT scanned on entity/attachable json: persona
                // pieces render through the persona pipeline, a dead client_entity
                // file with a controller.render reference is harmless and the
                // filter must not hide the piece over it
                if (text.Contains("render_controllers", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("controller.render.", StringComparison.OrdinalIgnoreCase))
                {
                    hasRenderControllers = true;
                }
                if (root.TryGetProperty("format_version", out JsonElement version))
                {
                    string? candidateVersion = version.ValueKind == JsonValueKind.String ? version.GetString() : version.GetRawText();
                    if (candidateVersion is not null && TryParseFormatVersion(candidateVersion, out Version parsed))
                    {
                        if (highest is null || parsed > highest)
                        {
                            highest = parsed;
                            highestText = candidateVersion;
                        }

                        if (lowest is null || parsed < lowest)
                        {
                            lowest = parsed;
                            lowestText = candidateVersion;
                        }
                    }
                }

                // a top-level, non-empty "animations" object in the geometry json
                if (root.TryGetProperty("animations", out JsonElement animations)
                    && animations.ValueKind == JsonValueKind.Object
                    && animations.EnumerateObject().Any())
                {
                    hasAnimations = true;
                }

                // face bones (the persona face-accessory rig, MCPE-165232) — the
                // frozen client attaches those wrong
                if (ScanFaceBones(root))
                {
                    hasFaceBones = true;
                }

                // visible bounds + custom rig bones — oversized and custom-rigged
                // head pieces render displaced/stretched on the frozen client
                ScanRigShape(root);
            }
            catch (JsonException)
            {
                // malformed json — the raw-text scans above still counted
            }
        }

        if (!sawGeometry && highest is null && !sawMetaOrManifest && hasAnimations is not true && hasParticles is not true && hasRenderControllers is not true && hasFaceBones is not true
            && maxBoundsWidth is null && maxBoundsHeight is null && hasNonstandardBones is not true && hasTextureBindingIssue is not true && metaTextureRefs.Count == 0)
        {
            return null; // nothing inspectable in the zip — all fields unknown
        }

        // conservative negatives: the zip held a geometry file with no animation
        // signal anywhere, meta/manifest text without a particle reference, or
        // no render-controller / face-bone evidence in the files that could
        // carry them
        hasAnimations ??= sawGeometry ? false : null;
        hasParticles ??= sawMetaOrManifest ? false : null;
        hasRenderControllers ??= sawGeometry || sawMetaOrManifest ? false : null;
        hasFaceBones ??= sawGeometry ? false : null;
        hasNonstandardBones ??= sawGeometry ? false : null;

        // texture binding: positive evidence only. A meta that declared
        // geometry_sources texture references resolves the fact — every
        // reference matched an entry = false, one dangling reference = true.
        // A piece with geometry but NO texture-like entry at all also renders
        // untextured. Everything else stays null (unknown — never hides).
        if (hasTextureBindingIssue is null)
        {
            if (metaTextureRefs.Count > 0)
            {
                hasTextureBindingIssue = metaTextureRefs.Any(reference =>
                    !entryBasenames.Contains(reference)
                    && !entryBasenames.Contains(Path.GetFileName(reference)));
            }
            else if (sawGeometry && !sawTextureEntry)
            {
                hasTextureBindingIssue = true;
            }
        }

        return new GeometryCompatInfo(highestText, hasAnimations, hasParticles, hasRenderControllers, hasFaceBones, lowestText, maxBoundsWidth, maxBoundsHeight, hasNonstandardBones, hasTextureBindingIssue);

        // The description objects of modern minecraft:geometry entries — where
    // visible_bounds_width/height live. Legacy 1.8.0 wrappers carry none, so
    // the bounds stay null and the gate serves (fail-open, like every fact).
    static IEnumerable<JsonElement> GeometryDescriptions(JsonElement root)
    {
        if (root.TryGetProperty("minecraft:geometry", out JsonElement geometries)
            && geometries.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement geometry in geometries.EnumerateArray())
            {
                if (geometry.ValueKind == JsonValueKind.Object
                    && geometry.TryGetProperty("description", out JsonElement description)
                    && description.ValueKind == JsonValueKind.Object)
                {
                    yield return description;
                }
            }
        }
    }

    void ScanRigShape(JsonElement root)
    {
        foreach (JsonElement description in GeometryDescriptions(root))
        {
            if (description.TryGetProperty("visible_bounds_width", out JsonElement width)
                && width.ValueKind == JsonValueKind.Number
                && width.TryGetSingle(out float w)
                && (maxBoundsWidth is null || w > maxBoundsWidth))
            {
                maxBoundsWidth = w;
            }

            if (description.TryGetProperty("visible_bounds_height", out JsonElement height)
                && height.ValueKind == JsonValueKind.Number
                && height.TryGetSingle(out float h)
                && (maxBoundsHeight is null || h > maxBoundsHeight))
            {
                maxBoundsHeight = h;
            }
        }

        foreach (JsonElement bones in BoneLists(root))
        {
            foreach (JsonElement bone in bones.EnumerateArray())
            {
                if (bone.ValueKind == JsonValueKind.Object
                    && bone.TryGetProperty("name", out JsonElement name)
                    && name.ValueKind == JsonValueKind.String
                    && name.GetString() is { Length: > 0 } boneName
                    && !StandardRigBoneNames.Contains(boneName)
                    && !FaceBoneNames.Contains(boneName))
                {
                    hasNonstandardBones = true;
                }
            }
        }
    }
    }

    // Reads the texture file names a piece meta declares for its geometry
    // ({"geometry_sources":[{"geometry":…,"body_size":…,"texture":"x.png"}]})
    // — the binding the client's persona pipeline loads per body size. Appends
    // every "texture" string it finds into <paramref name="into"/>; returns
    // false when the meta does not parse or declares none (fact stays unknown).
    private static bool TryReadMetaTextureRefs(string metaText, List<string> into)
    {
        try
        {
            using var doc = JsonDocument.Parse(metaText, LenientJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("geometry_sources", out JsonElement sources)
                || sources.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            bool any = false;
            foreach (JsonElement source in sources.EnumerateArray())
            {
                if (source.ValueKind == JsonValueKind.Object
                    && source.TryGetProperty("texture", out JsonElement texture)
                    && texture.ValueKind == JsonValueKind.String
                    && texture.GetString() is { Length: > 0 } name)
                {
                    into.Add(name);
                    any = true;
                }
            }

            return any;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Bedrock format versions are "1.21.0" / "1.16.200" style strings — parse
    // the leading numeric components and stop at the first non-numeric one
    // (tolerating pre-release suffixes and garbage); fewer than 4 components
    // pad with 0. Unparsable input returns false (fail closed). Keep in sync
    // with PersonaPieceTypes.TryParseFormatVersion.
    private static bool TryParseFormatVersion(string? formatVersion, out Version version)    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(formatVersion))
        {
            return false;
        }

        int[] components = new int[4];
        bool any = false;
        int parsed = 0;
        foreach (string part in formatVersion.Trim().Split('.'))
        {
            if (part.Length == 0
                || !int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                || value < 0)
            {
                break;
            }

            any = true;
            if (parsed < components.Length)
            {
                components[parsed++] = value;
            }
        }

        if (!any)
        {
            return false;
        }

        version = new Version(components[0], components[1], components[2], components[3]);
        return true;
    }

    private static string? TryReadEntryText(CompatEntry compatEntry)
    {
        try
        {
            ZipArchiveEntry entry = compatEntry.Entry;
            if (entry.Length is < 0 or > MaxCompatEntryBytes)
            {
                return null;
            }

            byte[] bytes = ReadEntryBytes(entry);
            // per-file-encrypted art: the layer's contents.json key decrypts it —
            // without this every real marketplace piece scans as ciphertext
            if (compatEntry.FileKeys is { } keys
                && (keys.TryGetValue(entry.Name, out string? fileKey)
                    || keys.TryGetValue(entry.FullName, out fileKey))
                && PersonaContentCrypto.TryDecryptEntry(bytes, fileKey) is { } plain)
            {
                bytes = plain;
            }

            string text = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes);
            return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text; // strip a UTF-8 BOM — the strict parser throws on it
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------------------
    // Lazy thumbnail ensure (MojangImagesCdnController miss path)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// The dressing room asked for a live item thumbnail — a persona piece's or a
    /// classic skin pack's (the Classic Skins row tiles): serve from cache, else
    /// download from the thumbnail URL recorded at sync time; on failure try the
    /// Bedrock-Persona mirror. Returns the cached file path or null.
    /// </summary>
    public static async Task<string?> TryEnsureThumbnailAsync(string imageId, ILogger logger, CancellationToken cancellationToken)
    {
        var index = Index;
        if (index is null)
        {
            return null;
        }

        var entry = index.Items.FirstOrDefault(item => item.ThumbnailId == imageId);
        string? recordedUrl = entry?.ThumbnailUrl;
        if (entry is null)
        {
            // the sync records a thumbnail id/URL per skin pack too — the Classic
            // Skins row's tiles resolve through the same lazy pipeline
            var skinPack = index.SkinPacks.FirstOrDefault(packEntry => packEntry.ThumbnailId == imageId);
            if (skinPack is null)
            {
                return null; // not a live catalog thumbnail
            }

            recordedUrl = skinPack.ThumbnailUrl;
        }

        var gate = GetItemLock($"thumb:{imageId}");
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(ThumbnailsDir);
            string path = Path.Combine(ThumbnailsDir, $"{imageId}.png");
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                return path;
            }

            // the mirror is flat (no piece type in the path) and the repo has a
            // handful of zero-byte PNGs — retrying on the next miss self-heals
            string mirrorUrl = $"{ThumbnailMirrorBase}/{imageId}.png";
            foreach (string candidate in new[] { recordedUrl, mirrorUrl }.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct())
            {
                try
                {
                    // content-CDN file download — NOT the API pacer (see
                    // PaceRemoteAsync): pacing this at 2/s serialized a cold
                    // dressing room's whole page of thumbnails. Cap raw
                    // concurrency instead.
                    await ThumbDownloadSlot.WaitAsync(cancellationToken);
                    try
                    {
                        using var response = await HttpClient.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                        if (!response.IsSuccessStatusCode)
                        {
                            continue;
                        }

                        string partPath = path + ".part";
                        await using (var content = await response.Content.ReadAsStreamAsync(cancellationToken))
                        await using (var fileStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                        {
                            await content.CopyToAsync(fileStream, cancellationToken);
                        }

                        File.Move(partPath, path, overwrite: true);
                        MarkBinaryLanded();
                        return path;
                    }
                    finally
                    {
                        ThumbDownloadSlot.Release();
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.Information("Live persona thumbnail {Image} could not be fetched from {Url}: {Reason}", imageId, candidate, ex.Message);
                }
            }

            return null;
        }
        finally
        {
            gate.Release();
        }
    }
}
