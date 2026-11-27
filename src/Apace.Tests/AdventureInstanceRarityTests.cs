using Apace.ApiServer.Controllers.EarthApi;
using Apace.ApiServer.Types.Common;
using Apace.StaticData;
using static Apace.ApiServer.Utils.BuildplateInstancesManager;

namespace Apace.Tests;

public sealed class AdventureInstanceRarityTests
{
    private const string Pool = """
        {
          "buildplates": [
            { "templateId": "aaaa", "weight": 10 }
          ]
        }
        """;

    private static InstanceInfo Instance(InstanceType type, string buildplateId)
        => new(type, "instance", "player", "adventure-1", buildplateId, "localhost", 19132, true, false);

    [Theory]
    [InlineData(InstanceType.ENCOUNTER)]
    [InlineData(InstanceType.PLAYER_ADVENTURE)]
    public void AdventureInstanceHasTheRarityOfItsPool(InstanceType type)
    {
        using var data = new TempStaticData();
        data.Write("adventures/rare/rare-buildplates.json", Pool);

        Assert.Equal(Rarity.RARE, BuildplatesController.GetAdventureRarity(Instance(type, "aaaa"), data.Load()));
    }

    [Theory]
    [InlineData(InstanceType.ENCOUNTER)]
    [InlineData(InstanceType.PLAYER_ADVENTURE)]
    public void AdventureInstanceOnABuildplateInNoPoolHasNoRarity(InstanceType type)
    {
        using var data = new TempStaticData();
        data.Write("adventures/rare/rare-buildplates.json", Pool);

        Assert.Null(BuildplatesController.GetAdventureRarity(Instance(type, "bbbb"), data.Load()));
    }

    [Theory]
    [InlineData(InstanceType.BUILD)]
    [InlineData(InstanceType.PLAY)]
    [InlineData(InstanceType.SHARED_BUILD)]
    [InlineData(InstanceType.SHARED_PLAY)]
    public void OtherInstanceHasNoRarity(InstanceType type)
    {
        using var data = new TempStaticData();
        data.Write("adventures/rare/rare-buildplates.json", Pool);

        Assert.Null(BuildplatesController.GetAdventureRarity(Instance(type, "aaaa"), data.Load()));
    }

    [Fact]
    public void OtherInstanceDoesNotReadTheAdventurePools()
    {
        using var data = new TempStaticData();
        data.Write("adventures/adventures-spawn.json", "{");

        Assert.Throws<StaticDataException>(() => data.Load().AdventuresConfig);
        Assert.Null(BuildplatesController.GetAdventureRarity(Instance(InstanceType.BUILD, "aaaa"), data.Load()));
    }

    [Theory]
    [InlineData("common", Rarity.COMMON)]
    [InlineData("uncommon", Rarity.UNCOMMON)]
    [InlineData("rare", Rarity.RARE)]
    [InlineData("epic", Rarity.EPIC)]
    [InlineData("legendary", Rarity.LEGENDARY)]
    [InlineData("oobe", Rarity.OOBE)]
    [InlineData("Epic", Rarity.EPIC)]
    [InlineData("RARE", Rarity.RARE)]
    public void FolderNamedAfterARarityGivesThatRarity(string folder, Rarity rarity)
        => Assert.Equal(rarity, BuildplatesController.AdventureFolderToRarity(folder));

    [Theory]
    [InlineData("events")]
    [InlineData("3")]
    [InlineData("")]
    public void OtherFolderGivesNoRarity(string folder)
        => Assert.Null(BuildplatesController.AdventureFolderToRarity(folder));
}
