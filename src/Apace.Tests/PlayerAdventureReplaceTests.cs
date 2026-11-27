using System.Reflection;
using Apace.ApiServer.Utils;
using Apace.EventBus.Client;

namespace Apace.Tests;

public sealed class PlayerAdventureReplaceTests
{
    private const float Lat = 56.2029275f;
    private const float Lon = 10.2276822f;
    private const long Start = 1_000_000;

    private static TappablesManager NewManager()
        => (TappablesManager)typeof(TappablesManager)
            .GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, [typeof(EventBusClient)])!
            .Invoke([null]);

    private static TappablesManager.Adventure Place(TappablesManager manager, long time, string template, IReadOnlyCollection<string>? replacing = null)
        => manager.PlacePlayerAdventure("player", Lat, Lon, time, 60 * 60 * 1000, "icon", TappablesManager.Adventure.RarityE.COMMON, template, replacing ?? []);

    [Fact]
    public void SecondCrystalWithinAMinuteReturnsTheSameAdventure()
    {
        var manager = NewManager();
        var first = Place(manager, Start, "template-a");

        var second = Place(manager, Start + 8_000, "template-b");

        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public void ReplacingTheRecentAdventureWithinAMinutePlacesANewOne()
    {
        var manager = NewManager();
        var first = Place(manager, Start, "template-a");

        var second = Place(manager, Start + 8_000, "template-b", [first.Id]);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Null(manager.GetAdventureWithId(first.Id));
    }

    [Fact]
    public void RepeatedReplaceRequestReturnsTheReplacement()
    {
        var manager = NewManager();
        var first = Place(manager, Start, "template-a");
        var second = Place(manager, Start + 8_000, "template-b", [first.Id]);

        var retry = Place(manager, Start + 9_000, "template-b", [first.Id]);

        Assert.Equal(second.Id, retry.Id);
    }

    [Fact]
    public void RecentAdventureAtLocationIgnoresAReplacedAdventure()
    {
        var manager = NewManager();
        var first = Place(manager, Start, "template-a");

        Assert.Equal(first.Id, manager.GetRecentPlayerAdventureAtLocation("player", Lat, Lon, Start + 8_000, [])?.Id);
        Assert.Null(manager.GetRecentPlayerAdventureAtLocation("player", Lat, Lon, Start + 8_000, [first.Id]));
    }

    [Fact]
    public void RemovingPlayerAdventuresEndsTheCrystalAdventure()
    {
        var manager = NewManager();
        var crystal = Place(manager, Start, "template-a");

        string[] removed = manager.RemovePlayerAdventures("player");

        Assert.Equal([crystal.Id], removed);
        Assert.Null(manager.GetAdventureWithId(crystal.Id));
        Assert.Empty(manager.GetAllPlayerAdventures("player"));
        Assert.Null(manager.GetRecentPlayerAdventureAtLocation("player", Lat, Lon, Start + 1_000, []));
    }

    [Fact]
    public void RemovingPlayerAdventuresWithNoneChangesNothing()
    {
        var manager = NewManager();
        var mapAdventure = manager.PlaceAdventure(Lat, Lon, Start, 60 * 60 * 1000, "icon", TappablesManager.Adventure.RarityE.COMMON, "template-a");

        Assert.Empty(manager.RemovePlayerAdventures("player"));
        Assert.NotNull(manager.GetAdventureWithId(mapAdventure.Id));
    }

    [Fact]
    public void RemovingPlayerAdventuresLeavesMapAndOtherPlayersAlone()
    {
        var manager = NewManager();
        var mapAdventure = manager.PlaceAdventure(Lat, Lon + 0.01f, Start, 60 * 60 * 1000, "icon", TappablesManager.Adventure.RarityE.COMMON, "template-a");
        var other = manager.PlacePlayerAdventure("other", Lat + 0.01f, Lon, Start, 60 * 60 * 1000, "icon", TappablesManager.Adventure.RarityE.COMMON, "template-b", []);
        var crystal = Place(manager, Start, "template-c");

        string[] removed = manager.RemovePlayerAdventures("player");

        Assert.Equal([crystal.Id], removed);
        Assert.NotNull(manager.GetAdventureWithId(mapAdventure.Id));
        Assert.NotNull(manager.GetAdventureWithId(other.Id));
        Assert.Single(manager.GetAllPlayerAdventures("other"));
    }
}
