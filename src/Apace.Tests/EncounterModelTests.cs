using Apace.ApiServer.Controllers.EarthApi;
using Apace.ApiServer.Types.Tappables;
using Apace.ApiServer.Utils;
using Apace.StaticData;
using Apace.TappablesGenerator;

namespace Apace.Tests;

public sealed class EncounterModelTests
{
    private const string PeacefulIcon = "genoa:adventure_generic_map";
    private const string HostileIcon = "genoa:adventure_generic_map_b";

    [Fact]
    public void HostileFileNamesTheHostileWorldsAndTheirIcon()
    {
        using var data = new TempStaticData();
        WriteConfig(data, "common");
        data.Write("encounters/encounters-hostile.json", $$"""{ "icon": "{{HostileIcon}}", "hostile": ["zzzz", "yyyy"] }""");

        EncountersConfig config = data.Load().EncountersConfig;

        Assert.True(config.IsHostile("zzzz"));
        Assert.True(config.IsHostile("yyyy"));
        Assert.False(config.IsHostile("aaaa"));
        Assert.Equal(HostileIcon, config.HostileIcon);
    }

    [Fact]
    public void WithoutAHostileFileEveryWorldIsPeaceful()
    {
        using var data = new TempStaticData();
        WriteConfig(data, "common");

        EncountersConfig config = data.Load().EncountersConfig;

        Assert.False(config.IsHostile("zzzz"));
        Assert.Null(config.HostileIcon);
    }

    [Fact]
    public void HostileFileIsNotReadAsAnEncounter()
    {
        using var data = new TempStaticData();
        WriteConfig(data, "common");
        data.Write("encounters/encounters-hostile.json", $$"""{ "icon": "{{HostileIcon}}", "hostile": ["zzzz"], "adventureGroup": "rare", "spawnWeight": 5 }""");

        Assert.Equal("common", Assert.Single(data.Load().EncountersConfig.Encounters).AdventureGroup);
    }

    [Fact]
    public void UnreadableHostileFileThrowsStaticDataException()
    {
        using var data = new TempStaticData();
        WriteConfig(data, "common");
        data.Write("encounters/encounters-hostile.json", "{");

        Assert.Throws<StaticDataException>(() => data.Load().EncountersConfig);
    }

    [Fact]
    public void HostileWorldGetsTheHostileIconAndFlag()
    {
        Encounter encounter = Spawn(data =>
        {
            WriteConfig(data, "rare");
            WritePool(data, "rare", "zzzz");
            data.Write("encounters/encounters-hostile.json", $$"""{ "icon": "{{HostileIcon}}", "hostile": ["zzzz"] }""");
        });

        Assert.Equal(HostileIcon, encounter.Icon);
        Assert.True(encounter.Hostile);
        Assert.Equal("zzzz", encounter.EncounterBuildplateId);
        Assert.Equal(Encounter.RarityE.RARE, encounter.Rarity);
    }

    [Fact]
    public void PeacefulWorldKeepsTheConfigIconAndIsNotHostile()
    {
        Encounter encounter = Spawn(data =>
        {
            WriteConfig(data, "epic");
            WritePool(data, "epic", "aaaa");
            data.Write("encounters/encounters-hostile.json", $$"""{ "icon": "{{HostileIcon}}", "hostile": ["zzzz"] }""");
        });

        Assert.Equal(PeacefulIcon, encounter.Icon);
        Assert.False(encounter.Hostile);
        Assert.Equal(Encounter.RarityE.EPIC, encounter.Rarity);
    }

    [Fact]
    public void EachWorldOfAMixedPoolCarriesItsOwnModel()
    {
        using var data = new TempStaticData();
        WriteConfig(data, "common");
        data.Write("adventures/common/common-buildplates.json", """{ "buildplates": [ { "templateId": "aaaa", "weight": 10 }, { "templateId": "zzzz", "weight": 10 } ] }""");
        data.Write("encounters/encounters-spawn.json", """{ "chancePerTile": 1 }""");
        data.Write("encounters/encounters-hostile.json", $$"""{ "icon": "{{HostileIcon}}", "hostile": ["zzzz"] }""");
        var generator = new EncounterGenerator(data.Load());

        List<Encounter> encounters = [];
        for (int i = 0; i < 200; i++)
        {
            encounters.AddRange(generator.GenerateEncounters(34000, 20000, 0));
        }

        Assert.Contains(encounters, e => e.EncounterBuildplateId == "aaaa");
        Assert.Contains(encounters, e => e.EncounterBuildplateId == "zzzz");
        Assert.All(encounters, e =>
        {
            bool hostile = e.EncounterBuildplateId == "zzzz";
            Assert.Equal(hostile, e.Hostile);
            Assert.Equal(hostile ? HostileIcon : PeacefulIcon, e.Icon);
        });
    }

    [Fact]
    public void HostileFlagSurvivesTheEventBusRoundTrip()
    {
        var spawned = new Encounter("id", 56.2f, 10.2f, 1, 2, HostileIcon, Encounter.RarityE.UNCOMMON, "zzzz", true);

        var received = Common.Json.Deserialize<TappablesManager.Encounter[]>(Common.Json.Serialize(new[] { spawned }));

        TappablesManager.Encounter encounter = Assert.Single(received!);
        Assert.True(encounter.Hostile);
        Assert.Equal(HostileIcon, encounter.Icon);
    }

    [Fact]
    public void EncounterSpawnedWithoutTheFlagIsPeaceful()
    {
        const string json = """[{"id":"id","lat":56.2,"lon":10.2,"spawnTime":1,"validFor":2,"icon":"genoa:adventure_generic_map","rarity":"COMMON","encounterBuildplateId":"aaaa"}]""";

        var received = Common.Json.Deserialize<TappablesManager.Encounter[]>(json);

        Assert.False(Assert.Single(received!).Hostile);
    }

    [Theory]
    [InlineData(true, ActiveLocation.EncounterMetadataR.EncounterTypeE.SHORT_4X4_HOSTILE)]
    [InlineData(false, ActiveLocation.EncounterMetadataR.EncounterTypeE.SHORT_4X4_PEACEFUL)]
    public void PinTypeFollowsTheFlag(bool hostile, ActiveLocation.EncounterMetadataR.EncounterTypeE expected)
    {
        var encounter = new TappablesManager.Encounter("id", 56.2f, 10.2f, 1, 2, PeacefulIcon, TappablesManager.Encounter.RarityE.COMMON, "aaaa", hostile);

        Assert.Equal(expected, TappablesController.GetEncounterType(encounter));
    }

    private static Encounter Spawn(Action<TempStaticData> write)
    {
        using var data = new TempStaticData();
        data.Write("encounters/encounters-spawn.json", """{ "chancePerTile": 1 }""");
        write(data);
        var generator = new EncounterGenerator(data.Load());
        return Assert.Single(generator.GenerateEncounters(34000, 20000, 0));
    }

    private static void WriteConfig(TempStaticData data, string rarity)
        => data.Write($"encounters/{rarity}.json", $$"""
            {
              "icon": "{{PeacefulIcon}}",
              "rarity": "{{rarity.ToUpperInvariant()}}",
              "duration": 600,
              "adventureGroup": "{{rarity}}",
              "spawnWeight": 1
            }
            """);

    private static void WritePool(TempStaticData data, string rarity, string templateId)
        => data.Write($"adventures/{rarity}/{rarity}-buildplates.json", $$"""{ "buildplates": [ { "templateId": "{{templateId}}", "weight": 10 } ] }""");
}
