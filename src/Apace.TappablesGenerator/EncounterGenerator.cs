using Serilog;
using System.Diagnostics;
using Apace.Common.Utils;
using Apace.StaticData;

namespace Apace.TappablesGenerator;

public class EncounterGenerator
{
    // TODO: make these configurable
    private static readonly int CHANCE_PER_TILE = 100;
    private static readonly long MIN_DELAY = 1 * 60 * 1000;
    private static readonly long MAX_DELAY = 2 * 60 * 1000;

    private readonly StaticData.StaticData _staticData;
    private readonly int _maxDuration;

    private readonly Random _random;

    public EncounterGenerator(StaticData.StaticData staticData)
    {
        _staticData = staticData;

        if (_staticData.EncountersConfig.Encounters.Length == 0)
        {
            Log.Warning("No encounter configs provided");
        }

        _maxDuration = _staticData.EncountersConfig.Encounters.Select(encounterConfig => encounterConfig.Duration).DefaultIfEmpty().Max() * 1000;

        _random = new Random();
    }

    public long GetMaxEncounterLifetime()
        => MAX_DELAY + _maxDuration + 30 * 1000;

    public Encounter[] GenerateEncounters(int tileX, int tileY, long currentTime)
    {
        if (!ENABLE_RANDOM_ENCOUNTERS)
        {
            return [];
        }

        if (_staticData.EncountersConfig.Encounters.Length == 0)
        {
            return [];
        }

        List<Encounter> encounters = [];
        if (_random.Next(0, CHANCE_PER_TILE) == 0)
        {
            long spawnDelay = _random.NextInt64(MIN_DELAY, MAX_DELAY + 1);

            EncountersConfig.EncounterConfig? encounterConfig = PickEncounterConfig();
            if (encounterConfig is null)
            {
                return [];
            }

            string? encounterBuildplateId = _staticData.AdventuresConfig.PickTemplateForFolder(encounterConfig.AdventureGroup, _random);
            if (encounterBuildplateId is null)
            {
                Log.Warning("Encounter config references adventure group {AdventureGroup}, but that group has no buildplates", encounterConfig.AdventureGroup);
                return [];
            }

            Span<float> tileBounds = stackalloc float[4];
            GetTileBounds(tileX, tileY, tileBounds);
            float lat = _random.NextSingle(tileBounds[1], tileBounds[0]);
            float lon = _random.NextSingle(tileBounds[2], tileBounds[3]);

            var encounter = new Encounter(
                U.RandomUuid().ToString(),
                lat,
                lon,
                currentTime + spawnDelay,
                encounterConfig.Duration * 1000,
                encounterConfig.Icon,
                Enum.Parse<Encounter.RarityE>(encounterConfig.Rarity.ToString()),
                encounterBuildplateId
            );

            encounters.Add(encounter);
        }

        return [.. encounters];
    }

    // ---- Trading posts: fixed, tile-seeded locations (no storage needed, stable across restarts) ----
    private const int POST_TILE_ODDS = 1;                      // 1 in N tiles has a trading post
    private const long POST_WINDOW_MS = 6L * 60 * 60 * 1000;    // stock rotates every 6 hours
    private const string POST_ADVENTURE_GROUP = "trading_posts";
    private const string POST_ICON = "genoa:adventure_generic_map";

    // The original random per-tile encounters. Off: only trading posts spawn naturally.
    private static readonly bool ENABLE_RANDOM_ENCOUNTERS = false;

    private readonly Dictionary<(int, int), long> _lastPostWindow = [];

    // DEBUG: pin one trading post ~20 m north of this spot so it can be tapped from home.
    // Copy lat/lon from the "locations/{lat}/{lon}" request lines in the server log, e.g. (12.3456f, -78.9012f).
    // Leave as null to turn it off.
    private static readonly (float Lat, float Lon)? DEBUG_POST_LOCATION = null;

