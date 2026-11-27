using System.Text.Json.Serialization;

namespace Apace.TappablesGenerator;

public sealed record Encounter(
    string Id,
    float Lat,
    float Lon,
    long SpawnTime,
    long ValidFor,
    string Icon,
    Encounter.RarityE Rarity,
    string EncounterBuildplateId,
    bool Hostile = false
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