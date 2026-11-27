using Apace.ApiServer.Utils;
using Apace.DB.Models.Common;
using Apace.DB.Models.Player;

namespace Apace.Tests;

public sealed class AdventureBackpacksTests
{
    private const string InstanceId = "8c521fee-6a8b-475e-8784-3ee9183e5c28";
    private const string PlayerId = "6bd8769afeba7d52";
    private const string Cobblestone = "cobblestone";
    private const string Dirt = "dirt";
    private const string Pickaxe = "stone_pickaxe";
    private const string Sword = "stone_sword";

    private static Inventory Owned()
    {
        var inventory = new Inventory();
        inventory.AddItems(Cobblestone, 64);
        inventory.AddItems(Dirt, 30);
        inventory.AddItems(Pickaxe, [new NonStackableItemInstance("p1", 10), new NonStackableItemInstance("p2", 0)]);
        inventory.AddItems(Sword, [new NonStackableItemInstance("s1", 0)]);
        return inventory;
    }

    private static Hotbar HotbarWith(params Hotbar.Item?[] items)
    {
        var hotbar = new Hotbar();
        for (int index = 0; index < items.Length; index++)
        {
            hotbar.Items[index] = items[index];
        }

        return hotbar;
    }

    [Fact]
    public void BackpackHoldsOnlyTheHotbar()
    {
        var backpacks = new AdventureBackpacks();
        backpacks.Open(InstanceId, PlayerId, HotbarWith(new Hotbar.Item(Cobblestone, 8, null), new Hotbar.Item(Pickaxe, 1, "p1"), new Hotbar.Item(Cobblestone, 4, null)));

        Inventory contents = backpacks.GetContents(InstanceId, PlayerId, Owned())!;

        Assert.Equal(12, contents.GetItemCount(Cobblestone));
        Assert.Equal(0, contents.GetItemCount(Dirt));
        Assert.Equal(["p1"], contents.GetItemInstances(Pickaxe).Select(instance => instance.InstanceId));
        Assert.Equal(10, contents.GetItemInstance(Pickaxe, "p1")!.Wear);
        Assert.Empty(contents.GetItemInstances(Sword));
    }

    [Fact]
    public void PickedUpItemsAreAddedAndUsedItemsRemoved()
    {
        var backpacks = new AdventureBackpacks();
        backpacks.Open(InstanceId, PlayerId, HotbarWith(new Hotbar.Item(Cobblestone, 8, null)));
        Inventory owned = Owned();

        owned.AddItems(Dirt, 3);
        backpacks.Add(InstanceId, PlayerId, Dirt, 3, null);
        owned.AddItems(Sword, [new NonStackableItemInstance("s2", 0)]);
        backpacks.Add(InstanceId, PlayerId, Sword, 1, "s2");
        owned.TakeItems(Cobblestone, 5);
        backpacks.Remove(InstanceId, PlayerId, Cobblestone, 5, null);

        Inventory contents = backpacks.GetContents(InstanceId, PlayerId, owned)!;

        Assert.Equal(3, contents.GetItemCount(Cobblestone));
        Assert.Equal(3, contents.GetItemCount(Dirt));
        Assert.Equal(["s2"], contents.GetItemInstances(Sword).Select(instance => instance.InstanceId));
    }

    [Fact]
    public void BackpackNeverShowsMoreThanIsOwned()
    {
        var backpacks = new AdventureBackpacks();
        backpacks.Open(InstanceId, PlayerId, HotbarWith(new Hotbar.Item(Cobblestone, 8, null), new Hotbar.Item(Pickaxe, 1, "p1")));
        backpacks.Remove(InstanceId, PlayerId, Cobblestone, 20, null);
        var owned = new Inventory();
        owned.AddItems(Cobblestone, 2);

        Inventory contents = backpacks.GetContents(InstanceId, PlayerId, owned)!;

        Assert.Equal(0, contents.GetItemCount(Cobblestone));
        Assert.Empty(contents.GetItemInstances(Pickaxe));
    }

    [Fact]
    public void NoBackpackWithoutOpen()
    {
        var backpacks = new AdventureBackpacks();
        backpacks.Add(InstanceId, PlayerId, Dirt, 3, null);

        Assert.Null(backpacks.GetContents(InstanceId, PlayerId, Owned()));
        backpacks.Open(InstanceId, PlayerId, HotbarWith());
        Assert.Null(backpacks.GetContents("f50bd1d7-1cb5-4ef2-b548-568f48e13e24", PlayerId, Owned()));
        Assert.Null(backpacks.GetContents(InstanceId, "another", Owned()));
    }

    [Fact]
    public void OpeningAgainKeepsTheBackpackAndCloseForgetsIt()
    {
        var backpacks = new AdventureBackpacks();
        backpacks.Open(InstanceId, PlayerId, HotbarWith(new Hotbar.Item(Cobblestone, 8, null)));
        backpacks.Add(InstanceId, PlayerId, Dirt, 3, null);
        backpacks.Open(InstanceId, PlayerId, HotbarWith());

        Assert.Equal(3, backpacks.GetContents(InstanceId, PlayerId, Owned())!.GetItemCount(Dirt));

        backpacks.Close(InstanceId, PlayerId);

        Assert.Null(backpacks.GetContents(InstanceId, PlayerId, Owned()));
    }
}
