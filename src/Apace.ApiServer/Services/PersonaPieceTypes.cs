using System.Globalization;
using Serilog;
using System.Text.Json;
using CItem = Apace.StaticData.Playfab.Item;

namespace Apace.ApiServer.Services;

// Persona piece helpers shared by the catalog and the object controllers, one
// file so the grouping, the piece-id resolution and the compat gate have a
// single implementation on both sides:
//
//  - CategoryGroupOf / GroupLimitCounter: the dressing-room category GROUPING,
//    parsed once from the committed dressing-room layout (PersonaQueryManifest_V1.0
//    item a2e12d4d-3f07-4d47-8f85-2d5b9a44c101). The client renders one
//    dressing-room tab per group — dr.categories.outerwear queries
//    persona_outerwear + persona_hood, dr.categories.bottom queries
//    persona_bottom + persona_high_pants + persona_dress — so the per-category
//    size limit must be counted per GROUP (a per-piece-type cap made grouped
//    tabs show 2-3x the limit), and the equip-side sanitize enforces the same
//    grouping. Piece types the layout does not know get per-piece-type
//    buckets, as does an unparsable layout item (the fallback).
//  - TryResolvePiece: piece-id → piece-type resolver for the persona profile
//    objects (universalApp entries carry piece ids like "<pieceId>/d"). The
//    live catalog index is checked by PieceId first, then the committed
//    staticdata persona items.
    //  - IsSupportedCosmetic: the client-compatibility gate behind packs.json
    //    "onlySupported" (default true) — applies the metadata pipeline's per-item
    //    compat verdict (panel-side twin: CosmeticCompatIndex): only RISKY pieces
    //    (geometry format_version outside the [1.12.0, maxGeometryFormatVersion)
    //    band — max configurable via packs.json, default 1.16.0 — or an
    //    animation/particle/render-controller signal, or face bones on a
    //    face-accessory piece) are blocked; UNKNOWN pieces (no compat facts,
    //    unparsable version) keep serving.
public static class PersonaPieceTypes
{
    // The committed dressing-room layout (PersonaQueryManifest_V1.0): its
    // dressingRoomLayout.categoryObjects[].subCategories[].contentQuery
    // .pieceTypeFilters declare which piece types every dressing-room tab
    // queries. One sub category = one category GROUP.
    private static readonly Guid LayoutItemId = new("a2e12d4d-3f07-4d47-8f85-2d5b9a44c101");

    // Profile entries suffix the piece id: "<pieceId>/d" (default), plus the
    // left/right variants "<pieceId>/l/d" and "<pieceId>/r/d". Longest first.
    private static readonly string[] PieceIdSuffixes = ["/l/d", "/r/d", "/d"];

    // The supported geometry band, EXCLUSIVE at the top ([1.12.0, max)): the
    // max is PersonaPackGate.MaxGeometryFormatVersion (packs.json
    // "maxGeometryFormatVersion", default 1.16.0 — the frozen client renders
    // geometry format_version >= 1.16.0 broken, see the research note in
    // LivePersonaCatalog), below 1.12.0 is legacy geometry. Mirrors
    // CosmeticCompatIndex on the panel side — keep the two in sync.
    private static readonly Version MinSupportedFormatVersion = new(1, 12, 0, 0);

    // piece type → dressing-room group key ("dr.categories.bottom", …), parsed
    // once; empty when the layout could not be read (per-piece-type fallback)
    private static Dictionary<string, string> _categoryGroups = [];
    private static bool _categoryGroupsLoaded;

    // piece id → piece type + creator, from the committed staticdata persona
    // items (the legacy catalog is immutable at runtime); the creator defaults
    // to "Minecraft" like CIItemToItem, so the pack gate can classify
    // staticdata-resolved pieces too
    private static Dictionary<Guid, StaticPieceEntry> _staticPieces = [];
    private static bool _staticPiecesLoaded;

