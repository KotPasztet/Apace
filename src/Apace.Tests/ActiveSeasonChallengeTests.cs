using System.Reflection;
using System.Text.Json.Nodes;
using Apace.ApiServer.Controllers.EarthApi;
using Apace.ApiServer.Utils;
using Apace.Common;
using Apace.DB;
using Apace.DB.Models.Player;

namespace Apace.Tests;

public sealed class ActiveSeasonChallengeTests
{
    private const string PlayerId = "6bd8769afeba7d52";
    private const string PlayAdventuresId = "e81d6ce8-622c-461a-a2d5-bb1f8ac36c62";
    private const string PlaceAnimalsId = "1ccbe15e-3a72-4d8b-ad0b-00a23ac72eb1";
    private const string CollectMobsId = "edf9f1dc-f73f-4275-a3e5-e1d9d861d158";
    private const string TerracottaId = "63e5d65e-1152-4adc-97e7-2818854a867a";
    private const string WhiteTerracottaItem = "a06bf8c7-f350-1a55-ee58-f50dac614b34";
    private const string CowIcon = "genoa:cow_tappable_map";

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

    private static int Count(ChallengeProgressVersion progress, long now, string challengeId)
        => List(progress, now)[challengeId]!["currentCount"]!.GetValue<int>();

    private static void Collect(ChallengeProgressVersion progress, Tokens tokens, long now, string icon, params string[] itemIds)
    {
        var tappable = new TappablesManager.Tappable("t", 0, 0, now, 60_000, icon, TappablesManager.Tappable.RarityE.COMMON, []);
        typeof(TappablesController)
            .GetMethod("AddDailyObjectiveProgress", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [progress, tokens, tappable, itemIds.ToHashSet(), now]);
    }

    private static void Select(ChallengeProgressVersion progress, string challengeId, long now)
    {
        ChallengeActionsController.ApplyModifyState(progress, challengeId, now);
        Assert.Equal(challengeId, progress.ActiveSeasonChallengeId);
    }

    private static IEnumerable<string> ChallengeTokens(Tokens tokens)
        => tokens.GetTokens().Select(token => token.Token switch
        {
            Tokens.ChallengeProgressToken progress => progress.ChallengeId,
            Tokens.ChallengeCompletedToken completed => completed.ChallengeId,
            _ => null,
        }).OfType<string>();

    [Fact]
    public void AnInactiveUnlockedChallengeDoesNotProgress()
    {
        long now = At("2026-11-25T12:00:00Z");
        var progress = new ChallengeProgressVersion();
        var tokens = new Tokens();
        Select(progress, PlayAdventuresId, now);
        Assert.True(ChallengesController.GetSeasonChallengeStatus(progress, CollectMobsId).Unlocked);

        for (int i = 0; i < 3; i++)
        {
            Collect(progress, tokens, now, CowIcon);
        }

        Assert.False(ChallengeEvents.ApplyMobPlaced(progress, tokens, now, "chicken"));

        JsonObject challenges = List(progress, now);
        Assert.Equal(0, challenges[CollectMobsId]!["currentCount"]!.GetValue<int>());
        Assert.False(challenges[CollectMobsId]!["isComplete"]!.GetValue<bool>());
        Assert.Equal(0, challenges[PlaceAnimalsId]!["currentCount"]!.GetValue<int>());
        Assert.False(ChallengesController.GetSeasonChallengeStatus(progress, CollectMobsId).Complete);
    }

    [Fact]
    public void TheActiveChallengeProgresses()
    {
        long now = At("2026-11-25T12:00:00Z");
        var progress = new ChallengeProgressVersion();
        var tokens = new Tokens();
        Select(progress, CollectMobsId, now);

        for (int i = 0; i < 3; i++)
        {
            Collect(progress, tokens, now, CowIcon);
        }

        JsonObject challenges = List(progress, now);
        Assert.Equal(3, challenges[CollectMobsId]!["currentCount"]!.GetValue<int>());
        Assert.Equal("Completed", challenges[CollectMobsId]!["state"]!.GetValue<string>());
        Assert.True(ChallengesController.GetSeasonChallengeStatus(progress, CollectMobsId).Complete);
    }

