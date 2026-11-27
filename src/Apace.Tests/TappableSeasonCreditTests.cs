using System.Reflection;
using System.Text.Json.Nodes;
using Apace.ApiServer.Controllers.EarthApi;
using Apace.ApiServer.Utils;
using Apace.Common;
using Apace.DB;
using Apace.DB.Models.Player;

namespace Apace.Tests;

public sealed class TappableSeasonCreditTests
{
    private const string PlayerId = "6bd8769afeba7d52";
    private const string CollectMobsId = "edf9f1dc-f73f-4275-a3e5-e1d9d861d158";
    private const string UncommonMobId = "0d4c99ce-b485-4890-be69-caab88400df3";
    private const string TerracottaId = "63e5d65e-1152-4adc-97e7-2818854a867a";
    private const string RabbitsId = "703c24f0-aedb-4fc1-bce8-6b06229b2006";
    private const string UncommonCowsId = "26f62499-5996-477a-8530-22f852b1ccad";
    private const string DarkOakSlabsId = "3183cf3d-19e5-4bf5-b8a8-3085f4e650d3";
    private const string RareMobId = "0b346237-79a4-4f24-a45a-e2e6284e3e56";
    private const string WhiteTerracottaItem = "a06bf8c7-f350-1a55-ee58-f50dac614b34";
    private const string RedGlazedTerracottaItem = "e6e669cd-4fb6-dd0f-8eee-1644b53c3eaf";
    private const string RabbitItem = "bae634b3-6b3d-4ca0-8e1e-f223b6760727";
    private const string DarkOakSlabItem = "393d90c3-4968-72ab-22c9-dff5d0ec3861";
    private const string UncommonCowItem = "214103ec-f98b-4d7a-3dea-a69b696cefc2";
    private const string RareMobItem = "038c8034-1d98-96ca-8278-bc69f592549f";
    private const string ContinuousTappablesKey = "b8fa3840-43f2-4c87-9d69-f51d77a1a001";
    private const string TappablesReferenceId = "6b0655aa-cc63-4876-a1e1-afb319403c1c";

    private static long At(string utc)
        => DateTimeOffset.Parse(utc, null, System.Globalization.DateTimeStyles.AssumeUniversal).ToUnixTimeMilliseconds();

    private static int Season(ChallengeProgressVersion progress, string challengeId)
        => progress.GetSeasonChallengeProgress(challengeId);

    private static ChallengeProgressVersion ActiveOn(string challengeId)
    {
        var progress = new ChallengeProgressVersion();
        progress.ClaimedChallengeIds.UnionWith(ChallengesController.SeasonChallengeIds().Where(id => id != challengeId));
        progress.ActiveSeasonChallengeId = challengeId;
        return progress;
    }

    private static void Collect(ChallengeProgressVersion progress, long now, string icon, params string[] itemIds)
    {
        var tappable = new TappablesManager.Tappable("t", 0, 0, now, 60_000, icon, TappablesManager.Tappable.RarityE.COMMON, []);
        typeof(TappablesController)
            .GetMethod("AddDailyObjectiveProgress", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [progress, new Tokens(), tappable, itemIds.ToHashSet(), now]);
    }

    private static Apace.ApiServer.Utils.Rewards Redeem(ChallengeProgressVersion progress, TokenClaims tokenClaims, long now)
    {
        var rewards = new Apace.ApiServer.Utils.Rewards();
        typeof(TappablesController)
            .GetMethod("AddCompletedDailyChallengeRewards", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [new EarthDB.Query(true), PlayerId, tokenClaims, progress, rewards, now]);
        return rewards;
    }

    [Fact]
    public void MobTappablesCountForTheSeasonAndSurviveMidnight()
    {
        var progress = new ChallengeProgressVersion { ActiveSeasonChallengeId = CollectMobsId };
        Collect(progress, At("2026-11-25T20:00:00Z"), "genoa:cow_tappable_map");
        Collect(progress, At("2026-11-25T21:00:00Z"), "genoa:chicken_tappable_map");

        progress.EnsureDate(At("2026-11-26T08:00:00Z"));

        Assert.Equal(2, Season(progress, CollectMobsId));
    }

    [Fact]
    public void EachTerracottaColourCountsOnce()
    {
        long now = At("2026-11-25T12:00:00Z");
        var progress = ActiveOn(TerracottaId);

        Collect(progress, now, "genoa:stone_mound_a_tappable_map", WhiteTerracottaItem);
        Collect(progress, now, "genoa:stone_mound_a_tappable_map", WhiteTerracottaItem);
        Collect(progress, now, "genoa:stone_mound_a_tappable_map", RedGlazedTerracottaItem);

        Assert.Equal(2, Season(progress, TerracottaId));
    }

    [Theory]
    [InlineData(DarkOakSlabsId, "genoa:tree_tappable_map", DarkOakSlabItem)]
    [InlineData(RabbitsId, "genoa:rabbit_tappable_map", RabbitItem)]
    [InlineData(UncommonCowsId, "genoa:cow_tappable_map", UncommonCowItem)]
    [InlineData(UncommonMobId, "genoa:cow_tappable_map", UncommonCowItem)]
    [InlineData(RareMobId, "genoa:pig_tappable_map", RareMobItem)]
    public void LootItemsCountForTheirActiveSeasonChallenge(string challengeId, string icon, string itemId)
    {
        long now = At("2026-11-25T12:00:00Z");
        var progress = ActiveOn(challengeId);

        Collect(progress, now, icon, itemId);

        Assert.Equal(1, Season(progress, challengeId));
        Assert.Equal(challengeId, Assert.Single(progress.SeasonChallengeCounts).Key);
    }

    [Fact]
    public void ACompletedContinuousChallengeIsRedeemedOnceADay()
    {
        long now = At("2026-11-25T12:00:00Z");
        var progress = new ChallengeProgressVersion();
        progress.AddObjectiveProgress(now, TappablesReferenceId, 10);
        var tokenClaims = new TokenClaims();

        var first = Redeem(progress, tokenClaims, now);
        var second = Redeem(progress, tokenClaims, now);

        Assert.Equal(10, first.ToDBRewardsModel().ExperiencePoints);
        Assert.Equal(0, second.ToDBRewardsModel().ExperiencePoints);
        Assert.Contains(ContinuousTappablesKey, progress.ClaimedChallengeIds);
        Assert.Contains($"2026-11-25:{ContinuousTappablesKey}", tokenClaims.RedeemedChallengeRewardKeys);
    }

    [Fact]
    public void ARedeemedContinuousChallengeIsListedAgainTheNextDay()
    {
        long now = At("2026-11-25T12:00:00Z");
        var progress = new ChallengeProgressVersion();
        progress.AddObjectiveProgress(now, TappablesReferenceId, 10);
        Redeem(progress, new TokenClaims(), now);

        long tomorrow = At("2026-11-26T09:00:00Z");
        progress.EnsureDate(tomorrow);
        string end = TimeFormatter.FormatTime(tomorrow + 3_600_000);
        var listing = ChallengesController.BuildChallengeListing(PlayerId, progress, new TokenClaims(), tomorrow, end, end);
        JsonObject challenges = JsonNode.Parse(Json.Serialize(listing.Challenges))!.AsObject();

        Assert.True(challenges.ContainsKey(ContinuousTappablesKey));
        Assert.Equal(0, challenges[ContinuousTappablesKey]!["currentCount"]!.GetValue<int>());
    }
}
