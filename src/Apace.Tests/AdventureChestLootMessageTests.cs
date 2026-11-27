using Apace.Buildplate.Connector.Model;
using Apace.Common;

namespace Apace.Tests;

public sealed class AdventureChestLootMessageTests
{
    private const string CommonCrystal = "4f16a053-4929-263a-c91a-29663e29df76";
    private const string Cookie = "fd595ab4-a2c8-b278-caf7-91021fd32811";

    [Fact]
    public void ItemsAreListedWithTheirCatalogIdAndCount()
    {
        var message = AdventureChestLootMessage.Create("player-1", "rare", new Dictionary<string, int> { [CommonCrystal] = 1, [Cookie] = 2 });

        Assert.Equal("player-1", message.PlayerId);
        Assert.Equal("rare", message.Rarity);
        Assert.Equal(
            [new AdventureChestLootMessage.Item(CommonCrystal, 1), new AdventureChestLootMessage.Item(Cookie, 2)],
            message.Items);
    }

    [Fact]
    public void JsonUsesCamelCaseAndSurvivesTheEventBus()
    {
        var message = AdventureChestLootMessage.Create("pl\u00e6yer", "legendary", new Dictionary<string, int> { [Cookie] = 2 });

        string json = Json.Serialize(message);

        Assert.All(json, character => Assert.InRange(character, (char)32, (char)126));
        Assert.Contains("\"playerId\":", json);
        Assert.Contains("\"rarity\":\"legendary\"", json);
        Assert.Contains($"{{\"itemId\":\"{Cookie}\",\"count\":2}}", json);
        AdventureChestLootMessage? read = Json.Deserialize<AdventureChestLootMessage>(json);
        Assert.NotNull(read);
        Assert.Equal(message.PlayerId, read.PlayerId);
        Assert.Equal(message.Items, read.Items);
    }

    [Fact]
    public void EventTypeAndQueueMatchTheInstanceQueue()
    {
        Assert.Equal("adventureChestLoot", AdventureChestLootMessage.EventType);
        Assert.Equal("buildplate_abc", AdventureChestLootMessage.QueueName("abc"));
    }

    [Fact]
    public void LootGoesToTheInstanceQueueAndTheSharedBridgeQueue()
    {
        Assert.Equal(["buildplate_abc", "buildplate_default"], AdventureChestLootMessage.QueueNames("abc"));
        Assert.Equal(["buildplate_default"], AdventureChestLootMessage.QueueNames("default"));
    }
}
