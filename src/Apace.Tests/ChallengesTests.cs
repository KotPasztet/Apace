using System.Collections;
using System.Reflection;
using System.Text.Json.Nodes;
using Apace.ApiServer.Controllers.EarthApi;
using Apace.ApiServer.Utils;
using Apace.Common;
using Apace.DB;
using Apace.DB.Models.Player;

namespace Apace.Tests;

public sealed class ChallengesTests
{
    private const string PlayerId = "6bd8769afeba7d52";
    private const string DailyReferenceId = "2619913d-6504-4c74-9fc9-e03649a70efc";
    private const string ChickenKey = "e7b9715a-6c27-4708-bab6-ca4c80397625";

    private static long At(string utc)
        => DateTimeOffset.Parse(utc, null, System.Globalization.DateTimeStyles.AssumeUniversal).ToUnixTimeMilliseconds();

    private static JsonObject List(ChallengeProgressVersion progress, long now)
    {
        progress.EnsureDate(now);
        string end = TimeFormatter.FormatTime(now + 3_600_000);
        var listing = ChallengesController.BuildChallengeListing(PlayerId, progress, new TokenClaims(), now, end, end);
        return JsonNode.Parse(Json.Serialize(listing.Challenges))!.AsObject();
    }

    [Fact]
    public void NoMadeUpDailyCardIsListed()
    {
        long now = At("2026-11-22T22:27:36Z");
        var progress = new ChallengeProgressVersion();
        progress.EnsureDate(now);
        progress.ObjectiveCounts[ChickenKey] = 2;

        JsonObject challenges = List(progress, now);

        Assert.DoesNotContain(challenges, entry => entry.Value!["referenceId"]!.GetValue<string>() == DailyReferenceId);
        Assert.False(challenges.ContainsKey("c1a1beef-0d01-4b1a-8d1e-000000000001"));
        JsonNode chicken = challenges[ChickenKey]!;
        Assert.Equal(2, chicken["currentCount"]!.GetValue<int>());
        Assert.Equal(3, chicken["totalThreshold"]!.GetValue<int>());
    }

    [Fact]
    public void ADailyClaimedOnAnEarlierDayIsListedAgainWhenItIsSelectedAgain()
    {
        const string CowOrSheepKey = "252bb18b-5a96-4ac5-bca0-45c1a0d51269";
        var progress = new ChallengeProgressVersion();
        progress.EnsureDate(At("2026-11-21T12:00:00Z"));
        progress.ClaimedChallengeIds.Add(CowOrSheepKey);

        JsonObject challenges = List(progress, At("2026-11-23T08:00:00Z"));

        JsonNode card = challenges[CowOrSheepKey]!;
        Assert.Equal(0, card["currentCount"]!.GetValue<int>());
        Assert.Equal(4, card["totalThreshold"]!.GetValue<int>());
    }

    [Fact]
    public void NewDayClearsDailyAndContinuousClaimsAndKeepsTheRest()
    {
        const string DailyKey = "252bb18b-5a96-4ac5-bca0-45c1a0d51269";
        const string ContinuousKey = "b8fa3840-43f2-4c87-9d69-f51d77a1a001";
        const string SeasonKey = "29ebe650-072f-4f70-996f-4ffdda93ed1f";
        const string SignInKey = "d4111000-0000-4000-8000-000000000001";
        var progress = new ChallengeProgressVersion();
        progress.EnsureDate(At("2026-11-21T12:00:00Z"));
        progress.ClaimedChallengeIds.UnionWith([DailyKey, ContinuousKey, SeasonKey, SignInKey]);

        progress.EnsureDate(At("2026-11-21T23:00:00Z"));
        Assert.Contains(DailyKey, progress.ClaimedChallengeIds);

        progress.EnsureDate(At("2026-11-22T00:00:01Z"));

        Assert.DoesNotContain(DailyKey, progress.ClaimedChallengeIds);
        Assert.DoesNotContain(ContinuousKey, progress.ClaimedChallengeIds);
        Assert.Equal([SeasonKey, SignInKey], progress.ClaimedChallengeIds.Order());
    }

