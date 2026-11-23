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
// categories, pieces without a theme tag (the base content) always serve, and
// the classic-skin packs keep following the gate by their pack uuid. The panel
// writes packs.json next to the catalog index (hot-reloaded like the index
// itself); per-player grants live in earth.db (objects type "personaPacks")
// and are cached here briefly so panel edits apply within ~30s without
// hitting SQLite on every search.
public static class PersonaPackGate
{
    public const int DefaultCategoryLimit = 30;

    private sealed record PackConfig(bool? Gating, List<string>? DefaultPacks, int? CategoryLimit);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, (DateTimeOffset Expires, IReadOnlyCollection<string> Packs)> GrantCache = new();
    private static readonly IReadOnlyCollection<string> NoPacks = [];

    private static string ConfigPath => Path.Combine(Program.MojangAssetsDir, "persona-catalog", "packs.json");

    private static bool _gating;
    private static IReadOnlySet<string> _defaults = new HashSet<string>();
    private static int _categoryLimit = DefaultCategoryLimit;
    private static long _configStamp;

    /// <summary>
    /// Max live pieces served per piece type (one shared knob for every dressing
    /// room category — tops, capes, beards, …). The marketplace has no
    /// per-category size control of its own and the frozen client chokes on huge
    /// category lists; the panel's Marketplace Management page configures this
    /// and the gate hot-reloads it from packs.json. 0 = no limit.
    /// </summary>
    public static int CategoryLimit => _categoryLimit;

    /// <summary>
    /// The gating flag only (no DB access) — for pages that just show the state.
    /// </summary>
    public static (bool Gating, int CategoryLimit) ConfigSnapshot
    {
        get
        {
            LoadConfig();
            return (_gating, _categoryLimit);
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
                    _gating = false;
                    _defaults = new HashSet<string>();
                    _categoryLimit = DefaultCategoryLimit;
                    Log.Information("Persona pack config gone from {Path} — gating off", ConfigPath);
                }

                return;
            }

            long stamp = File.GetLastWriteTimeUtc(ConfigPath).Ticks;
            if (_configStamp == stamp)
            {
                return;
            }

            var loaded = JsonSerializer.Deserialize<PackConfig>(File.ReadAllText(ConfigPath), JsonOptions);
            _gating = loaded?.Gating ?? false;
            _defaults = (loaded?.DefaultPacks ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _categoryLimit = Math.Max(0, loaded?.CategoryLimit ?? DefaultCategoryLimit);
            _configStamp = stamp;
            Log.Information("Persona pack config loaded from {Path} — gating: {Gating}, default packs: {Packs}, category limit: {Limit}",
                ConfigPath, _gating, _defaults.Count == 0 ? "(none)" : string.Join(", ", _defaults),
                _categoryLimit == 0 ? "none" : _categoryLimit.ToString());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load the persona pack config from {Path}", ConfigPath);
        }
    }

    /// <summary>
    /// The thematic packs this player may see (theme tags for persona pieces,
    /// pack uuids for classic-skin packs), or null when gating is off (serve
    /// everything — the pre-gating behavior). Defaults always apply; grants add
    /// per-player extras. Entries predating the theme model (piece type names or
    /// orphaned skin-pack uuids) simply never match a theme tag and are ignored.
    /// Staticdata offers (the committed legacy catalog and the Default Skins
    /// pack) are never gated.
    /// </summary>
    public static async Task<IReadOnlySet<string>?> AllowedPackUuidsAsync(string? playerId, CancellationToken cancellationToken)
    {
        LoadConfig();
        if (!_gating)
        {
            return null;
        }

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
            var grants = results.Get<PersonaPackGrants>("personaPacks")?.Packs ?? NoPacks;
            GrantCache[key] = (DateTimeOffset.UtcNow.AddSeconds(30), grants);
            return grants;
        }
        catch (EarthDB.DatabaseException ex)
        {
            Log.Warning(ex, "Could not read persona pack grants for {Player} — serving default packs only", key);
            return NoPacks;
        }
    }
}
