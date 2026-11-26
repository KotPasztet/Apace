using System.Collections.Concurrent;
using System.Text.Json;
using Apace.DB;
using Apace.DB.Models.Player;
using Serilog;

namespace Apace.ApiServer.Services;

// Skin pack gating for the live persona marketplace. The marketplace has no
// accessory pack entities — pieces carry exactly one packIdentity (themselves;
// probe: scripts/probe_persona_tags.py) — so packs are the pieces' marketplace
// theme tags (tag.*): enabling a pack serves every themed piece across all
// categories, and the classic-skin packs follow the gate by their pack uuid.
//
// packs.json (written by the panel next to the catalog index, hot-reloaded by
// mtime) holds { defaultPacks, categoryLimit, onlySupported, fillBlankSlotsWithRandomCosmetics,
// experimentalAutoRepairRigs, enableLegacyCosmetics } plus the compat
// knob "maxGeometryFormatVersion" (optional — hand-edit only, the panel's Save
// keeps only its own fields); old files may
// still carry the deprecated "gating" flag — it is ignored (the deserializer
// drops unknown members). Gating is implicit AUTO-GATING from the allowed set
// (defaults ∪ per-player grants): ALL live accessories serve only from the
// selected packs, whatever their marketplace creator — the set must be
// non-empty AND one of the piece's theme tags must be in it, so tagless
// accessories never serve; persona_skin pieces
// and the classic-skin packs are exempt and serve regardless. Players keep the
// staticdata defaults (the built-in Default Skins pack, Bold Rabbit Feet, the
// customs, layout/StoreConfig) — see CatalogController.LiveOfferItemsFor.
//
// Per-player grants live in earth.db (objects type "personaPacks") and are
// cached here briefly so panel edits apply within ~30s without hitting SQLite
// on every search.
public static class PersonaPackGate
{
    public const int DefaultCategoryLimit = 45;

    /// <summary>The baseline "customSkeletonMode" when packs.json omits the
    /// key: Flat only — the user's requested default (extra skeleton handling
    /// ON, offer/serve both strip custom skeletons, so custom-bone pieces
    /// qualify everywhere the gate consults them).</summary>
    public const CustomSkeletonModes DefaultCustomSkeletonMode = CustomSkeletonModes.FlatOnly;

    /// <summary>The "customSkeletonMode" knob (packs.json, default Flat only):
    /// what the server does with cosmetics that carry a custom (non-standard)
    /// rig — the invented bone trees ("Squid head" tentacle chains) the frozen
    /// client cannot bind, which render scattered. Off serves them as-is
    /// (hidden by the compat gate while onlySupported is on); Strip rewrites
    /// the served geometry so every custom bone is REMOVED and its cubes merge
    /// into the nearest standard bone (rigid, nothing can fly apart); FlatOnly
    /// does the same at serve time AND — for pieces the rewriter CANNOT
    /// flatten — hides them from the offer. A piece the strip CAN flatten
    /// counts as flat and serves stripped (custom bones are always
    /// flattenable, so with Flat only on, no piece hides on bones alone).</summary>
    public enum CustomSkeletonModes
    {
        Off = 0,
        Strip = 1,
        FlatOnly = 2,
    }

    private sealed record PackConfig(List<string>? DefaultPacks, int? CategoryLimit, bool? OnlySupported,
        string? MaxGeometryFormatVersion, bool? FillBlankSlots, bool? ExperimentalAutoRepairRigs, string? CustomSkeletonMode,
        bool? EnableLegacyCosmetics);

    // Documented default compat threshold (see PersonaPieceTypes.IsSupportedCosmetic):
    // geometry format_version at or past the max breaks on the frozen client
    // (UV rotation, transform pivots) — the research note in LivePersonaCatalog
    // puts the render breakage at 1.16.0. Configurable via packs.json
    // "maxGeometryFormatVersion". There is deliberately no animation-version
    // knob: animated pieces are risky regardless of their animation
    // format_version, so a threshold for them had nothing to act on.
    public static readonly Version DefaultMaxGeometryFormatVersion = new(1, 16, 0, 0);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, (DateTimeOffset Expires, IReadOnlyCollection<string> Packs)> GrantCache = new();
    private static (DateTimeOffset Expires, IReadOnlySet<string> Packs)? _allGrantsCache;

