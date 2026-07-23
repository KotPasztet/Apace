using Serilog;
using System.Diagnostics;
using Solace.Common.Utils;
using Solace.StaticData;

namespace Solace.TappablesGenerator;

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
