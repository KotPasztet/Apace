using Apace.StaticData;
using Item = Apace.StaticData.AdventureLoot.Item;

namespace Apace.Tests;

public sealed class AdventureLootTests
{
    private const int Rolls = 200_000;

    [Fact]
    public void EveryItemDropsWithItsOwnChance()
    {
        Item[] items =
        [
            new("crystal-common", "Common Adventure Crystal", 84.4, Group: "crystal"),
            new("crystal-uncommon", "Uncommon Adventure Crystal", 15.6, Group: "crystal"),
            new("wooden-axe", "Wooden Axe", 5.8),
            new("terracotta", "Terracotta", 3.5),
            new("carrot", "Carrot", 3.8, 1, 3),
            new("rare-thing", "Rare Thing", 0.6),
            new("common-thing", "Common Thing", 47.0, 2, 4),
            // the same item on two rows rolls twice
            new("terracotta", "Black Terracotta", 1.7),
            new("either-a", "Either A", 30.0, Group: "either"),
            new("either-b", "Either B", 20.0, Group: "either"),
        ];

        var table = AdventureLoot.Table.FromItems(items);
        int[] dropped = new int[items.Length];
        int neitherOfEither = 0;
        var random = new Random(1234);
        for (int roll = 0; roll < Rolls; roll++)
        {
            IReadOnlyList<AdventureLoot.Drop> drops = table.RollDrops(random);
            foreach (AdventureLoot.Drop drop in drops)
            {
                dropped[Array.IndexOf(items, drop.Item)]++;
            }

            if (!drops.Any(drop => drop.Item.Group == "either"))
            {
                neitherOfEither++;
            }
        }

        for (int index = 0; index < items.Length; index++)
        {
            AssertChance(items[index].Chance / 100.0, dropped[index], $"{items[index].Name}");
        }

        AssertChance(0.5, neitherOfEither, "neither of the group under 100");
    }

    [Theory]
    [InlineData(84.4, 15.6)]
    [InlineData(93.7, 6.3)]
    [InlineData(96.5, 3.5)]
    [InlineData(100.0, 0.0)]
    public void ExactlyOneCrystalPerChest(double first, double second)
    {
        List<Item> items =
        [
            new("bow", "Bow", 8.9, 1, 2),
            new("crystal-a", "Crystal A", first, Group: "crystal"),
            new("cookie", "Cookie", 5.6, 1, 2),
        ];
        if (second > 0)
        {
            items.Add(new("crystal-b", "Crystal B", second, Group: "crystal"));
        }

        var table = AdventureLoot.Table.FromItems(items);
        int firstCrystals = 0;
        var random = new Random(99);
        for (int roll = 0; roll < Rolls; roll++)
        {
            var crystals = table.RollDrops(random).Where(drop => drop.Item.Group == "crystal").ToArray();
            var crystal = Assert.Single(crystals);
            Assert.Equal(1, crystal.Count);
            if (crystal.Item.Id == "crystal-a")
            {
                firstCrystals++;
            }
        }

        AssertChance(first / 100.0, firstCrystals, "first crystal");
    }

    [Fact]
    public void CountsStayWithinMinAndMax()
    {
        Item[] items =
        [
            new("one", "One", 100.0),
            new("one-to-three", "One to three", 100.0, 1, 3),
            new("two-to-seven", "Two to seven", 100.0, 2, 7),
            new("six-to-eight", "Six to eight", 50.0, 6, 8),
        ];

        var table = AdventureLoot.Table.FromItems(items);
        Dictionary<string, HashSet<int>> seen = items.ToDictionary(item => item.Id, item => new HashSet<int>());
        var random = new Random(7);
        for (int roll = 0; roll < 20_000; roll++)
        {
            foreach (AdventureLoot.Drop drop in table.RollDrops(random))
            {
                Assert.InRange(drop.Count, drop.Item.Min, drop.Item.Max);
                seen[drop.Item.Id].Add(drop.Count);
            }
        }

        foreach (Item item in items)
        {
            Assert.Equal(Enumerable.Range(item.Min, item.Max - item.Min + 1), seen[item.Id].Order());
        }
    }

    [Fact]
    public void SameSeedRollsTheSameChest()
    {
        var table = AdventureLoot.Table.FromItems(
        [
            new("a", "A", 50.0, 1, 3),
            new("b", "B", 50.0, Group: "g"),
            new("c", "C", 50.0, Group: "g"),
        ]);

        var first = new Random(42);
        var second = new Random(42);
        for (int roll = 0; roll < 100; roll++)
        {
            Assert.Equal(table.RollDrops(first), table.RollDrops(second));
        }
    }