    private static string ConfigPath => Path.Combine(Program.MojangAssetsDir, "persona-catalog", "packs.json");

    private static IReadOnlySet<string> _defaults = new HashSet<string>();
    private static int _categoryLimit = DefaultCategoryLimit;
    private static bool _onlySupported = true;
    private static bool _fillBlankSlots = true;
    private static bool _experimentalAutoRepairRigs;
    private static CustomSkeletonModes _customSkeletonMode = DefaultCustomSkeletonMode;
    private static Version _maxGeometryFormatVersion = DefaultMaxGeometryFormatVersion;
    private static bool _legacyCosmetics = true;
    private static long _configStamp;

    // Item ids currently offered as blank-slot filler (CatalogController.RefreshFiller
    // mirrors its pool here). The binary fetch pipeline consults this set: filler
    // pieces were OFFERED to players like any other piece, so their binaries
    // download like any offered piece too — without the exemption the lazy fetch
    // re-ran the pack gate WITHOUT it (the controller's own serve check has it)
    // and every filler piece outside the selected packs was forced onto the gray
    // placeholder stub.
    public static readonly HashSet<string> FillerExemptItemIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Max geometry format_version the frozen client renders (packs.json
    /// "maxGeometryFormatVersion", default "1.16.0") — at or past it, UV rotation /
    /// per-face UV / transform pivots break. Consumed by the actual gate math in
    /// PersonaPieceTypes.IsSupportedCosmetic and mirrored by the panel's
    /// CosmeticCompatIndex.</summary>
    public static Version MaxGeometryFormatVersion
    {
        get
        {
            LoadConfig();
            return _maxGeometryFormatVersion;
        }
    }

    /// <summary>
    /// Max pieces served per dressing-room category GROUP (the client renders
    /// one tab per group — e.g. outerwear = persona_outerwear + persona_hood —
    /// see PersonaPieceTypes for the grouping). One shared knob for every
    /// group, independent of pack gating and applied over the whole composed
    /// offer (staticdata pieces count too) — the marketplace has no
    /// per-category size control of its own and the frozen client chokes on
    /// huge category lists. The panel's Marketplace Management page configures
    /// this and the gate hot-reloads it from packs.json. 0 = no limit.
    /// </summary>
    public static int CategoryLimit => _categoryLimit;

    /// <summary>
    /// True when only client-supported live pieces serve (packs.json
    /// "onlySupported", default true when absent): pieces whose geometry the
    /// frozen client cannot render (see PersonaPieceTypes.IsSupportedCosmetic)
    /// are hidden in the offer and refused on equip. Pieces without compat
    /// metadata are served — the filter only ever hides pieces with positive
    /// evidence of incompatibility.
    /// </summary>
    public static bool OnlySupported => _onlySupported;

    /// <summary>
    /// Fill blank dressing-room slots with stable filler pieces (packs.json
    /// "fillBlankSlotsWithRandomCosmetics", default true when absent): after the
    /// pack-selected pieces, the composed offer appends deterministic, supported
    /// live pieces of every dressing-room group until the group reaches the
    /// category limit — always the same pieces for the same catalog, so a
    /// player's earlier-equipped pieces never reshuffle or vanish. With no packs
    /// selected every group fills up to the limit. Nothing fills when the
    /// category limit is 0 (no limit — there is nothing to fill up to).
    /// </summary>
    public static bool FillBlankSlots => _fillBlankSlots;

    /// <summary>
    /// EXPERIMENTAL, DEFAULT OFF (packs.json "experimentalAutoRepairRigs", default
    /// false when absent): when true, head-family pieces whose ONLY compat risk
    /// signal is custom rig bones (PersonaPieceTypes.EvaluateCosmetic with
    /// repair-awareness) have their geometry rewritten at serve time — the
    /// repairer re-parents each custom bone-subtree root onto the standard
    /// "head" bone so the frozen client can bind the geometry and render the
    /// piece attached instead of displaced. Nothing changes while the knob is
    /// off; oversized/animations/particles/format-version pieces are never
    /// repaired (the transform fixes bones only — they stay hidden). The
    /// transform lives in PersonaRigRepairer; the panel's
    /// CosmeticCompatIndex mirrors the verdict awareness.
    /// </summary>
    public static bool ExperimentalAutoRepairRigs => _experimentalAutoRepairRigs;

    /// <summary>The packs.json "customSkeletonMode" knob (default
    /// <see cref="DefaultCustomSkeletonMode"/>, i.e. FlatOnly). Hot-reloaded with
    /// the rest of the config; unparsable values fall back to
    /// <see cref="DefaultCustomSkeletonMode"/>.</summary>
    public static CustomSkeletonModes CustomSkeletonMode => _customSkeletonMode;

    /// <summary>
    /// "Enable legacy cosmetics" (packs.json "enableLegacyCosmetics", default
    /// true when absent — the Marketplace Management page's "Cosmetics sources"
    /// switch): pieces with a manually mirrored binary in the data volume
    /// (<c>&lt;mojang-assets&gt;/playfab/persona/&lt;itemId&gt;.zip</c> — the legacy
    /// mirror, e.g. the 234 recovered original binaries) serve even when the
    /// marketplace CDN is dead: a mirrored binary makes the piece alive again
    /// whatever an earlier upstream probe concluded (the dead→alive bridge).
    /// When false, the legacy mirror is ignored ENTIRELY — the offer hides such
    /// pieces (unless a live-synced binary exists in persona-catalog/binaries),
    /// the warm-up and the dead→alive bridge skip them, and direct binary
    /// requests fall through to the lazy pipeline/placeholder exactly as if the
    /// files were missing. Custom cosmetics, the live volume cache and the
    /// built-in defaults are never affected. Hot-reloaded with the rest of the
    /// config; the offer refreshes on the next request after a panel save.
    /// </summary>
    public static bool LegacyCosmetics
    {
        get
        {
            LoadConfig();
            return _legacyCosmetics;
        }
    }

    /// <summary>True when a serve-time geometry transform is active at all —
    /// the experimental auto-rig repair (re-parent) or the custom-skeleton
    /// stripper. The compat gate lets custom-bones-only head pieces through
    /// only while this is true (they are rewritten at serve time instead of
    /// being hidden).</summary>
    public static bool ServeTimeTransformActive
        => _experimentalAutoRepairRigs || _customSkeletonMode != CustomSkeletonModes.Off;

    /// <summary>True in Flat-only mode (packs.json "customSkeletonMode":
    /// "flatOnly") — the offer hides every piece with custom bones so only
    /// "flat" cosmetics serve.</summary>
    public static bool FlatOnlyActive => _customSkeletonMode == CustomSkeletonModes.FlatOnly;

    /// <summary>The packs.json hot-reload stamp (mtime ticks) — the filler
    /// candidate cache keys off it, so a panel save rebuilds the filler set.</summary>
    public static long ConfigStamp
    {
        get
        {
            LoadConfig();
            return _configStamp;
        }
    }

    /// <summary>The configured default pack uuids (packs.json "defaultPacks") —
    /// the sync-readable "selected bundles" set. The filler pool keys on this
    /// (binaries only download for the selected bundles, so the filler may only
    /// draw from them); per-player grants serve through the pack gate instead.</summary>
    public static IReadOnlySet<string> DefaultPackUuids
    {
        get
        {
            LoadConfig();
            return _defaults;
        }
    }

    /// <summary>
    /// The auto-gating state (no DB access) — for pages that just show the
    /// state: gating restricts the live accessories for a player when at least
    /// one pack is configured for them (defaults ∪ grants); with no defaults
    /// configured (Gating false) no accessory serves unless the player has
    /// grants — without a player, the configured defaults. Skins serve
    /// regardless.
    /// </summary>
    public static (bool Gating, int CategoryLimit) ConfigSnapshot
    {
        get
        {
            LoadConfig();
            return (_defaults.Count > 0, _categoryLimit);
        }
    }

    private static void LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                if (_configStamp != 0)
                {
                    _configStamp = 0;
                    _defaults = new HashSet<string>();
                    _categoryLimit = DefaultCategoryLimit;
                    _onlySupported = true;
                    _fillBlankSlots = true;
                    _experimentalAutoRepairRigs = false;
                    _customSkeletonMode = DefaultCustomSkeletonMode;
                    _maxGeometryFormatVersion = DefaultMaxGeometryFormatVersion;
                    _legacyCosmetics = true;
                    Log.Information("Persona pack config gone from {Path} — no packs configured, the live marketplace serves nothing until packs are selected", ConfigPath);
                }

                return;
            }

