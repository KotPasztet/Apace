using System.Collections.Concurrent;
using System.Text.Json;
using Apace.DB;
using Apace.DB.Models.Player;
using Serilog;

namespace Apace.ApiServer.Services;

// Skin pack gating for the live persona marketplace: the panel decides which
// dressing room categories (persona piece types) are available — a default set
// for every player plus per-player grants. The panel writes packs.json next to
// the catalog index (hot-reloaded like the index itself); per-player grants
// live in earth.db (objects type "personaPacks") and are cached here briefly
// so panel edits apply within ~30s without hitting SQLite on every search.
public static class PersonaPackGate
{
    private sealed record PackConfig(bool? Gating, List<string>? DefaultPacks);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, (DateTimeOffset Expires, IReadOnlyCollection<string> Packs)> GrantCache = new();
    private static readonly IReadOnlyCollection<string> NoPacks = [];

    private static string ConfigPath => Path.Combine(Program.MojangAssetsDir, "persona-catalog", "packs.json");

    private static bool _gating;
    private static IReadOnlySet<string> _defaults = new HashSet<string>();
    private static long _configStamp;

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
            _configStamp = stamp;
            Log.Information("Persona pack config loaded from {Path} — gating: {Gating}, default packs: {Packs}",
                ConfigPath, _gating, _defaults.Count == 0 ? "(none)" : string.Join(", ", _defaults));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load the persona pack config from {Path}", ConfigPath);
        }
    }

    /// <summary>
    /// The piece types this player may see in the live marketplace, or null when
    /// gating is off (serve everything — the pre-gates behavior). Default packs
    /// always apply; grants add per-player extras. Staticdata offers (the
    /// committed legacy catalog and the Default Skins pack) are never gated.
    /// </summary>
    public static async Task<IReadOnlySet<string>?> AllowedPieceTypesAsync(string? playerId, CancellationToken cancellationToken)
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
