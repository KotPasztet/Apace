using Apace.StaticData;
using RarityE = Apace.StaticData.AdventureChestBlocks.RarityE;

namespace Apace.Tests;

public sealed class AdventureChestBlocksTests
{
    private const string ChestBlocks = """
        {
          "minecraft:ochre_froglight[axis=x]": "COMMON",
          "minecraft:ochre_froglight[axis=z]": "UNCOMMON",
          "minecraft:verdant_froglight[axis=x]": "RARE",
          "minecraft:pearlescent_froglight[axis=x]": "EPIC",
          "minecraft:pearlescent_froglight[axis=z]": "LEGENDARY"
        }
        """;

    [Theory]
    [InlineData("minecraft:ochre_froglight[axis=x]", RarityE.COMMON)]
    [InlineData("minecraft:ochre_froglight[axis=z]", RarityE.UNCOMMON)]
    [InlineData("minecraft:verdant_froglight[axis=x]", RarityE.RARE)]
    [InlineData("minecraft:pearlescent_froglight[axis=x]", RarityE.EPIC)]
    [InlineData("minecraft:pearlescent_froglight[axis=z]", RarityE.LEGENDARY)]
    public void ChestBlockGivesItsRarity(string blockState, RarityE rarity)
    {
        using var data = new TempStaticData();
        data.Write("adventures/chest-blocks.json", ChestBlocks);

        var adventureChestBlocks = data.Load().AdventureChestBlocks;

        Assert.Equal(5, adventureChestBlocks.Count);
        Assert.Equal(rarity, adventureChestBlocks.GetRarity(blockState));
    }

    [Theory]
    [InlineData("minecraft:ochre_froglight[axis=y]")]
    [InlineData("minecraft:verdant_froglight[axis=z]")]
    [InlineData("minecraft:ochre_froglight")]
    [InlineData("minecraft:chest[facing=north,type=single,waterlogged=false]")]
    [InlineData("MINECRAFT:OCHRE_FROGLIGHT[AXIS=X]")]
    [InlineData("")]
    public void OtherBlockHasNoRarity(string blockState)
    {
        using var data = new TempStaticData();
        data.Write("adventures/chest-blocks.json", ChestBlocks);

        Assert.Null(data.Load().AdventureChestBlocks.GetRarity(blockState));
    }

    [Fact]
    public void MissingFileThrowsStaticDataException()
    {
        using var data = new TempStaticData();

        var exception = Assert.Throws<StaticDataException>(() => data.Load().AdventureChestBlocks);
        Assert.True(exception.InnerException is FileNotFoundException or DirectoryNotFoundException);
    }

    [Theory]
    [InlineData("""{ "minecraft:ochre_froglight[axis=x]": "COMMON", """)]
    [InlineData("""{ "minecraft:ochre_froglight[axis=x]": "MYTHIC" }""")]
    [InlineData("""{ "minecraft:ochre_froglight[axis=x]": null }""")]
    [InlineData("""[ "minecraft:ochre_froglight[axis=x]" ]""")]
    [InlineData("")]
    public void UnreadableFileThrowsStaticDataException(string content)
    {
        using var data = new TempStaticData();
        data.Write("adventures/chest-blocks.json", content);

        var exception = Assert.Throws<StaticDataException>(() => data.Load().AdventureChestBlocks);
        Assert.IsNotType<FileNotFoundException>(exception.InnerException);
    }
}