    [Fact]
    public void TablesAreReadPerRarity()
    {
        using var data = new TempStaticData();
        data.Write("adventures/common/common-loot.json", """
            {
              "source": "ignored",
              "items": [
                { "id": "4f16a053-4929-263a-c91a-29663e29df76", "name": "Common Adventure Crystal", "chance": 84.4, "min": 1, "max": 1, "group": "crystal", "uncertain": false },
                { "id": "81a84b7e-928f-7157-254c-6543e90dbc59", "name": "Uncommon Adventure Crystal", "chance": 15.6, "group": "crystal" },
                { "id": "5b81110c-1152-d4d4-a7c6-849fe93795d8", "name": "Red Sand", "chance": 2.6, "min": 2, "max": 4, "uncertain": true }
              ]
            }
            """);
        data.Write("adventures/rare/rare-loot.json", """
            { "items": [ { "id": "123dd088-ba76-71ce-b40c-2f05b948f303", "name": "Rare Adventure Crystal", "chance": 100, "group": "crystal" } ] }
            """);
        data.Write("adventures/common/common-buildplates.json", """{ "buildplates": [] }""");

        AdventureLoot loot = data.Load().AdventureLoot;

        Assert.Empty(loot.Errors);
        Assert.Equal(["common", "rare"], loot.Tables.Keys.Order(StringComparer.Ordinal));
        AdventureLoot.Table common = loot.GetTable("COMMON")!;
        Assert.Same(common, loot.GetTable("Common"));
        Assert.Equal(3, common.Items.Length);
        Assert.Equal(new Item("5b81110c-1152-d4d4-a7c6-849fe93795d8", "Red Sand", 2.6, 2, 4, null, true), common.Items[2]);
        Assert.Equal(new Item("81a84b7e-928f-7157-254c-6543e90dbc59", "Uncommon Adventure Crystal", 15.6, 1, 1, "crystal", false), common.Items[1]);
        Assert.Null(loot.GetTable("uncommon"));
        Assert.Null(loot.GetTable("legendary"));
    }

    [Theory]
    [InlineData("""{ "items": [ { "id": "a", "chance": 60, "group": "crystal" }, { "id": "b", "chance": 41, "group": "crystal" } ] }""")]
    [InlineData("""{ "items": [ { "id": "a", "chance": 10, "min": 3, "max": 2 } ] }""")]
    [InlineData("""{ "items": [ { "id": "a", "chance": 10, "min": 0, "max": 2 } ] }""")]
    [InlineData("""{ "items": [ { "id": "a", "chance": 0 } ] }""")]
    [InlineData("""{ "items": [ { "id": "a", "chance": 100.5 } ] }""")]
    [InlineData("""{ "items": [ { "id": "", "chance": 10 } ] }""")]
    [InlineData("""{ "items": [] }""")]
    [InlineData("""{ "items": [ { "id": "a", "chance": "lots" } ] }""")]
    [InlineData("""{ "items": [ { "id": "a", "chance": 10 } """)]
    [InlineData("")]
    public void BrokenTableOnlyTakesOutItsOwnRarity(string brokenFile)
    {
        using var data = new TempStaticData();
        data.Write("adventures/common/common-loot.json", brokenFile);
        data.Write("adventures/rare/rare-loot.json", """{ "items": [ { "id": "r", "chance": 100, "group": "crystal" } ] }""");

        AdventureLoot loot = data.Load().AdventureLoot;

        Assert.Null(loot.GetTable("common"));
        Assert.Contains("common", loot.Errors.Keys);
        Assert.NotNull(loot.GetTable("rare"));
    }

    [Fact]
    public void NoAdventuresFolderMeansNoTables()
    {
        using var data = new TempStaticData();

        AdventureLoot loot = data.Load().AdventureLoot;

        Assert.Empty(loot.Tables);
        Assert.Empty(loot.Errors);
    }

    private static void AssertChance(double expected, int hits, string what)
    {
        double actual = (double)hits / Rolls;
        double tolerance = 5.0 * Math.Sqrt(expected * (1.0 - expected) / Rolls) + 1e-9;
        Assert.True(Math.Abs(actual - expected) <= tolerance, $"{what}: expected {expected:P3}, got {actual:P3} (tolerance {tolerance:P3})");
    }
}
