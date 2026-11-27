using Apace.ApiServer.Controllers.EarthApi;
using Apace.ApiServer.Types.Buildplates;
using Apace.StaticData;
using static Apace.ApiServer.Utils.BuildplateInstancesManager;

namespace Apace.Tests;

public sealed class AdventureFootprintsTests
{
    private const string TinySheepFarm = "6f7a07b2-498c-6a33-4d0e-d760006f768c";
    private const string BuriedBooty = "bbbbbbbb-0000-0000-0000-000000000000";
    private const string SheepFarm = "5685c773-05ff-8b5d-06eb-3d79c04185c2";
    private const string BirdEnclosure = "95904502-656b-d1f4-624f-16dbe8fafa7d";

    private const string Footprints = $$"""
        {
          "about": "test",
          "footprints": {
            "{{TinySheepFarm}}": { "x": 12, "z": 12, "name": "Tiny Sheep Farm" },
            "{{BuriedBooty}}": { "x": 12, "z": 19, "status": "guess" },
            "{{SheepFarm}}": { "x": 12, "z": 12, "offset": { "x": 1, "z": 0 }, "name": "Sheep Farm" },
            "{{BirdEnclosure}}": { "x": 12, "z": 12, "offset": { "x": 0, "z": -3 }, "name": "Bird Enclosure" }
          }
        }
        """;

    private static AdventureFootprints Load(string content)
    {
        using var data = new TempStaticData();
        data.Write("adventures/footprints.json", content);
        return data.Load().AdventureFootprints;
    }

    [Fact]
    public void KnownTemplateGivesItsFootprint()
    {
        var footprints = Load(Footprints);

        Assert.Equal(4, footprints.Count);
        Assert.Equal(new AdventureFootprints.Footprint(12, 12), footprints.GetFootprint(TinySheepFarm));
        Assert.Equal(new AdventureFootprints.Footprint(12, 19), footprints.GetFootprint(BuriedBooty));
        Assert.Equal(new AdventureFootprints.Footprint(12, 12, new AdventureFootprints.FootprintOffset(1, 0)), footprints.GetFootprint(SheepFarm));
        Assert.Equal(new AdventureFootprints.Footprint(12, 12, new AdventureFootprints.FootprintOffset(0, -3)), footprints.GetFootprint(BirdEnclosure));
    }

    [Fact]
    public void UnknownTemplateHasNoFootprint()
    {
        Assert.Null(Load(Footprints).GetFootprint("00000000-0000-0000-0000-000000000000"));
    }

    [Fact]
    public void MissingFileThrowsStaticDataException()
    {
        using var data = new TempStaticData();

        var exception = Assert.Throws<StaticDataException>(() => data.Load().AdventureFootprints);
        Assert.True(exception.InnerException is FileNotFoundException or DirectoryNotFoundException);
    }

    [Theory]
    [InlineData("""{ "footprints": { "a": { "x": 12, """)]
    [InlineData("""{ "footprints": { "a": { "x": 0, "z": 12 } } }""")]
    [InlineData("""{ "footprints": { "a": { "x": 12, "z": -1 } } }""")]
    [InlineData("""{ "footprints": { "a": { "x": 12 } } }""")]
    [InlineData("""{ "footprints": { "a": { "x": "twelve", "z": 12 } } }""")]
    [InlineData("""{ "footprints": { "a": { "x": 12, "z": 12, "offset": { "x": 1 } } } }""")]
    [InlineData("""{ "footprints": { "a": { "x": 12, "z": 12, "offset": { "z": 1 } } } }""")]
    public void MalformedFileThrowsStaticDataException(string content)
    {
        Assert.Throws<StaticDataException>(() => Load(content));
    }

    [Fact]
    public void LoaderGivesNullWithoutTheFile()
    {
        using var data = new TempStaticData();

        Assert.Null(BuildplatesController.LoadAdventureFootprints(data.Load()));
    }

    [Fact]
    public void LoaderGivesNullForAMalformedFile()
    {
        using var data = new TempStaticData();
        data.Write("adventures/footprints.json", "{ not json");

        Assert.Null(BuildplatesController.LoadAdventureFootprints(data.Load()));
    }

    [Fact]
    public void LoaderGivesTheFootprints()
    {
        using var data = new TempStaticData();
        data.Write("adventures/footprints.json", Footprints);

        Assert.Equal(4, BuildplatesController.LoadAdventureFootprints(data.Load())?.Count);
    }

