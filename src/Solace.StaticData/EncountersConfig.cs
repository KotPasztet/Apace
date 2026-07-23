using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json.Serialization;
using Solace.Common;

namespace Solace.StaticData;

public sealed class EncountersConfig
{
    public readonly ImmutableArray<EncounterConfig> Encounters;

    internal EncountersConfig(string dir)
    {
        try
        {
            LinkedList<EncounterConfig> encounters = [];
            foreach (string file in Directory.EnumerateFiles(dir))
            {
                if (Path.GetExtension(file) != ".json")
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
}