    [Fact]
    public void SwitchingFreezesTheOldChallengeAndResumesItLater()
    {
        long now = At("2026-11-25T12:00:00Z");
        var progress = new ChallengeProgressVersion();
        var tokens = new Tokens();

        Select(progress, CollectMobsId, now);
        Collect(progress, tokens, now, CowIcon);
        Assert.Equal(1, Count(progress, now, CollectMobsId));

        Select(progress, PlayAdventuresId, now);
        Collect(progress, tokens, now, CowIcon);
        Collect(progress, tokens, now, CowIcon);
        Assert.True(ChallengeEvents.ApplyAdventureCompleted(progress, tokens, now));
        Assert.Equal(1, Count(progress, now, CollectMobsId));
        Assert.Equal(1, Count(progress, now, PlayAdventuresId));

        Select(progress, CollectMobsId, now);
        Assert.False(ChallengeEvents.ApplyAdventureCompleted(progress, tokens, now));
        Collect(progress, tokens, now, CowIcon);
        Assert.Equal(2, Count(progress, now, CollectMobsId));
        Assert.Equal(1, Count(progress, now, PlayAdventuresId));
    }

    [Fact]
    public void DailyTappableCountsDoNotFillASeasonCard()
    {
        long now = At("2026-11-25T12:00:00Z");
        var progress = new ChallengeProgressVersion();
        Select(progress, CollectMobsId, now);
        progress.AddObjectiveProgress(now, Reference(CollectMobsId), 5);
        progress.AddObjectiveProgress(now, Reference(PlayAdventuresId), 5);

        JsonObject challenges = List(progress, now);
        Assert.Equal(0, challenges[CollectMobsId]!["currentCount"]!.GetValue<int>());
        Assert.False(challenges[CollectMobsId]!["isComplete"]!.GetValue<bool>());
        Assert.Equal(0, challenges[PlayAdventuresId]!["currentCount"]!.GetValue<int>());
        Assert.False(ChallengesController.GetSeasonChallengeStatus(progress, CollectMobsId).Complete);
        Assert.False(ChallengeActionsController.ApplyModifyState(progress, CollectMobsId, now).ClaimAllowed);
    }

    [Fact]
    public void ANotificationTokenOnlyComesForTheActiveChallenge()
    {
        long now = At("2026-11-25T12:00:00Z");
        var progress = new ChallengeProgressVersion();
        var inactiveTokens = new Tokens();
        Select(progress, PlayAdventuresId, now);

        for (int i = 0; i < 3; i++)
        {
            Collect(progress, inactiveTokens, now, CowIcon);
        }

        ChallengeEvents.ApplyMobPlaced(progress, inactiveTokens, now, "cow");
        Assert.DoesNotContain(CollectMobsId, ChallengeTokens(inactiveTokens));
        Assert.DoesNotContain(PlaceAnimalsId, ChallengeTokens(inactiveTokens));

        var activeTokens = new Tokens();
        Select(progress, CollectMobsId, now);
        for (int i = 0; i < 3; i++)
        {
            Collect(progress, activeTokens, now, CowIcon);
        }

        Assert.Equal([CollectMobsId, CollectMobsId, CollectMobsId], ChallengeTokens(activeTokens));
        Assert.IsType<Tokens.ChallengeCompletedToken>(activeTokens.GetTokens().Last().Token);
    }

    [Fact]
    public void AnUniqueEventIgnoredWhileInactiveStillCountsOnceActive()
    {
        long now = At("2026-11-25T12:00:00Z");
        var progress = new ChallengeProgressVersion();
        var tokens = new Tokens();
        progress.ClaimedChallengeIds.UnionWith(ChallengesController.SeasonChallengeIds().Where(id => id != TerracottaId));
        Assert.True(ChallengesController.GetSeasonChallengeStatus(progress, TerracottaId).Unlocked);
        progress.ActiveSeasonChallengeId = PlayAdventuresId;

        Collect(progress, tokens, now, "genoa:stone_mound_a_tappable_map", WhiteTerracottaItem);
        Assert.False(ChallengeEvents.ApplyMobPlaced(progress, tokens, now, "chicken"));

        Select(progress, TerracottaId, now);
        Collect(progress, tokens, now, "genoa:stone_mound_a_tappable_map", WhiteTerracottaItem);
        Collect(progress, tokens, now, "genoa:stone_mound_a_tappable_map", WhiteTerracottaItem);

        Assert.Equal(1, Count(progress, now, TerracottaId));
    }