    [Theory]
    [InlineData(InstanceType.ENCOUNTER)]
    [InlineData(InstanceType.PLAYER_ADVENTURE)]
    public void AdventureInstanceCarriesTheWorldFootprint(InstanceType type)
    {
        var footprints = Load(Footprints);

        Assert.Equal(new Dimension(12, 12), BuildplatesController.InstanceDimension(type, TinySheepFarm, 16, footprints));
        Assert.Equal(new Dimension(12, 19), BuildplatesController.InstanceDimension(type, BuriedBooty, 16, footprints));
    }

    [Theory]
    [InlineData(InstanceType.ENCOUNTER)]
    [InlineData(InstanceType.PLAYER_ADVENTURE)]
    public void AdventureInstanceWithoutAFootprintKeepsTheTemplateSize(InstanceType type)
    {
        var footprints = Load(Footprints);

        Assert.Equal(new Dimension(16, 16), BuildplatesController.InstanceDimension(type, "00000000-0000-0000-0000-000000000000", 16, footprints));
        Assert.Equal(new Dimension(16, 16), BuildplatesController.InstanceDimension(type, TinySheepFarm, 16, null));
    }

    [Theory]
    [InlineData(InstanceType.ENCOUNTER)]
    [InlineData(InstanceType.PLAYER_ADVENTURE)]
    public void AdventureInstanceCarriesTheWorldOffset(InstanceType type)
    {
        var footprints = Load(Footprints);

        Assert.Equal(new Dimension(12, 12), BuildplatesController.InstanceDimension(type, SheepFarm, 16, footprints));
        Assert.Equal(new Offset(1, 35, 0), BuildplatesController.InstanceOffset(type, SheepFarm, 35, footprints));
        Assert.Equal(new Offset(0, 60, -3), BuildplatesController.InstanceOffset(type, BirdEnclosure, 60, footprints));
    }

    [Fact]
    public void AFootprintOffsetCanBeHalfABlock()
    {
        var footprints = Load($$"""
            { "footprints": { "{{TinySheepFarm}}": { "x": 8, "z": 8, "offset": { "x": 0, "z": -0.5 } } } }
            """);

        Assert.Equal(new Offset(0, 60, -0.5), BuildplatesController.InstanceOffset(InstanceType.PLAYER_ADVENTURE, TinySheepFarm, 60, footprints));
        Assert.Equal("{\"x\":0,\"y\":60,\"z\":-0.5}", Apace.Common.Json.Serialize(new Offset(0, 60, -0.5)));
    }

    [Fact]
    public void AFootprintCanShiftThePlateHeight()
    {
        var footprints = Load($$"""
            { "footprints": { "{{TinySheepFarm}}": { "x": 6, "z": 6, "offset": { "x": 0, "z": 0, "y": 1 } } } }
            """);

        Assert.Equal(new Offset(0, 61, 0), BuildplatesController.InstanceOffset(InstanceType.PLAYER_ADVENTURE, TinySheepFarm, 60, footprints));
    }

    [Theory]
    [InlineData(InstanceType.ENCOUNTER)]
    [InlineData(InstanceType.PLAYER_ADVENTURE)]
    public void AdventureInstanceWithoutAFootprintOffsetKeepsZeroXAndZ(InstanceType type)
    {
        var footprints = Load(Footprints);

        Assert.Equal(new Offset(0, 60, 0), BuildplatesController.InstanceOffset(type, TinySheepFarm, 60, footprints));
        Assert.Equal(new Offset(0, 60, 0), BuildplatesController.InstanceOffset(type, "00000000-0000-0000-0000-000000000000", 60, footprints));
        Assert.Equal(new Offset(0, 35, 0), BuildplatesController.InstanceOffset(type, SheepFarm, 35, null));
    }

    [Theory]
    [InlineData(InstanceType.BUILD)]
    [InlineData(InstanceType.PLAY)]
    [InlineData(InstanceType.SHARED_BUILD)]
    [InlineData(InstanceType.SHARED_PLAY)]
    public void OtherInstancesKeepTheirSize(InstanceType type)
    {
        var footprints = Load(Footprints);

        Assert.Equal(new Dimension(16, 16), BuildplatesController.InstanceDimension(type, TinySheepFarm, 16, footprints));
        Assert.Equal(new Offset(0, 35, 0), BuildplatesController.InstanceOffset(type, SheepFarm, 35, footprints));
    }
}
