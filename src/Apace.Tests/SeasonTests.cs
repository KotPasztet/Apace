using System.Reflection;
using System.Text.Json.Nodes;
using Apace.ApiServer.Controllers.EarthApi;
using Apace.ApiServer.Utils;
using Apace.Common;
using Apace.DB;
using Apace.DB.Models.Player;

namespace Apace.Tests;

public sealed class SeasonTests
{
    private const string PlayerId = "6bd8769afeba7d52";
    private const string PlayAdventuresId = "e81d6ce8-622c-461a-a2d5-bb1f8ac36c62";
    private const string SpiderSwordId = "8e052786-0495-4eb9-80bf-e1b36b9d89ef";
    private const string OakLogsId = "761c6754-ad1f-4cbf-a116-8a1e1dd611b9";
    private const string FinalId = "152a875e-2389-4245-8e1f-08f1915d6c7a";
    private const string SignInId = "d4111000-0000-4000-8000-000000000001";
    private const long Day = 24L * 60 * 60 * 1000;

    private static long At(string utc)
        => DateTimeOffset.Parse(utc, null, System.Globalization.DateTimeStyles.AssumeUniversal).ToUnixTimeMilliseconds();

    private static string Reference(string challengeId)
        => ChallengesController.GetSeasonChallengeReferenceId(challengeId)!;

    private static JsonObject List(ChallengeProgressVersion progress, long now)
    {
        progress.EnsureDate(now);
        string end = TimeFormatter.FormatTime(progress.GetSeasonEndsAt());
        var listing = ChallengesController.BuildChallengeListing(PlayerId, progress, new TokenClaims(), now, end, end);
        return JsonNode.Parse(Json.Serialize(listing.Challenges))!.AsObject();
    }

    private static ChallengeProgressVersion FromDbJson(string json)
    {
        MethodInfo fromJson = typeof(EarthDB).GetMethod("FromJson", BindingFlags.NonPublic | BindingFlags.Static, [typeof(string), typeof(Type)])!;
        return (ChallengeProgressVersion)fromJson.Invoke(null, [json, typeof(ChallengeProgressVersion)])!;
    }

    [Fact]
    public void ChallengesSavedBeforeSeasonsStillLoad()
    {
        const string saved = """
            {"updatedAt":1763900000000,"dailyDateUtc":"2026-11-25","activeSeasonId":"season_17","activeSeasonChallengeId":"e81d6ce8-622c-461a-a2d5-bb1f8ac36c62","tappablesRedeemed":4,"objectiveCounts":{"6b0655aa-cc63-4876-a1e1-afb319403c1c":4},"claimedChallengeIds":["f0532069-a70a-4a01-8611-f770bb46d9cd"],"removedContinuousChallengeIds":[]}
            """;

        ChallengeProgressVersion progress = FromDbJson(saved);

        Assert.Equal(0, progress.SeasonStartedAt);
        Assert.Empty(progress.SeasonObjectiveCounts);
        Assert.Empty(progress.SeasonChallengeCounts);
        Assert.Empty(progress.SeasonEventFlags);
        Assert.Equal(4, progress.GetObjectiveProgress("6b0655aa-cc63-4876-a1e1-afb319403c1c"));

        progress.EnsureDate(At("2026-11-25T18:30:00Z"));

        Assert.Equal(At("2026-11-25T00:00:00Z"), progress.SeasonStartedAt);
        Assert.Equal(At("2026-12-09T00:00:00Z"), progress.GetSeasonEndsAt());
        Assert.Equal(4, progress.TappablesRedeemed);
        Assert.Contains("f0532069-a70a-4a01-8611-f770bb46d9cd", progress.ClaimedChallengeIds);
    }

    [Fact]
    public void SeasonEndCountsDownInsteadOfMovingWithEveryDay()
    {
        var progress = new ChallengeProgressVersion();
        progress.EnsureDate(At("2026-11-25T18:30:00Z"));
        long end = progress.GetSeasonEndsAt();

        progress.EnsureDate(At("2026-11-30T09:00:00Z"));

        Assert.Equal(end, progress.GetSeasonEndsAt());
    }

    [Fact]
    public void SeasonProgressSurvivesMidnightAndIsKeptApartFromDailyCounts()
    {
        string reference = Reference(OakLogsId);
        var progress = new ChallengeProgressVersion();
        progress.AddSeasonChallengeProgress(At("2026-11-25T20:00:00Z"), OakLogsId, 3);
        progress.AddObjectiveProgress(At("2026-11-25T20:00:00Z"), reference, 5);
        Assert.Equal(3, progress.GetSeasonChallengeProgress(OakLogsId));
        Assert.Equal(5, progress.GetObjectiveProgress(reference));

        progress.EnsureDate(At("2026-11-26T08:00:00Z"));

        Assert.Equal(3, progress.GetSeasonChallengeProgress(OakLogsId));
        Assert.Equal(0, progress.GetObjectiveProgress(reference));
    }

    [Fact]
    public void AnEndedSeasonRestartsWhereTheOldRunEnded()
    {
        var progress = new ChallengeProgressVersion();
        progress.EnsureDate(At("2026-11-01T12:00:00Z"));
        progress.AddSeasonChallengeProgress(At("2026-11-01T12:00:00Z"), OakLogsId, 2);
        progress.SeasonEventFlags.Add("event:leaves_oak");
        progress.ClaimedChallengeIds.UnionWith([PlayAdventuresId, SignInId]);
        progress.ActiveSeasonChallengeId = SpiderSwordId;

        progress.EnsureDate(At("2026-12-10T12:00:00Z"));

        Assert.Equal(At("2026-11-29T00:00:00Z"), progress.SeasonStartedAt);
        Assert.Empty(progress.SeasonChallengeCounts);
        Assert.Empty(progress.SeasonEventFlags);
        Assert.DoesNotContain(PlayAdventuresId, progress.ClaimedChallengeIds);
        Assert.Contains(SignInId, progress.ClaimedChallengeIds);
        Assert.Null(progress.ActiveSeasonChallengeId);
    }