    [Fact]
    public void TodaysChallengesEndAtUtcMidnightWhateverTheServerTimeZone()
    {
        Assert.Equal(At("2026-11-27T00:00:00Z"), ChallengesController.EndOfUtcDay(DateTimeOffset.FromUnixTimeMilliseconds(At("2026-11-26T23:08:28Z"))));
        Assert.Equal(At("2026-11-27T00:00:00Z"), ChallengesController.EndOfUtcDay(DateTimeOffset.FromUnixTimeMilliseconds(At("2026-11-26T00:00:00Z"))));
        Assert.Equal(At("2026-11-27T00:00:00Z"), ChallengesController.EndOfUtcDay(new DateTimeOffset(2026, 11, 27, 0, 30, 0, TimeSpan.FromHours(1))));
    }

    [Fact]
    public void TheTimedCardsInTheSeasonListResetEveryDayAndEndAtUtcMidnight()
    {
        const string FindASheepKey = "91d23ab8-7a63-4d6d-8b4b-6ae462a51067";
        var progress = new ChallengeProgressVersion();
        progress.EnsureDate(At("2026-11-26T23:40:00Z"));
        progress.ClaimedChallengeIds.Add(FindASheepKey);

        long nextDay = At("2026-11-27T00:30:00Z");
        progress.EnsureDate(nextDay);
        string dailyEnd = TimeFormatter.FormatTime(ChallengesController.EndOfUtcDay(DateTimeOffset.FromUnixTimeMilliseconds(nextDay)));
        string seasonEnd = TimeFormatter.FormatTime(progress.GetSeasonEndsAt());
        var listing = ChallengesController.BuildChallengeListing(PlayerId, progress, new TokenClaims(), nextDay, dailyEnd, seasonEnd);
        JsonObject challenges = JsonNode.Parse(Json.Serialize(listing.Challenges))!.AsObject();

        Assert.DoesNotContain(FindASheepKey, progress.ClaimedChallengeIds);
        Assert.Equal("Active", challenges[FindASheepKey]!["state"]!.GetValue<string>());
        Assert.Equal(TimeFormatter.FormatTime(At("2026-11-28T00:00:00Z")), challenges[FindASheepKey]!["endTimeUtc"]!.GetValue<string>());
    }

    [Fact]
    public void AllDailiesBonusDoesNotClaimASeasonChallenge()
    {
        const string SeasonKey = "29ebe650-072f-4f70-996f-4ffdda93ed1f";
        long now = At("2026-11-22T12:00:00Z");
        var progress = new ChallengeProgressVersion();
        progress.EnsureDate(now);
        var pool = (IEnumerable)typeof(TappablesController)
            .GetField("DailyChallengePool", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
        foreach (object definition in pool)
        {
            progress.ObjectiveCounts[(string)definition.GetType().GetProperty("ReferenceId")!.GetValue(definition)!] = 99;
        }

        var tokenClaims = new TokenClaims();
        var rewards = new Apace.ApiServer.Utils.Rewards();
        typeof(TappablesController)
            .GetMethod("AddCompletedDailyChallengeRewards", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [new EarthDB.Query(true), PlayerId, tokenClaims, progress, rewards, now]);

        Assert.DoesNotContain(SeasonKey, progress.ClaimedChallengeIds);
        Assert.Contains(ChallengesController.DailyBonusChallengeId, progress.ClaimedChallengeIds);
        Assert.Contains($"2026-11-22:{ChallengesController.DailyBonusChallengeId}", tokenClaims.RedeemedChallengeRewardKeys);
        Assert.Equal(25 + 3 * 10 + 3 * 10, rewards.ToDBRewardsModel().ExperiencePoints);
    }

    [Fact]
    public void DailyLoginTokenNamesTheBonusChallenge()
    {
        var token = new Tokens.DailyLoginToken("2026-11-22", new Apace.DB.Models.Common.Rewards(0, 25, null, new Dictionary<string, int?>(), [], []));

        Dictionary<string, string> properties = TokensController.TokenToApiResponse(token).ClientProperties;

        Assert.Equal(ChallengesController.DailyBonusChallengeId, properties["challengeid"]);
        Assert.NotEqual("29ebe650-072f-4f70-996f-4ffdda93ed1f", properties["challengeid"]);
    }

    [Fact]
    public void BonusChallengeIdCollidesWithNoClientChallenge()
    {
        var progress = new ChallengeProgressVersion();
        long now = At("2026-11-22T12:00:00Z");

        JsonObject challenges = List(progress, now);

        Assert.False(challenges.ContainsKey(ChallengesController.DailyBonusChallengeId));
        Assert.Null(ChallengesController.GetSeasonChallengeReferenceId(ChallengesController.DailyBonusChallengeId));
    }
}
