using Microsoft.Data.Sqlite;
using Apace.ApiServer.Utils;
using Apace.DB;
using Apace.DB.Models.Player;

namespace Apace.Tests;

[Collection("Database")]
public sealed class AdventureChestRewardsTests : IDisposable
{
    private const string PlayerId = "0a1b2c3d4e5f6071";
    private const string CommonCrystal = "4f16a053-4929-263a-c91a-29663e29df76";
    private const string UncommonCrystal = "81a84b7e-928f-7157-254c-6543e90dbc59";
    private const string Cookie = "fd595ab4-a2c8-b278-caf7-91021fd32811";
    private const string WoodenAxe = "7f99f866-55e9-8c16-463d-33ec18ccf535";

    private readonly TempStaticData _data = new();
    private readonly EarthDB _earthDB;

    public AdventureChestRewardsTests()
    {
        WriteCatalog(_data);
        _earthDB = EarthDB.Open(Path.Combine(_data.Root, "earth.db"));
    }

    public void Dispose()
    {
        _earthDB.Dispose();
        ReleaseDatabaseFiles();
        _data.Dispose();
    }

    // EarthDB keeps connections open after a commit
    private static void ReleaseDatabaseFiles()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task ChestGivesOneTokenForTheInstance()
    {
        WriteLoot("common", $$"""{ "id": "{{CommonCrystal}}", "chance": 100, "group": "crystal" }, { "id": "{{Cookie}}", "chance": 100, "min": 2, "max": 2 }""");

        bool given = await GiveAsync("instance-a", "COMMON");

        Assert.True(given);
        var (id, token) = Assert.Single(await GetAdventureTokensAsync());
        Assert.Equal("instance-a", token.InstanceId);
        Assert.Equal(new Dictionary<string, int?> { [CommonCrystal] = 1, [Cookie] = 2 }, token.Rewards.Items);
        Assert.Equal(0, (await GetInventoryAsync()).GetItemCount(Cookie));
        Assert.True(Guid.TryParse(id, out _));
    }

