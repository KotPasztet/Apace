using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json.Serialization;
using Apace.Common;

namespace Apace.StaticData;

public sealed class EncountersConfig
{
    private const string SpawnConfigFileName = "encounters-spawn.json";
    private const string HostileConfigFileName = "encounters-hostile.json";
    private const int DefaultChancePerTile = 100;

    public readonly ImmutableArray<EncounterConfig> Encounters;
    public readonly int ChancePerTile;
    public readonly string? HostileIcon;

    private readonly ImmutableHashSet<string> _hostileBuildplates = [];

    internal EncountersConfig(string dir)
    {
        try
        {
            ChancePerTile = LoadChancePerTile(dir);
            (HostileIcon, _hostileBuildplates) = LoadHostile(dir);

            LinkedList<EncounterConfig> encounters = [];
            foreach (string file in Directory.EnumerateFiles(dir))
            {
                if (Path.GetExtension(file) != ".json" || Path.GetFileName(file) is SpawnConfigFileName or HostileConfigFileName)
                {
                    continue;
                }

                using (var stream = File.OpenRead(file))
                {
                    var encounter = Json.Deserialize<EncounterConfig>(stream);

                    Debug.Assert(encounter is not null);

                    if (string.IsNullOrWhiteSpace(encounter.AdventureGroup) || encounter.SpawnWeight <= 0)
                    {
                        continue;
                    }

                    encounters.AddLast(encounter with
                    {
                        AdventureGroup = encounter.AdventureGroup.Trim(),
                        SpawnWeight = int.Max(0, encounter.SpawnWeight)
                    });
                }
            }

            Encounters = [.. encounters];
        }
        catch (Exception exception)
        {
            throw new StaticDataException(null, exception);
        }
    }

    public bool IsHostile(string buildplateId)
        => _hostileBuildplates.Contains(buildplateId);

    private static (string? Icon, ImmutableHashSet<string> Buildplates) LoadHostile(string dir)
    {
        string hostileConfigFile = Path.Combine(dir, HostileConfigFileName);
        if (!File.Exists(hostileConfigFile))
        {
            return (null, []);
        }

        using var stream = File.OpenRead(hostileConfigFile);
        HostileConfig? hostileConfig = Json.Deserialize<HostileConfig>(stream);
        Debug.Assert(hostileConfig is not null);
        string? icon = string.IsNullOrWhiteSpace(hostileConfig.Icon) ? null : hostileConfig.Icon.Trim();
        return (icon, [.. (hostileConfig.Hostile ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim())]);
    }

    private static int LoadChancePerTile(string dir)
    {
        string spawnConfigFile = Path.Combine(dir, SpawnConfigFileName);
        if (!File.Exists(spawnConfigFile))
        {
            return DefaultChancePerTile;
        }

        using var stream = File.OpenRead(spawnConfigFile);
        SpawnConfig? spawnConfig = Json.Deserialize<SpawnConfig>(stream);
        Debug.Assert(spawnConfig is not null);
        return spawnConfig.ChancePerTile >= 1 ? spawnConfig.ChancePerTile : DefaultChancePerTile;
    }

    public record EncounterConfig(
        string Icon,
        EncounterConfig.RarityE Rarity,
        int Duration,
        string AdventureGroup,
        int SpawnWeight
    )
    {
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public enum RarityE
        {
            COMMON,
            UNCOMMON,
            RARE,
            EPIC,
            LEGENDARY
        }
    }

    private sealed record HostileConfig(
        string? Icon,
        string[]? Hostile
    );

    private sealed record SpawnConfig(
        int ChancePerTile
    );
}
