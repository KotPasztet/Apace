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
// mtime) holds { defaultPacks, categoryLimit, onlySupported }; old files may
// still carry the deprecated "gating" flag — it is ignored (the deserializer
// drops unknown members). Gating is implicit AUTO-GATING from the allowed set
// (defaults ∪ per-player grants): an EMPTY set gates the whole live marketplace
// off — zero live pieces serve, themed or tagless (players keep the staticdata
// defaults: the built-in Default Skins pack, Bold Rabbit Feet, the customs,
// layout/StoreConfig); a NON-EMPTY set enforces filtering — only pieces
// carrying an allowed theme tag serve while the tagless base pieces keep
// serving (see CatalogController.LiveOfferItemsFor), and the classic-skin
// packs follow the allowed set by their pack uuid.
//
// Per-player grants live in earth.db (objects type "personaPacks") and are
// cached here briefly so panel edits apply within ~30s without hitting SQLite
// on every search.
public static class PersonaPackGate
{
    public const int DefaultCategoryLimit = 30;

    private sealed record PackConfig(List<string>? DefaultPacks, int? CategoryLimit, bool? OnlySupported,
        string? MaxGeometryFormatVersion, string? MaxAnimationFormatVersion);

    // Documented default compat thresholds (see PersonaPieceTypes.IsSupportedCosmetic):
    // geometry / animation format_version at or past the max breaks on the frozen
    // client; legacy versions and ordinary bone animations are fine. Configurable
    // via packs.json "maxGeometryFormatVersion" / "maxAnimationFormatVersion".
    public static readonly Version DefaultMaxGeometryFormatVersion = new(1, 21, 0, 0);
    public static readonly Version DefaultMaxAnimationFormatVersion = new(1, 21, 0, 0);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, (DateTimeOffset Expires, IReadOnlyCollection<string> Packs)> GrantCache = new();

    private static string ConfigPath => Path.Combine(Program.MojangAssetsDir, "persona-catalog", "packs.json");

    private static IReadOnlySet<string> _defaults = new HashSet<string>();
    private static int _categoryLimit = DefaultCategoryLimit;
    private static bool _onlySupported = true;
    private static Version _maxGeometryFormatVersion = DefaultMaxGeometryFormatVersion;
    private static Version _maxAnimationFormatVersion = DefaultMaxAnimationFormatVersion;
    private static long _configStamp;

    /// <summary>Max geometry format_version the frozen client renders (packs.json
    /// "maxGeometryFormatVersion", default "1.21.0") — at or past it, UV rotation /
    /// per-face UV / transform pivots break. See PersonaPieceTypes.IsSupportedCosmetic.</summary>
    public static Version MaxGeometryFormatVersion => _maxGeometryFormatVersion;

    /// <summary>Max animation format_version the frozen client renders (packs.json
    /// "maxAnimationFormatVersion", default "1.21.0") — ordinary bone animations below
    /// it serve; at or past it the piece is risky.</summary>
    public static Version MaxAnimationFormatVersion => _maxAnimationFormatVersion;

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
    /// The auto-gating state (no DB access) — for pages that just show the
    /// state: gating filters the live marketplace for a player when at least
    /// one pack is configured for them (defaults ∪ grants); with no defaults
    /// configured (Gating false) the live marketplace is gated off entirely
    /// unless the player has grants — without a player, the configured
    /// defaults.
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
                    _maxGeometryFormatVersion = DefaultMaxGeometryFormatVersion;
                    _maxAnimationFormatVersion = DefaultMaxAnimationFormatVersion;
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
            _maxGeometryFormatVersion = ParseVersion(loaded?.MaxGeometryFormatVersion, DefaultMaxGeometryFormatVersion);
            _maxAnimationFormatVersion = ParseVersion(loaded?.MaxAnimationFormatVersion, DefaultMaxAnimationFormatVersion);
            _configStamp = stamp;
            Log.Information("Persona pack config loaded from {Path} — configured packs: {Packs}, category limit: {Limit}, onlySupported: {OnlySupported}, max geometry version: {GeometryVersion}, max animation version: {AnimationVersion}",
                ConfigPath, _defaults.Count == 0 ? "(none)" : string.Join(", ", _defaults),
                _categoryLimit == 0 ? "none" : _categoryLimit.ToString(),
                _onlySupported,
                MaxGeometryFormatVersion.ToString(),
                MaxAnimationFormatVersion.ToString());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load the persona pack config from {Path}", ConfigPath);
        }
    }

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
    /// pieces, pack uuids for classic-skin packs): defaults ∪ per-player
    /// grants. An EMPTY set gates the whole live marketplace off — zero live
    /// pieces serve, themed or tagless; staticdata offers (the committed
    /// legacy catalog and the Default Skins pack) and the custom cosmetics
    /// are never gated. A non-empty set enforces filtering: themed pieces
    /// need one of the allowed theme tags while the tagless base pieces keep
    /// serving, and the classic-skin packs follow the set by their pack
    /// uuid. Entries predating the theme model (piece type names or orphaned
    /// skin-pack uuids) simply never match a theme tag and are ignored.
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
}