    // piece id → live piece facts, rebuilt per (index stamp, custom-cosmetics
    // stamp) like the controllers' offer caches. Live-catalog pieces carry the
    // gating theme tags and creator the pack gate checks; the panel's custom
    // cosmetics are mapped too (own content — never pack-gated, but resolved so
    // the equip-side checks can classify them). Only pieces the live offer
    // currently serves resolve to a live item — dead or emote pieces fall
    // through to staticdata or stay unknown, which is what the callers want
    // (unknown = keep).
    private sealed record LivePieceEntry(string PieceType, string LiveItemId, string? CreatorName, string[] ThemeTags);

    // staticdata twin of LivePieceEntry — no live item, no theme tags; the
    // creator comes from PersonaData (default "Minecraft", matching CIItemToItem)
    private sealed record StaticPieceEntry(string PieceType, string CreatorName);

    private static Dictionary<Guid, LivePieceEntry> _livePieces = [];
    private static (long Index, ulong Custom) _livePiecesStamp = (long.MinValue, 0);

    /// <summary>One resolved persona piece: its dressing-room piece type, the
    /// side slot the entry selects ("l" / "r" / "d" — left, right, default) and,
    /// for live-catalog pieces, the catalog item id behind it plus the creator
    /// and gating theme tags the pack gate checks (the compat gate is keyed by
    /// item id). LiveItemId is null for staticdata pieces — no live compat
    /// metadata, so they are always treated as supported; their creator still
    /// feeds the pack gate, and since staticdata carries no theme tags a
    /// Minecraft-creator staticdata accessory can never be in the allowed
    /// set.</summary>
    public readonly record struct ResolvedPiece(string PieceType, string? LiveItemId, string? CreatorName, string[] ThemeTags, string Slot);

    /// <summary>
    /// The dressing-room GROUP a piece type is rendered in (see the layout
    /// item referenced above). Piece types the layout does not know get their
    /// own bucket (their piece type); a missing/empty piece type gets the
    /// shared "" bucket so it cannot escape the per-group limit.
    /// </summary>
    public static string CategoryGroupOf(string? pieceType)
    {
        if (string.IsNullOrWhiteSpace(pieceType))
        {
            return "";
        }

        var groups = CategoryGroups;
        return groups.TryGetValue(pieceType, out string? group) ? group : pieceType;
    }

    /// <summary>
    /// Resolves a piece id as it appears in persona profile entries
    /// ("…/d", "…/l/d", "…/r/d" suffixes tolerated) to its piece type, the side
    /// slot the entry selects and the live catalog item behind it. Unknown ids
    /// (built-in client pieces, custom skins, pieces the live index no longer
    /// offers) do not resolve — callers keep them untouched.
    /// </summary>
    public static bool TryResolvePiece(string rawPieceId, out ResolvedPiece piece)
    {
        piece = default;
        (string normalized, string slot) = NormalizePieceId(rawPieceId);
        if (!Guid.TryParse(normalized, out Guid pieceId))
        {
            return false;
        }

        if (LivePieces.TryGetValue(pieceId, out LivePieceEntry? live))
        {
            piece = new ResolvedPiece(live.PieceType, live.LiveItemId, live.CreatorName, live.ThemeTags, slot);
            return true;
        }

        if (StaticPieces.TryGetValue(pieceId, out StaticPieceEntry? staticPiece))
        {
            piece = new ResolvedPiece(staticPiece.PieceType, null, staticPiece.CreatorName, [], slot);
            return true;
        }

        return false;
    }

    /// <summary>
    /// The packs.json "onlySupported" gate (default true): applies the per-item
    /// compatibility verdict the metadata pipeline computes (keep in sync with
    /// the panel's CosmeticCompatIndex — separate processes share the volume,
    /// not the code). Verdict precedence:
    ///  1. an explicit verdict from the compat lane's store
    ///     (persona-catalog/compat-verdicts.json — CosmeticCompatVerdicts):
    ///     "risky" blocks, "supported"/"unknown" serve;
    ///  2. the geometry facts recorded in probe-state.json
    ///     (LivePersonaCatalog.TryGetGeometryCompat): supported = every parsed
    ///     geometry variant inside [1.12.0, maxGeometryFormatVersion) and no
    ///     effect signal (animations, particles, render controllers, face
    ///     bones) — the recorded facts are the worst case across the piece's
    ///     geometry variants, so one risky variant marks the piece;
    ///  3. unknown (no compat metadata, unparsable version) serves — the
    ///     filter only ever hides pieces with positive evidence of
    ///     incompatibility, so with the gate on the rule is verdict != "risky".
    /// </summary>
    public static bool IsSupportedCosmetic(string itemId) => EvaluateCosmetic(itemId).Supported;