    [Fact]
    public void SeasonProgressSavedByReferenceIsNotCarriedOver()
    {
        string saved = $$"""
            {"updatedAt":{{At("2026-11-25T12:00:00Z")}},"dailyDateUtc":"2026-11-25","activeSeasonId":"season_17","activeSeasonChallengeId":"{{PlaceAnimalsId}}","tappablesRedeemed":3,"objectiveCounts":{},"claimedChallengeIds":["f0532069-a70a-4a01-8611-f770bb46d9cd","{{CollectMobsId}}"],"removedContinuousChallengeIds":[],"seasonObjectiveCounts":{"{{Reference(CollectMobsId)}}":3,"{{Reference(PlayAdventuresId)}}":2,"{{Reference(PlaceAnimalsId)}}":1,"placed:chicken":1},"seasonStartedAt":{{At("2026-11-25T00:00:00Z")}}}
            """;
        MethodInfo fromJson = typeof(EarthDB).GetMethod("FromJson", BindingFlags.NonPublic | BindingFlags.Static, [typeof(string), typeof(Type)])!;
        var progress = (ChallengeProgressVersion)fromJson.Invoke(null, [saved, typeof(ChallengeProgressVersion)])!;
        long now = At("2026-11-25T18:00:00Z");

        JsonObject challenges = List(progress, now);

        Assert.Equal("Claimed", challenges[CollectMobsId]!["state"]!.GetValue<string>());
        Assert.Contains(CollectMobsId, progress.ClaimedChallengeIds);
        Assert.Equal(0, challenges[PlayAdventuresId]!["currentCount"]!.GetValue<int>());
        Assert.Equal(0, challenges[PlaceAnimalsId]!["currentCount"]!.GetValue<int>());
        Assert.Equal(PlaceAnimalsId, ChallengesController.SelectActiveSeasonChallengeId(progress, progress.ActiveSeasonChallengeId));

        Assert.True(ChallengeEvents.ApplyMobPlaced(progress, new Tokens(), now, "chicken"));
        Assert.Equal(1, Count(progress, now, PlaceAnimalsId));
    }

    [Fact]
    public void ACompletedTokenForAnUnfinishedSeasonChallengeDoesNotClaimIt()
    {
        long now = At("2026-11-25T12:00:00Z");
        var progress = new ChallengeProgressVersion();
        progress.EnsureDate(now);
        progress.ActiveSeasonChallengeId = PlayAdventuresId;

        Assert.False(TokenUtils.ClaimCompletedChallenge(progress, CollectMobsId, now));
        Assert.DoesNotContain(CollectMobsId, progress.ClaimedChallengeIds);

        ChallengeEvents.ApplyAdventureCompleted(progress, new Tokens(), now);
        ChallengeEvents.ApplyAdventureCompleted(progress, new Tokens(), now);

        Assert.True(TokenUtils.ClaimCompletedChallenge(progress, PlayAdventuresId, now));
        Assert.False(TokenUtils.ClaimCompletedChallenge(progress, PlayAdventuresId, now));
        Assert.Contains(PlayAdventuresId, progress.ClaimedChallengeIds);
        Assert.NotEqual(PlayAdventuresId, progress.ActiveSeasonChallengeId);
    }

    [Fact]
    public void AClaimedSeasonCardShowsItsFullCount()
    {
        long now = At("2026-11-26T12:00:00Z");
        var progress = new ChallengeProgressVersion();
        progress.EnsureDate(now);
        progress.ClaimedChallengeIds.Add(PlayAdventuresId);

        JsonNode card = List(progress, now)[PlayAdventuresId]!;

        Assert.Equal(card["totalThreshold"]!.GetValue<int>(), card["currentCount"]!.GetValue<int>());
        Assert.True(card["isComplete"]!.GetValue<bool>());
    }

    [Fact]
    public void SuperPrizesIsDoneOnceTheGoldPickaxeCardWasClaimed()
    {
        long now = At("2026-11-26T12:00:00Z");
        var progress = new ChallengeProgressVersion();
        progress.EnsureDate(now);
        progress.ClaimedChallengeIds.Add("fb13127e-d19d-48bd-a977-f830eecff180");

        Assert.True(ChallengesController.GetSeasonChallengeStatus(progress, "96593763-a8a6-41f8-986a-8d6df0470c57").Complete);
    }

    [Fact]
    public void ALockedSeasonCardCannotBecomeTheActiveOne()
    {
        var progress = new ChallengeProgressVersion();
        progress.EnsureDate(At("2026-11-26T12:00:00Z"));

        Assert.NotEqual("96593763-a8a6-41f8-986a-8d6df0470c57", ChallengesController.SelectActiveSeasonChallengeId(progress, "96593763-a8a6-41f8-986a-8d6df0470c57"));
    }
}
