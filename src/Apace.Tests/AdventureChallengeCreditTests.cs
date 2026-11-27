using Microsoft.Data.Sqlite;
using Apace.ApiServer.Controllers.EarthApi;
using Apace.ApiServer.Utils;
using Apace.DB;
using Apace.DB.Models.Player;

namespace Apace.Tests;

[Collection("Database")]
public sealed class AdventureChallengeCreditTests : IDisposable
{
    private const string PlayerId = "0a1b2c3d4e5f6071";
    private const string PlayAdventuresId = "e81d6ce8-622c-461a-a2d5-bb1f8ac36c62";
    private const string RareChestId = "ab2e0734-62b1-4383-a239-d1b4d4a93dc4";
    private const string SpiderKillsId = "a1394f77-20ba-44c2-b46c-e8d7933f2e51";

    private readonly DirectoryInfo _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "apace-adventure-credit-" + Guid.NewGuid().ToString("N")));
    private readonly EarthDB _earthDB;

    public AdventureChallengeCreditTests()
    {
        _earthDB = EarthDB.Open(Path.Combine(_root.FullName, "earth.db"));
    }

    public void Dispose()
    {
        _earthDB.Dispose();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        SqliteConnection.ClearAllPools();
        _root.Delete(true);
    }

    private async Task<(ChallengeProgressVersion Progress, Tokens Tokens)> LoadAsync()
    {
        EarthDB.Results results = await new EarthDB.Query(false)
            .Get("challenges", PlayerId, typeof(ChallengeProgressVersion))
            .Get("tokens", PlayerId, typeof(Tokens))
            .ExecuteAsync(_earthDB);
        return (results.Get<ChallengeProgressVersion>("challenges"), results.Get<Tokens>("tokens"));
    }

    private static int Season(ChallengeProgressVersion progress, string challengeId)
        => progress.GetSeasonChallengeProgress(challengeId);

    private async Task ActivateAsync(string challengeId, bool unlockAll = false)
    {
        var progress = new ChallengeProgressVersion { ActiveSeasonChallengeId = challengeId };
        if (unlockAll)
        {
            progress.ClaimedChallengeIds.UnionWith(ChallengesController.SeasonChallengeIds().Where(id => id != challengeId));
        }

        await new EarthDB.Query(true)
            .Update("challenges", PlayerId, progress)
            .ExecuteAsync(_earthDB);
    }

    [Fact]
    public async Task TwoBrokenAdventureChestsCompleteThePlayAdventuresChallenge()
    {
        await ActivateAsync(PlayAdventuresId);
        Assert.True(await ChallengeEvents.RecordAdventureCompletedAsync(PlayerId, _earthDB));
        Assert.True(await ChallengeEvents.RecordAdventureCompletedAsync(PlayerId, _earthDB));

        var (progress, tokens) = await LoadAsync();

        Assert.Equal(2, Season(progress, PlayAdventuresId));
        Assert.Contains(tokens.GetTokens(), token => token.Token is Tokens.ChallengeCompletedToken);
    }

    [Fact]
    public async Task ALockedSeasonChallengeDoesNotCount()
    {
        ChallengeEvents.EventRule rule = ChallengeEvents.GetEventRule("spider_kill")!;
        await ActivateAsync(SpiderKillsId);
        var (progress, _) = await LoadAsync();
        Assert.False(ChallengesController.GetSeasonChallengeStatus(progress, SpiderKillsId).Unlocked);

        Assert.True(await ChallengeEvents.RecordChallengeEventAsync(PlayerId, "spider_kill", rule, _earthDB));

        var (after, tokens) = await LoadAsync();
        Assert.Equal(0, Season(after, SpiderKillsId));
        Assert.DoesNotContain(tokens.GetTokens(), token => token.Token is Tokens.ChallengeProgressToken or Tokens.ChallengeCompletedToken);
    }

    [Fact]
    public async Task AnAdventureDoesNotCountWhileAnotherChallengeIsActive()
    {
        await ActivateAsync("edf9f1dc-f73f-4275-a3e5-e1d9d861d158");

        Assert.True(await ChallengeEvents.RecordAdventureCompletedAsync(PlayerId, _earthDB));

        var (progress, tokens) = await LoadAsync();
        Assert.Equal(0, Season(progress, PlayAdventuresId));
        Assert.Empty(tokens.GetTokens());
    }

    [Fact]
    public async Task ARareChestCountsForTheRareChestChallenge()
    {
        ChallengeEvents.EventRule rule = ChallengeEvents.GetEventRule("adventure_rare_chest")!;
        await ActivateAsync(RareChestId, unlockAll: true);

        Assert.True(await ChallengeEvents.RecordChallengeEventAsync(PlayerId, "adventure_rare_chest", rule, _earthDB));

        var (progress, _) = await LoadAsync();
        Assert.Equal(1, Season(progress, RareChestId));
    }
}
