using Apace.StaticData;

namespace Apace.Tests;

public sealed class EncountersConfigTests
{
    [Fact]
    public void ChancePerTileIsAHundredWithoutASpawnFile()
    {
        using var data = new TempStaticData();
        WriteEncounter(data);

        Assert.Equal(100, data.Load().EncountersConfig.ChancePerTile);
    }

    [Fact]
    public void ChancePerTileComesFromTheSpawnFile()
    {
        using var data = new TempStaticData();
        WriteEncounter(data);
        data.Write("encounters/encounters-spawn.json", """{ "chancePerTile": 4 }""");

        Assert.Equal(4, data.Load().EncountersConfig.ChancePerTile);
    }

    [Fact]
    public void ChancePerTileBelowOneIsAHundred()
    {
        using var data = new TempStaticData();
        WriteEncounter(data);
        data.Write("encounters/encounters-spawn.json", """{ "chancePerTile": 0 }""");

        Assert.Equal(100, data.Load().EncountersConfig.ChancePerTile);
    }

    [Fact]
    public void ChancePerTileIsAHundredWhenTheSpawnFileLeavesItOut()
    {
        using var data = new TempStaticData();
        WriteEncounter(data);
        data.Write("encounters/encounters-spawn.json", "{}");

        Assert.Equal(100, data.Load().EncountersConfig.ChancePerTile);
    }

    [Fact]
    public void UnreadableSpawnFileThrowsStaticDataException()
    {
        using var data = new TempStaticData();
        WriteEncounter(data);
        data.Write("encounters/encounters-spawn.json", "{");

        Assert.Throws<StaticDataException>(() => data.Load().EncountersConfig);
    }

    [Fact]
    public void SpawnFileIsNotReadAsAnEncounterEvenIfItLooksLikeOne()
    {
        using var data = new TempStaticData();
        WriteEncounter(data);
        data.Write("encounters/encounters-spawn.json", """{ "chancePerTile": 4, "adventureGroup": "rare", "spawnWeight": 50 }""");

        Assert.Equal("common", Assert.Single(data.Load().EncountersConfig.Encounters).AdventureGroup);
    }

    private static void WriteEncounter(TempStaticData data)
        => data.Write("encounters/common-encounter.json", """
            {
              "icon": "minecraft:adventure_crystal_common",
              "rarity": "COMMON",
              "duration": 900,
              "adventureGroup": "common",
              "spawnWeight": 50
            }
            """);
}