    public Encounter[] GenerateTradingPost(int tileX, int tileY, long currentTime)
    {
        // Decide (deterministically) whether this tile has a post, and where in the tile it sits
        var tileRng = new Random(Seed(tileX, tileY, 0, 1));

        bool debugTile = DEBUG_POST_LOCATION is { } debugLocation
            && LocationToTile(debugLocation.Lat, debugLocation.Lon) == (tileX, tileY);
        if (!debugTile && tileRng.Next(POST_TILE_ODDS) != 0)
        {
            return [];
        }

        float lat;
        float lon;
        if (debugTile)
        {
            lat = DEBUG_POST_LOCATION!.Value.Lat + 0.0002f;
            lon = DEBUG_POST_LOCATION!.Value.Lon;
        }
        else
        {
            Span<float> tileBounds = stackalloc float[4];
            GetTileBounds(tileX, tileY, tileBounds);
            lat = tileRng.NextSingle(tileBounds[1], tileBounds[0]);
            lon = tileRng.NextSingle(tileBounds[2], tileBounds[3]);
        }

        // Per-tile offset so posts don't all rotate at the same moment
        long offset = tileRng.NextInt64(POST_WINDOW_MS);
        long window = (currentTime + offset) / POST_WINDOW_MS;
        if (_lastPostWindow.TryGetValue((tileX, tileY), out long lastWindow) && lastWindow == window)
        {
            return [];
        }

        // Same tile + same window always picks the same buildplate, so stock is stable until the window rolls over
        var windowRng = new Random(Seed(tileX, tileY, window, 2));
        string? buildplateId = _staticData.AdventuresConfig.PickTemplateForFolder(POST_ADVENTURE_GROUP, windowRng);
        if (buildplateId is null)
        {
            Log.Warning("Trading post adventure group {AdventureGroup} has no buildplates", POST_ADVENTURE_GROUP);
            return [];
        }

        _lastPostWindow[(tileX, tileY)] = window;

        if (debugTile)
        {
            Log.Information("DEBUG trading post placed at {Lat},{Lon} in tile {TileX},{TileY}", lat, lon, tileX, tileY);
        }

        long spawnTime = window * POST_WINDOW_MS - offset;
        return
        [
            new Encounter(
                U.RandomUuid().ToString(),
                lat,
                lon,
                spawnTime,
                POST_WINDOW_MS,
                POST_ICON,
                Encounter.RarityE.COMMON,
                buildplateId
            )
        ];
    }

    // Same web-mercator tile maths as TappablesManager, so the debug spot lands in the right tile
    private static (int X, int Y) LocationToTile(double lat, double lon)
    {
        double x = (1.0 + (lon * Math.PI / 180.0) / Math.PI) / 2.0;
        double latRad = lat * Math.PI / 180.0;
        double y = (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0;
        return ((int)Math.Floor(x * (1 << 16)), (int)Math.Floor(y * (1 << 16)));
    }

    // Plain arithmetic on purpose: HashCode.Combine / string.GetHashCode are randomized per process,
    // which would move the posts every time the server restarts.
    private static int Seed(int x, int y, long w, int salt)
        => unchecked((int)(x * 73856093L ^ y * 19349663L ^ w * 83492791L ^ salt * 2654435761L));

    private EncountersConfig.EncounterConfig? PickEncounterConfig()
    {
        int totalWeight = _staticData.EncountersConfig.Encounters.Sum(encounterConfig => encounterConfig.SpawnWeight);
        if (totalWeight <= 0)
        {
            return null;
        }

        int roll = _random.Next(0, totalWeight);
        foreach (EncountersConfig.EncounterConfig encounterConfig in _staticData.EncountersConfig.Encounters)
        {
            roll -= encounterConfig.SpawnWeight;
            if (roll < 0)
            {
                return encounterConfig;
            }
        }

        return _staticData.EncountersConfig.Encounters[^1];
    }

    private static void GetTileBounds(int tileX, int tileY, Span<float> dest)
    {
        Debug.Assert(dest.Length >= 4);

        dest[0] = YToLat((float)tileY / (1 << 16));
        dest[1] = YToLat((float)(tileY + 1) / (1 << 16));
        dest[2] = XToLon((float)tileX / (1 << 16));
        dest[3] = XToLon((float)(tileX + 1) / (1 << 16));
    }

    private static float XToLon(float x)
        => ((x * 2.0f - 1.0f) * float.Pi) * (180f / float.Pi);

    private static float YToLat(float y)
        => (float.Atan(float.Sinh((1.0f - y * 2.0f) * float.Pi))) * (180f / float.Pi);
}