    [Fact]
    public async Task SecondChestInTheSameInstanceAddsToItsToken()
    {
        WriteLoot("common", $$"""{ "id": "{{CommonCrystal}}", "chance": 100, "group": "crystal" }, { "id": "{{Cookie}}", "chance": 100 }""");

        Assert.True(await GiveAsync("instance-a", "common"));
        string firstId = Assert.Single(await GetAdventureTokensAsync()).Id;
        Assert.True(await GiveAsync("instance-a", "common"));

        var (id, token) = Assert.Single(await GetAdventureTokensAsync());
        Assert.Equal(firstId, id);
        Assert.Equal(new Dictionary<string, int?> { [CommonCrystal] = 2, [Cookie] = 2 }, token.Rewards.Items);

        Assert.True(await GiveAsync("instance-b", "common"));

        var tokens = await GetAdventureTokensAsync();
        Assert.Equal(2, tokens.Length);
        Assert.Equal(["instance-a", "instance-b"], tokens.Select(token => token.Token.InstanceId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RedeemingTheTokenGivesTheLoot()
    {
        WriteLoot("common", $$"""{ "id": "{{UncommonCrystal}}", "chance": 100, "group": "crystal" }, { "id": "{{Cookie}}", "chance": 100, "min": 3, "max": 3 }, { "id": "{{WoodenAxe}}", "chance": 100 }""");
        Assert.True(await GiveAsync("instance-a", "common"));
        string tokenId = Assert.Single(await GetAdventureTokensAsync()).Id;

        // as TokensController.Redeem does it
        await new EarthDB.Query(true)
            .Get("tokens", PlayerId, typeof(Tokens))
            .Then(results =>
            {
                Tokens tokens = results.Get<Tokens>("tokens");
                Tokens.Token token = tokens.RemoveToken(tokenId)!;
                return new EarthDB.Query(true)
                    .Update("tokens", PlayerId, tokens)
                    .Then(TokenUtils.DoActionsOnRedeemedToken(token, PlayerId, 1_000, _data.Load()), false);
            })
            .ExecuteAsync(_earthDB);

        Inventory inventory = await GetInventoryAsync();
        Assert.Equal(1, inventory.GetItemCount(UncommonCrystal));
        Assert.Equal(3, inventory.GetItemCount(Cookie));
        Assert.Single(inventory.GetItemInstances(WoodenAxe));
        Assert.Empty(await GetAdventureTokensAsync());

        // items new to the journal unlock their entry, as with any other reward
        Tokens remaining = await GetTokensAsync();
        Assert.Equal(
            [WoodenAxe, Cookie],
            remaining.GetTokens().Select(token => Assert.IsType<Tokens.JournalItemUnlockedToken>(token.Token).ItemId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RarityWithoutLootGivesNothing()
    {
        WriteLoot("common", $$"""{ "id": "{{CommonCrystal}}", "chance": 100, "group": "crystal" }""");

        Assert.False(await GiveAsync("instance-a", "legendary"));
        Assert.False(await GiveAsync("instance-a", "legendary"));

        Assert.Empty(await GetAdventureTokensAsync());
    }

    [Fact]
    public async Task BrokenLootFileGivesNothing()
    {
        _data.Write("adventures/common/common-loot.json", """{ "items": [ { "id": "a", "chance": 101 } ] }""");

        Assert.False(await GiveAsync("instance-a", "common"));

        Assert.Empty(await GetAdventureTokensAsync());
    }

    [Fact]
    public async Task ItemMissingFromTheCatalogIsLeftOut()
    {
        WriteLoot("common", $$"""{ "id": "{{CommonCrystal}}", "chance": 100, "group": "crystal" }, { "id": "00000000-0000-0000-0000-000000000000", "chance": 100 }""");

        Assert.True(await GiveAsync("instance-a", "common"));

        var (_, token) = Assert.Single(await GetAdventureTokensAsync());
        Assert.Equal(new Dictionary<string, int?> { [CommonCrystal] = 1 }, token.Rewards.Items);
    }

    [Fact]
    public async Task ChestWithNothingGrantableGivesNoToken()
    {
        WriteLoot("common", """{ "id": "00000000-0000-0000-0000-000000000000", "chance": 100 }""");

        Assert.False(await GiveAsync("instance-a", "common"));

        Assert.Empty(await GetAdventureTokensAsync());
    }

    [Fact]
    public async Task DatabaseErrorIsNotThrown()
    {
        WriteLoot("common", $$"""{ "id": "{{CommonCrystal}}", "chance": 100, "group": "crystal" }""");
        string path = Path.Combine(_data.Root, "gone.db");
        using var goneDB = EarthDB.Open(path);
        ReleaseDatabaseFiles();
        File.Delete(path);
        Directory.CreateDirectory(path);

        Assert.False(await AdventureChestRewards.GiveAsync(goneDB, _data.Load(), new Random(1), PlayerId, "instance-a", "common", 1_000));
    }

    [Fact]
    public async Task StaticDataErrorIsNotThrown()
    {
        WriteLoot("common", $$"""{ "id": "{{CommonCrystal}}", "chance": 100, "group": "crystal" }""");
        File.Delete(Path.Combine(_data.Root, "catalog", "items.json"));

        Assert.False(await GiveAsync("instance-a", "common"));
    }

    [Fact]
    public async Task GivingReturnsTheLootThatWasRolled()
    {
        WriteLoot("common", $$"""{ "id": "{{CommonCrystal}}", "chance": 100, "group": "crystal" }, { "id": "{{Cookie}}", "chance": 100, "min": 2, "max": 2 }""");

        IReadOnlyDictionary<string, int>? items = await AdventureChestRewards.RollAndGiveAsync(_earthDB, _data.Load(), new Random(1), PlayerId, "instance-a", "common", 1_000);

        Assert.Equal(new Dictionary<string, int> { [CommonCrystal] = 1, [Cookie] = 2 }, items);
    }

    [Fact]
    public async Task GivingReturnsNothingForARarityWithoutLoot()
    {
        WriteLoot("common", $$"""{ "id": "{{CommonCrystal}}", "chance": 100, "group": "crystal" }""");

        Assert.Null(await AdventureChestRewards.RollAndGiveAsync(_earthDB, _data.Load(), new Random(1), PlayerId, "instance-a", "legendary", 1_000));
    }

    private Task<bool> GiveAsync(string instanceId, string rarity)
        => AdventureChestRewards.GiveAsync(_earthDB, _data.Load(), new Random(1), PlayerId, instanceId, rarity, 1_000);

    private void WriteLoot(string rarity, string items)
        => _data.Write($"adventures/{rarity}/{rarity}-loot.json", $$"""{ "items": [ {{items}} ] }""");

    private async Task<Tokens> GetTokensAsync()
        => (await new EarthDB.Query(false).Get("tokens", PlayerId, typeof(Tokens)).ExecuteAsync(_earthDB)).Get<Tokens>("tokens");

    private async Task<(string Id, Tokens.AdventureRewardToken Token)[]> GetAdventureTokensAsync()
        => [.. (await GetTokensAsync()).GetTokens()
            .Where(token => token.Token is Tokens.AdventureRewardToken)
            .Select(token => (token.Id, (Tokens.AdventureRewardToken)token.Token))];

    private async Task<Inventory> GetInventoryAsync()
        => (await new EarthDB.Query(false).Get("inventory", PlayerId, typeof(Inventory)).ExecuteAsync(_earthDB)).Get<Inventory>("inventory");

    // the real catalog entries of the items used here, and nothing else
    private static void WriteCatalog(TempStaticData data)
    {
        data.Write("catalog/items.json", $$$"""
            [
              {"id": "{{{CommonCrystal}}}", "name": "adventure_crystal_common", "aux": 0, "stackable": true, "type": "ADVENTURE_SCROLL", "category": "ITEMS", "rarity": "COMMON", "useType": "CONSUME", "alternativeUseType": "NONE", "blockInfo": null, "toolInfo": null, "consumeInfo": null, "fuelInfo": null, "projectileInfo": null, "mobInfo": null, "boostInfo": null, "journalEntry": null, "experience": {"tappable": 20, "encounter": 0, "crafting": 0, "journal": 0}},
              {"id": "{{{UncommonCrystal}}}", "name": "adventure_crystal_uncommon", "aux": 0, "stackable": true, "type": "ADVENTURE_SCROLL", "category": "ITEMS", "rarity": "UNCOMMON", "useType": "CONSUME", "alternativeUseType": "NONE", "blockInfo": null, "toolInfo": null, "consumeInfo": null, "fuelInfo": null, "projectileInfo": null, "mobInfo": null, "boostInfo": null, "journalEntry": null, "experience": {"tappable": 0, "encounter": 0, "crafting": 0, "journal": 0}},
              {"id": "{{{Cookie}}}", "name": "cookie", "aux": 0, "stackable": true, "type": "BOOST", "category": "EQUIPMENT", "rarity": "UNCOMMON", "useType": "CONSUME", "alternativeUseType": "NONE", "blockInfo": null, "toolInfo": null, "consumeInfo": {"heal": 4, "returnItemId": null}, "fuelInfo": null, "projectileInfo": null, "mobInfo": null, "boostInfo": {"name": "cookie", "level": null, "type": "INVENTORY_ITEM", "canBeRemoved": false, "duration": 0, "triggeredOnDeath": false, "effects": [{"type": "HEALING", "value": 4, "applicableItemIds": [], "activation": "INSTANT"}]}, "journalEntry": {"group": "food", "order": 357, "biome": "NONE", "behavior": "NONE", "sound": null}, "experience": {"tappable": 1, "encounter": 0, "crafting": 0, "journal": 0}},
              {"id": "{{{WoodenAxe}}}", "name": "wooden_axe", "aux": 0, "stackable": false, "type": "TOOL", "category": "EQUIPMENT", "rarity": "COMMON", "useType": "DESTROY", "alternativeUseType": "BUILD", "blockInfo": null, "toolInfo": {"blockDamage": 2, "mobDamage": 4, "maxWear": 528, "efficiencyCategory": "axe"}, "consumeInfo": null, "fuelInfo": {"burnTime": 30, "heatPerSecond": 50}, "projectileInfo": null, "mobInfo": null, "boostInfo": null, "journalEntry": {"group": "wooden tools", "order": 271, "biome": "NONE", "behavior": "NONE", "sound": null}, "experience": {"tappable": 10, "encounter": 0, "crafting": 0, "journal": 0}}
            ]
            """);
        data.Write("catalog/itemEfficiencyCategories.json", "[]");
        data.Write("catalog/itemJournalGroups.json", "[]");
        data.Write("catalog/recipes.json", """{ "crafting": [], "smelting": [] }""");
        data.Write("catalog/nfc.json", """{ "miniFigs": [] }""");
    }
}
