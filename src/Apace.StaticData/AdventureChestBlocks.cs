using System.Collections.Frozen;
using System.Text.Json.Serialization;
using Apace.Common;

namespace Apace.StaticData;

// Java block state to chest rarity, e.g. { "minecraft:ochre_froglight[axis=x]": "COMMON" }
public sealed class AdventureChestBlocks
{
    public const string FileName = "chest-blocks.json";

    private readonly FrozenDictionary<string, RarityE> _rarities;

    internal AdventureChestBlocks(string dir)
    {
        try
        {
            using var stream = File.OpenRead(Path.Combine(dir, FileName));
            Dictionary<string, RarityE>? rarities = Json.Deserialize<Dictionary<string, RarityE>>(stream);
            _rarities = (rarities ?? []).ToFrozenDictionary();
        }
        catch (Exception exception)
        {
            throw new StaticDataException(null, exception);
        }
    }

    public int Count => _rarities.Count;

    public RarityE? GetRarity(string blockState)
        => _rarities.TryGetValue(blockState, out RarityE rarity) ? rarity : null;

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