    [Fact]
    public void ALockedChallengeShowsNoProgressAndUnlocksNothing()
    {
        long now = At("2026-11-25T12:00:00Z");
        var progress = new ChallengeProgressVersion();
        progress.AddSeasonChallengeProgress(now, OakLogsId, 8);

        JsonObject challenges = List(progress, now);

        Assert.Equal("Locked", challenges[OakLogsId]!["state"]!.GetValue<string>());
        Assert.Equal(0, challenges[OakLogsId]!["currentCount"]!.GetValue<int>());
        Assert.Equal("Locked", challenges[SpiderSwordId]!["state"]!.GetValue<string>());
    }

    [Fact]
    public void CompletingAnUnlockedChallengeUnlocksTheNextOne()
    {
        long now = At("2026-11-25T12:00:00Z");
        var progress = new ChallengeProgressVersion();
        progress.AddSeasonChallengeProgress(now, PlayAdventuresId, 2);

        JsonObject challenges = List(progress, now);

        Assert.Equal("Completed", challenges[PlayAdventuresId]!["state"]!.GetValue<string>());
        Assert.Equal("Active", challenges[SpiderSwordId]!["state"]!.GetValue<string>());
        Assert.Equal(0, challenges[SpiderSwordId]!["currentCount"]!.GetValue<int>());
    }

    [Fact]
    public void TimedCardsInTheSeasonListStillShowProgress()
    {
        const string PettingZooId = "ccc1836f-ccda-4f33-8a4d-c42d8d366255";
        long now = At("2026-11-25T12:00:00Z");
        var progress = new ChallengeProgressVersion();
        progress.AddObjectiveProgress(now, PettingZooId, 3);

        JsonObject challenges = List(progress, now);

        Assert.Equal(3, challenges[PettingZooId]!["currentCount"]!.GetValue<int>());
    }

    [Fact]
    public void ALockedSeasonChallengeCannotBeClaimedOrSelected()
    {
        long now = At("2026-11-25T12:00:00Z");
        var progress = new ChallengeProgressVersion();
        progress.AddSeasonChallengeProgress(now, OakLogsId, 8);

        var outcome = ChallengeActionsController.ApplyModifyState(progress, OakLogsId, now);

        Assert.False(outcome.ClaimAllowed);
        Assert.False(outcome.FirstClaim);
        Assert.DoesNotContain(OakLogsId, progress.ClaimedChallengeIds);
        Assert.NotEqual(OakLogsId, progress.ActiveSeasonChallengeId);
    }

    [Fact]
    public void AnUnfinishedSeasonChallengeIsOnlySelected()
    {
        long now = At("2026-11-25T12:00:00Z");
        var progress = new ChallengeProgressVersion();

        var outcome = ChallengeActionsController.ApplyModifyState(progress, PlayAdventuresId, now);

        Assert.False(outcome.ClaimAllowed);
        Assert.DoesNotContain(PlayAdventuresId, progress.ClaimedChallengeIds);
        Assert.Equal(PlayAdventuresId, progress.ActiveSeasonChallengeId);
    }

    [Fact]
    public void AFinishedUnlockedSeasonChallengeIsClaimedOnce()
    {
        long now = At("2026-11-25T12:00:00Z");
        var progress = new ChallengeProgressVersion();
        progress.AddSeasonChallengeProgress(now, PlayAdventuresId, 2);

        var first = ChallengeActionsController.ApplyModifyState(progress, PlayAdventuresId, now);
        var second = ChallengeActionsController.ApplyModifyState(progress, PlayAdventuresId, now);

        Assert.True(first.ClaimAllowed);
        Assert.True(first.FirstClaim);
        Assert.True(second.ClaimAllowed);
        Assert.False(second.FirstClaim);
        Assert.Contains(PlayAdventuresId, progress.ClaimedChallengeIds);
    }

    [Fact]
    public void ChallengesOutsideTheSeasonAreClaimedAsBefore()
    {
        const string ContinuousKey = "b8fa3840-43f2-4c87-9d69-f51d77a1a001";
        var progress = new ChallengeProgressVersion();

        var outcome = ChallengeActionsController.ApplyModifyState(progress, ContinuousKey, At("2026-11-25T12:00:00Z"));

        Assert.True(outcome.ClaimAllowed);
        Assert.True(outcome.FirstClaim);
    }

    [Fact]
    public void TheFinalChallengeCompletesOnceItIsUnlocked()
    {
        long now = At("2026-11-25T12:00:00Z");
        var progress = new ChallengeProgressVersion();

        JsonObject before = List(progress, now);
        Assert.Equal("Locked", before[FinalId]!["state"]!.GetValue<string>());

        progress.ClaimedChallengeIds.UnionWith(ChallengesController.SeasonChallengeIds().Where(id => id != FinalId));
        JsonObject after = List(progress, now);

        Assert.Equal("Completed", after[FinalId]!["state"]!.GetValue<string>());
        Assert.True(after[FinalId]!["isComplete"]!.GetValue<bool>());
    }
}