    /// <summary>One compat-gate verdict: whether the piece serves, plus the
    /// reason string for the deduplicated diagnostics (always set — supported
    /// verdicts carry the band/evidence summary too).</summary>
    public sealed record CosmeticGateVerdict(bool Supported, string Reason);

    /// <summary>
    /// The full compat verdict behind <see cref="IsSupportedCosmetic"/> — same
    /// precedence, plus the reason the enforcement points log (once per item
    /// per run, see <see cref="LogCompatVerdictOnce"/>). <paramref name="pieceType"/>
    /// is the dressing-room piece type of the item when the caller knows it (the
    /// catalog metadata carries it): the face-bone signal marks the face-accessory
    /// rig (MCPE-165232) and is risky only for persona_face_accessory pieces —
    /// hats, hoods and helmets legitimately carry hat/head rig bones the frozen
    /// client renders fine (it shipped with hundreds of them), so other/unknown
    /// piece types never block on face bones.
    /// </summary>
    public static CosmeticGateVerdict EvaluateCosmetic(string itemId, string? pieceType = null)
    {
        // 1. explicit verdict from the compat store, when the lane wrote one
        string? verdict = CosmeticCompatVerdicts.Lookup(itemId);
        if (verdict is not null)
        {
            return new CosmeticGateVerdict(
                !verdict.Equals("risky", StringComparison.OrdinalIgnoreCase),
                $"explicit compat-verdicts.json override: \"{verdict}\"");
        }

        // 2. the metadata pipeline's recorded geometry facts (worst case
        // across the piece's geometry variants)
        if (!LivePersonaCatalog.TryGetGeometryCompat(itemId, out string? formatVersion, out string? lowestFormatVersion, out bool? hasAnimations, out bool? hasParticles, out bool? hasRenderControllers, out bool? hasFaceBones))
        {
            return new CosmeticGateVerdict(true, "unknown — no compat metadata, nothing proves breakage");
        }

        // A version that cannot be parsed proves nothing about compatibility —
        // unknown, not risky. Null effect flags mean "not scanned" and do not
        // disqualify.
        Version maxGeometry = PersonaPackGate.MaxGeometryFormatVersion;
        bool highestParsed = TryParseFormatVersion(formatVersion, out Version highest);
        bool lowestParsed = TryParseFormatVersion(lowestFormatVersion, out Version lowest);
        if (!highestParsed && !lowestParsed)
        {
            return new CosmeticGateVerdict(true, "unknown — the recorded geometry format_version does not parse");
        }

        if (lowestParsed && lowest < MinSupportedFormatVersion)
        {
            return new CosmeticGateVerdict(false, $"legacy geometry format_version {lowestFormatVersion} — below 1.12.0 (worst of the piece's geometry variants)");
        }

        if (highestParsed && highest >= maxGeometry)
        {
            return new CosmeticGateVerdict(false, $"geometry format_version {formatVersion} — at or past the {maxGeometry} cap (worst of the piece's geometry variants)");
        }

        if (hasAnimations == true)
        {
            return new CosmeticGateVerdict(false, "animations in the piece files");
        }

        if (hasParticles == true)
        {
            return new CosmeticGateVerdict(false, "particles/flipbook in the piece files");
        }

        if (hasRenderControllers == true)
        {
            return new CosmeticGateVerdict(false, "render controllers in the piece files");
        }

        // Face bones mark the persona face-accessory rig (MCPE-165232) — risky
        // only for face accessories. Other piece types legitimately carry
        // hat/head rig bones the frozen client renders fine, so an unknown or
        // non-accessory piece type never blocks here — the filter only hides
        // pieces with positive evidence of incompatibility.
        if (hasFaceBones == true && pieceType is "persona_face_accessory")
        {
            return new CosmeticGateVerdict(false, "face bones in the piece geometry (MCPE-165232 — the face-accessory rig)");
        }

        return new CosmeticGateVerdict(true, $"geometry format_version {formatVersion}, in the supported band with no effect signals");
    }

