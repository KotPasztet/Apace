#pragma warning disable IDE0130 // Namespace does not match folder structure
namespace Apace.Buildplate.Connector.Model;
#pragma warning restore IDE0130 // Namespace does not match folder structure

public sealed record AdventureChestLootMessage(
    string PlayerId,
    string Rarity,
    AdventureChestLootMessage.Item[] Items
)
{
    public const string EventType = "adventureChestLoot";

    public const string SharedBridgeQueueName = "buildplate_default";

    public static string QueueName(string instanceId)
        => "buildplate_" + instanceId;

    public static IEnumerable<string> QueueNames(string instanceId)
        => QueueName(instanceId) == SharedBridgeQueueName ? [SharedBridgeQueueName] : [QueueName(instanceId), SharedBridgeQueueName];

    public static AdventureChestLootMessage Create(string playerId, string rarity, IReadOnlyDictionary<string, int> items)
        => new(playerId, rarity, [.. items.Select(item => new Item(item.Key, item.Value))]);

    public sealed record Item(
        string ItemId,
        int Count
    );
}
