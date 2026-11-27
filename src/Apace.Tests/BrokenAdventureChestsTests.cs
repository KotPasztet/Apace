using Apace.ApiServer.Utils;

namespace Apace.Tests;

public sealed class BrokenAdventureChestsTests
{
    private const string InstanceId = "8c521fee-6a8b-475e-8784-3ee9183e5c28";
    private const long Start = 1_760_000_000_000;

    [Fact]
    public void SameChestIsOnlyAddedOnce()
    {
        var brokenAdventureChests = new BrokenAdventureChests(TimeSpan.FromHours(2));

        Assert.True(brokenAdventureChests.TryAdd(InstanceId, -2, 44, 1, Start));
        Assert.False(brokenAdventureChests.TryAdd(InstanceId, -2, 44, 1, Start));
        Assert.False(brokenAdventureChests.TryAdd(InstanceId, -2, 44, 1, Start + 1000));
        Assert.False(brokenAdventureChests.TryAdd(InstanceId, -2, 44, 1, Start + (long)TimeSpan.FromHours(2).TotalMilliseconds - 1));
    }

    [Fact]
    public void OtherPositionOrInstanceIsAnotherChest()
    {
        var brokenAdventureChests = new BrokenAdventureChests(TimeSpan.FromHours(2));

        Assert.True(brokenAdventureChests.TryAdd(InstanceId, -2, 44, 1, Start));
        Assert.True(brokenAdventureChests.TryAdd(InstanceId, -2, 45, 1, Start));
        Assert.True(brokenAdventureChests.TryAdd(InstanceId, -1, 44, 1, Start));
        Assert.True(brokenAdventureChests.TryAdd(InstanceId, -2, 44, 2, Start));
        Assert.True(brokenAdventureChests.TryAdd("f50bd1d7-1cb5-4ef2-b548-568f48e13e24", -2, 44, 1, Start));
        Assert.False(brokenAdventureChests.TryAdd("f50bd1d7-1cb5-4ef2-b548-568f48e13e24", -2, 44, 1, Start));
    }

    [Fact]
    public void ChestIsForgottenAfterTheExpiry()
    {
        var brokenAdventureChests = new BrokenAdventureChests(TimeSpan.FromMinutes(1));

        Assert.True(brokenAdventureChests.TryAdd(InstanceId, -2, 44, 1, Start));
        Assert.False(brokenAdventureChests.TryAdd(InstanceId, -2, 44, 1, Start + 59_999));
        Assert.True(brokenAdventureChests.TryAdd(InstanceId, -2, 44, 1, Start + 60_000));
        Assert.False(brokenAdventureChests.TryAdd(InstanceId, -2, 44, 1, Start + 60_001));
    }

    [Fact]
    public void ConcurrentReportsAddTheChestOnce()
    {
        var brokenAdventureChests = new BrokenAdventureChests(TimeSpan.FromHours(2));

        int added = 0;
        Parallel.For(0, 1000, index =>
        {
            if (brokenAdventureChests.TryAdd(InstanceId, -2, 44, 1, Start + (index % 10)))
            {
                Interlocked.Increment(ref added);
            }
        });

        Assert.Equal(1, added);
    }
}