    // Deduplicated gate diagnostics: the gate runs per request over the whole
    // live offer (~9k pieces) and per-request logging would spam — one line
    // per item id per server run, shared by every enforcement point.
    private static readonly HashSet<string> CompatVerdictsLogged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Logs why a piece was hidden from the catalog or refused on
    /// equip — ONCE per item id per server run (static guard; the gate runs
    /// per request). <paramref name="source"/> names the enforcement point
    /// (catalog offer / published item / equipped profile).</summary>
    public static void LogCompatVerdictOnce(string source, string itemId, string reason)
    {
        lock (CompatVerdictsLogged)
        {
            if (!CompatVerdictsLogged.Add(itemId))
            {
                return;
            }
        }

        Log.Information("Compat gate ({Source}) hides {Item}: {Reason} — max geometry format_version {MaxGeometry} (packs.json maxGeometryFormatVersion)",
            source, itemId, reason, PersonaPackGate.MaxGeometryFormatVersion);
    }

    // Per-request counter for the grouped category limit (limit ≤ 0 disables
    // counting). Allows(group) counts one entry in the group and returns false
    // once the group reached its limit — the first N entries in stream order
    // win, so the result is stable for a given offer order.
    public sealed class GroupLimitCounter
    {
        private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

        public GroupLimitCounter(int limit)
        {
            Limit = limit;
        }

        public int Limit { get; }

        public bool Allows(string groupKey)
        {
            if (Limit <= 0)
            {
                return true;
            }

            int count = _counts.TryGetValue(groupKey, out int seen) ? seen : 0;
            if (count >= Limit)
            {
                return false;
            }

            _counts[groupKey] = count + 1;
            return true;
        }
    }

    // ---------------------------------------------------------------------------
    // Category groups (parsed once from the committed layout)
    // ---------------------------------------------------------------------------

    private static Dictionary<string, string> CategoryGroups
    {
        get
        {
            if (_categoryGroupsLoaded)
            {
                return _categoryGroups;
            }

            _categoryGroupsLoaded = true;
            var groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var staticData = Program.staticData;
                if (staticData is not null
                    && staticData.Playfab.Items.TryGetValue(LayoutItemId, out CItem? layout)
                    && layout.Data is CItem.ClientConfigData { DisplayProperties: var displayProperties })
                {
                    ParseCategoryGroups(displayProperties, groups);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not read the dressing-room layout item {Item} — the category limit falls back to per-piece-type buckets", LayoutItemId);
            }

            if (groups.Count > 0)
            {
                Log.Information("Dressing-room category groups from the layout: {Groups}",
                    string.Join("; ", groups.GroupBy(kv => kv.Value).Select(g => $"{g.Key} = {string.Join(" + ", g.Select(kv => kv.Key))}")));
            }
            else
            {
                Log.Warning("No dressing-room category groups parsed from layout item {Item} — the category limit falls back to per-piece-type buckets", LayoutItemId);
            }

            _categoryGroups = groups;
            return groups;
        }
    }

    private static void ParseCategoryGroups(JsonElement displayProperties, Dictionary<string, string> groups)
    {
        if (!displayProperties.TryGetProperty("dressingRoomLayout", out JsonElement layout)
            || layout.ValueKind != JsonValueKind.Object
            || !layout.TryGetProperty("categoryObjects", out JsonElement categoryObjects)
            || categoryObjects.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement category in categoryObjects.EnumerateArray())
        {
            if (category.ValueKind != JsonValueKind.Object
                || !category.TryGetProperty("subCategories", out JsonElement subCategories)
                || subCategories.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement subCategory in subCategories.EnumerateArray())
            {
                if (subCategory.ValueKind != JsonValueKind.Object
                    || !subCategory.TryGetProperty("title", out JsonElement title)
                    || title.ValueKind != JsonValueKind.String
                    || title.GetString() is not { Length: > 0 } group
                    || !subCategory.TryGetProperty("contentQuery", out JsonElement query)
                    || !query.TryGetProperty("pieceTypeFilters", out JsonElement filters)
                    || filters.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement filter in filters.EnumerateArray())
                {
                    if (filter.ValueKind == JsonValueKind.String && filter.GetString() is { Length: > 0 } pieceType)
                    {
                        groups[pieceType] = group;
                    }
                }
            }
        }
    }

