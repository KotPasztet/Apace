using Apace.ApiServer.Utils;
using static Apace.ApiServer.Utils.BuildplateInstancesManager;

namespace Apace.Tests;

public sealed class BuildplateInstanceReuseTests
{
    private static InstanceInfo Instance(InstanceType type, string? playerId, string? encounterId, bool shuttingDown = false)
        => new(type, "instance", playerId, encounterId, "template", "localhost", 19132, true, shuttingDown);

    [Fact]
    public void PlayerAdventureInstanceIsReusedForTheSameAdventure()
    {
        var instance = Instance(InstanceType.PLAYER_ADVENTURE, "player", "adventure-1");

        Assert.True(BuildplateInstancesManager.CanReuseInstance(instance, "player", "adventure-1", InstanceType.PLAYER_ADVENTURE));
    }

    [Fact]
    public void PlayerAdventureInstanceIsNotReusedForAnotherAdventureOnTheSameBuildplate()
    {
        var instance = Instance(InstanceType.PLAYER_ADVENTURE, "player", "adventure-1");

        Assert.False(BuildplateInstancesManager.CanReuseInstance(instance, "player", "adventure-2", InstanceType.PLAYER_ADVENTURE));
    }

    [Fact]
    public void BuildInstanceIsReusedForTheSamePlayer()
    {
        var instance = Instance(InstanceType.BUILD, "player", null);

        Assert.True(BuildplateInstancesManager.CanReuseInstance(instance, "player", null, InstanceType.BUILD));
    }

    [Fact]
    public void InstanceOfAnotherPlayerIsNotReused()
    {
        var instance = Instance(InstanceType.PLAYER_ADVENTURE, "player", "adventure-1");

        Assert.False(BuildplateInstancesManager.CanReuseInstance(instance, "someone-else", "adventure-1", InstanceType.PLAYER_ADVENTURE));
    }

    [Fact]
    public void InstanceOfAnotherTypeIsNotReused()
    {
        var instance = Instance(InstanceType.BUILD, "player", null);

        Assert.False(BuildplateInstancesManager.CanReuseInstance(instance, "player", null, InstanceType.PLAY));
    }

    [Fact]
    public void InstanceThatIsShuttingDownIsNotReused()
    {
        var instance = Instance(InstanceType.PLAYER_ADVENTURE, "player", "adventure-1", shuttingDown: true);

        Assert.False(BuildplateInstancesManager.CanReuseInstance(instance, "player", "adventure-1", InstanceType.PLAYER_ADVENTURE));
    }
}
