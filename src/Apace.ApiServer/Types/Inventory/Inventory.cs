namespace Apace.ApiServer.Types.Inventory;

internal sealed record Inventory(
    HotbarItem?[] Hotbar,
    StackableInventoryItem[] StackableItems,
    NonStackableInventoryItem[] NonStackableItems
);