    // ---------------------------------------------------------------------------
    // Piece-id resolution (live index per stamp, staticdata once)
    // ---------------------------------------------------------------------------

    private static Dictionary<Guid, LivePieceEntry> LivePieces
    {
        get
        {
            (long index, ulong custom) stamp = (LivePersonaCatalog.IndexStamp, CustomCosmetics.Stamp);
            if (_livePiecesStamp == stamp)
            {
                return _livePieces;
            }

            var pieces = new Dictionary<Guid, LivePieceEntry>();
            try
            {
                // the live offer: theme tags ride aligned with the items (GetOffer)
                var offer = LivePersonaCatalog.GetOffer();
                for (int i = 0; i < offer.Items.Count; i++)
                {
                    if (offer.Items[i].Data is CItem.PersonaData data)
                    {
                        pieces[data.PieceId] = new LivePieceEntry(
                            data.PieceType,
                            offer.Items[i].Id.ToString(),
                            data.CreatorName,
                            [.. offer.ThemeTags[i]]);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not map the live persona offer pieces — equip-side piece resolution falls back to staticdata");
            }

            try
            {
                // the panel's custom cosmetics: own content, always offered and
                // never pack-gated — resolved so the equip-side checks classify
                // them instead of treating their pieces as unknown
                foreach (CustomCosmeticItem custom in CustomCosmetics.Items)
                {
                    if (Guid.TryParse(custom.PieceId, out Guid pieceId))
                    {
                        pieces[pieceId] = new LivePieceEntry(custom.PieceType, custom.Id, custom.Creator, []);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not map the custom cosmetics pieces — equip-side resolution falls back to the live index only");
            }

            _livePieces = pieces;
            _livePiecesStamp = stamp;
            return pieces;
        }
    }

    private static Dictionary<Guid, StaticPieceEntry> StaticPieces
    {
        get
        {
            if (_staticPiecesLoaded)
            {
                return _staticPieces;
            }

            _staticPiecesLoaded = true;
            var pieces = new Dictionary<Guid, StaticPieceEntry>();
            try
            {
                var staticData = Program.staticData;
                if (staticData is not null)
                {
                    foreach (CItem item in staticData.Playfab.Items.Values)
                    {
                        if (item.Data is CItem.PersonaData data)
                        {
                            pieces[data.PieceId] = new StaticPieceEntry(data.PieceType, data.CreatorName ?? "Minecraft");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not map the staticdata persona pieces — equip-side piece resolution falls back to the live index only");
            }

            _staticPieces = pieces;
            return pieces;
        }
    }

    // "<pieceId>/d", "<pieceId>/l/d", "<pieceId>/r/d" — strip the suffix for
    // the lookup and report the side slot the entry selects ("l" left, "r"
    // right, "d" default/center); callers never rewrite the entry, so the
    // suffix is kept verbatim in the stored payload. Longest first so "/l/d"
    // is not mistaken for the plain "/d" variant.
    private static (string Normalized, string Slot) NormalizePieceId(string rawPieceId)
    {
        foreach (string suffix in PieceIdSuffixes)
        {
            if (rawPieceId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return (rawPieceId[..^suffix.Length], suffix[1..^2]);
            }
        }

        return (rawPieceId, "d");
    }

    // Bedrock format versions are "1.21.0" / "1.16.200" style strings — parse
    // the leading numeric components and stop at the first non-numeric one
    // (tolerating pre-release suffixes and garbage); fewer than 4 components
    // pad with 0. Unparsable input returns false (fail closed).
    private static bool TryParseFormatVersion(string? formatVersion, out Version version)
    {
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
}