            long stamp = File.GetLastWriteTimeUtc(ConfigPath).Ticks;
            if (_configStamp == stamp)
            {
                return;
            }

            var loaded = JsonSerializer.Deserialize<PackConfig>(File.ReadAllText(ConfigPath), JsonOptions);
            _defaults = (loaded?.DefaultPacks ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _categoryLimit = Math.Max(0, loaded?.CategoryLimit ?? DefaultCategoryLimit);
            _onlySupported = loaded?.OnlySupported ?? true;
            _fillBlankSlots = loaded?.FillBlankSlots ?? true;
            _experimentalAutoRepairRigs = loaded?.ExperimentalAutoRepairRigs ?? false;
            _customSkeletonMode = ParseCustomSkeletonMode(loaded?.CustomSkeletonMode);
            _maxGeometryFormatVersion = ParseVersion(loaded?.MaxGeometryFormatVersion, DefaultMaxGeometryFormatVersion);
            _legacyCosmetics = loaded?.EnableLegacyCosmetics ?? true;
            _configStamp = stamp;
            Log.Information("Persona pack config loaded from {Path} — configured packs: {Packs}, category limit: {Limit}, onlySupported: {OnlySupported}, fill blank slots: {FillBlankSlots}, max geometry version: {GeometryVersion}, experimental auto rig repair: {AutoRepairRigs}, custom skeleton mode: {CustomSkeletonMode}, legacy cosmetics: {LegacyCosmetics}",
                ConfigPath, _defaults.Count == 0 ? "(none)" : string.Join(", ", _defaults),
                _categoryLimit == 0 ? "none" : _categoryLimit.ToString(),
                _onlySupported,
                _fillBlankSlots,
                _maxGeometryFormatVersion.ToString(),
                _experimentalAutoRepairRigs,
                _customSkeletonMode,
                _legacyCosmetics);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load the persona pack config from {Path}", ConfigPath);
        }
    }

    // "off" / "strip" / "flatOnly" (case-insensitive, JSON-friendly camelCase
    // like the rest of the file) — anything else falls back to Off.
    // Default OFF in the enum's name only — the user's baseline config is
    // "Flat only": the extra skeleton handling starts ON so risky custom
    // skeletons are flattened at serve time instead of serving raw. An
    // explicit "off" in packs.json still turns it off.
    private static CustomSkeletonModes ParseCustomSkeletonMode(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "strip" => CustomSkeletonModes.Strip,
            "flatonly" or "flat-only" or "flat" => CustomSkeletonModes.FlatOnly,
            "off" => CustomSkeletonModes.Off,
            _ => DefaultCustomSkeletonMode,
        };

    // "1.21.0" / "1.16.200" style threshold strings — leading numeric components,
    // zero-padded to four; unparsable values fall back to the default.
    private static Version ParseVersion(string? value, Version fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                string[] parts = value.Trim().Split('.');
                int[] components = new int[4];
                for (int i = 0; i < parts.Length && i < 4; i++)
                {
                    if (!int.TryParse(parts[i], out int n) || n < 0)
                    {
                        return fallback;
                    }

                    components[i] = n;
                }

                return new Version(components[0], components[1], components[2], components[3]);
            }
        }
        catch (Exception)
        {
            // fall through to the default
        }

        return fallback;
    }

    /// <summary>
    /// The allowed pack uuid set for the caller (theme tags for persona
    /// accessories): defaults ∪ per-player grants. Live accessories serve only
    /// from these packs, whatever their creator — the set must be non-empty AND
    /// one of the piece's theme tags must be in it (tagless accessories never
    /// serve); persona_skin pieces and the classic-skin packs are exempt.
    /// The Default Skins pack and the custom cosmetics are never gated; the
    /// legacy staticdata offers no longer contribute accessories at all —
    /// their Minecraft-creator pieces drop unconditionally because staticdata
    /// carries no pack tags (see CatalogController.BaseOfferItems). Entries
    /// predating the theme model (piece type names or orphaned skin-pack
    /// uuids) simply never match a theme tag and are ignored.
    /// </summary>
    public static async Task<IReadOnlySet<string>> AllowedPackUuidsAsync(string? playerId, CancellationToken cancellationToken)
    {
        LoadConfig();

        var allowed = new HashSet<string>(_defaults, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(playerId))
        {
            foreach (string pack in await PlayerGrantsAsync(playerId, cancellationToken))
            {
                allowed.Add(pack);
            }
        }

        return allowed;
    }

    /// <summary>
    /// The gate's allowed-set composition for the diagnostic logs: the configured
    /// default pack ids and the player's grant count — the same loads as
    /// <see cref="AllowedPackUuidsAsync"/> (config mtime check + the 30 s grant
    /// cache), so calling it after the gate adds no extra database scan.
    /// </summary>
    public static async Task<(IReadOnlyCollection<string> Defaults, int Grants)> AllowedCompositionAsync(string? playerId, CancellationToken cancellationToken)
    {
        LoadConfig();
        IReadOnlyCollection<string> grants = [];
        if (!string.IsNullOrWhiteSpace(playerId))
        {
            grants = await PlayerGrantsAsync(playerId, cancellationToken);
        }

        return (_defaults, grants.Count);
    }

    private static async Task<IReadOnlyCollection<string>> PlayerGrantsAsync(string playerId, CancellationToken cancellationToken)
    {
        string key = playerId.ToLowerInvariant();
        if (GrantCache.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
        {
            return cached.Packs;
        }

        try
        {
            EarthDB.Results results = await new EarthDB.Query(false)
                .Get("personaPacks", key, typeof(PersonaPackGrants))
                .ExecuteAsync(Program.DB, cancellationToken);
            var grants = results.Get<PersonaPackGrants>("personaPacks")?.Packs ?? [];
            GrantCache[key] = (DateTimeOffset.UtcNow.AddSeconds(30), grants);
            return grants;
        }
        catch (EarthDB.DatabaseException ex)
        {
            Log.Warning(ex, "Could not read persona pack grants for {Player} — serving default packs only", key);
            return [];
        }
    }

    /// <summary>
    /// defaults ∪ every player's personaPacks grants — the allowed set for the
    /// unauthenticated binary endpoint (no player identity): everything the
    /// catalog can serve to anyone, so a piece some player sees also downloads,
    /// while accessories no pack selection anywhere covers refuse. Full
    /// SQLite scan over the personaPacks objects, cached ~30 s like the
    /// per-player grants; on a database error it falls back to defaults-only.
    /// </summary>
    public static async Task<IReadOnlySet<string>> AllAllowedPackUuidsAsync(CancellationToken cancellationToken)
    {
        LoadConfig();

        if (_allGrantsCache is { } cached && cached.Expires > DateTimeOffset.UtcNow)
        {
            return cached.Packs;
        }

        var allowed = new HashSet<string>(_defaults, StringComparer.OrdinalIgnoreCase);
        try
        {
            await foreach (PersonaPackGrants grants in Program.DB.ReadObjectsByTypeAsync<PersonaPackGrants>("personaPacks", cancellationToken))
            {
                foreach (string pack in grants.Packs)
                {
                    allowed.Add(pack);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not scan persona pack grants — the binary endpoint gates against the configured defaults only");
        }

        _allGrantsCache = (DateTimeOffset.UtcNow.AddSeconds(30), allowed);
        return allowed;
    }
}
