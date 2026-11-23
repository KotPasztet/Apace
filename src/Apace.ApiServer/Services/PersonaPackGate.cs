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
// mtime) holds { defaultPacks, categoryLimit, onlySupported } plus the compat
// knob "maxGeometryFormatVersion" (optional — hand-edit only, the panel's Save
// keeps only its own fields); old files may
// still carry the deprecated "gating" flag — it is ignored (the deserializer
// drops unknown members). Gating is implicit AUTO-GATING from the allowed set
// (defaults ∪ per-player grants): Minecraft-creator ACCESSORIES serve only from
// the selected packs — the set must be non-empty AND one of the piece's theme
// tags must be in it, so tagless accessories never serve; persona_skin pieces
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

    private sealed record PackConfig(List<string>? DefaultPacks, int? CategoryLimit, bool? OnlySupported,
        string? MaxGeometryFormatVersion);

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
    private static Version _maxGeometryFormatVersion = DefaultMaxGeometryFormatVersion;
    private static long _configStamp;

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
                    _maxGeometryFormatVersion = DefaultMaxGeometryFormatVersion;
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
            _configStamp = stamp;
            Log.Information("Persona pack config loaded from {Path} — configured packs: {Packs}, category limit: {Limit}, onlySupported: {OnlySupported}, max geometry version: {GeometryVersion}",
                ConfigPath, _defaults.Count == 0 ? "(none)" : string.Join(", ", _defaults),
                _categoryLimit == 0 ? "none" : _categoryLimit.ToString(),
                _onlySupported,
                _maxGeometryFormatVersion.ToString());
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
    /// accessories): defaults ∪ per-player grants. Minecraft-creator
    /// accessories serve only from these packs — the set must be non-empty AND
    /// one of the piece's theme tags must be in it (tagless accessories never
    /// serve); persona_skin pieces and the classic-skin packs are exempt.
    /// staticdata offers (the committed legacy catalog and the Default Skins
    /// pack) and the custom cosmetics are never gated. Entries predating the
    /// theme model (piece type names or orphaned skin-pack uuids) simply never
    /// match a theme tag and are ignored.
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
