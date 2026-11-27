using System.Text.Json.Nodes;
using Apace.ApiServer.Controllers.EarthApi;
using Apace.ApiServer.Utils;
using Apace.Common;
using Apace.DB;
using Apace.DB.Models.Player;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace Apace.Tests;

[Collection("Database")]
public sealed class DailyGoodiesTests(ITestOutputHelper output) : IDisposable
{
    private const string CommonCrystal = "4f16a053-4929-263a-c91a-29663e29df76";

    private readonly TempStaticData _data = new();
    private EarthDB? _earthDB;

    public void Dispose()
    {
        _earthDB?.Dispose();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        SqliteConnection.ClearAllPools();
        _data.Dispose();
    }

    private EarthDB Open()
    {
        _data.Write("catalog/items.json", $$$"""
            [
              {"id": "{{{CommonCrystal}}}", "name": "adventure_crystal_common", "aux": 0, "stackable": true, "type": "ADVENTURE_SCROLL", "category": "ITEMS", "rarity": "COMMON", "useType": "CONSUME", "alternativeUseType": "NONE", "blockInfo": null, "toolInfo": null, "consumeInfo": null, "fuelInfo": null, "projectileInfo": null, "mobInfo": null, "boostInfo": null, "journalEntry": null, "experience": {"tappable": 20, "encounter": 0, "crafting": 0, "journal": 0}}
            ]
            """);
        _data.Write("catalog/itemEfficiencyCategories.json", "[]");
        _data.Write("catalog/itemJournalGroups.json", "[]");
        _data.Write("catalog/recipes.json", """{ "crafting": [], "smelting": [] }""");
        _data.Write("catalog/nfc.json", """{ "miniFigs": [] }""");
        return _earthDB = EarthDB.Open(Path.Combine(_data.Root, "earth.db"));
    }

    private static async Task<Tokens.TokenWithId[]> DailyTokens(EarthDB db)
        => (await new EarthDB.Query(false).Get("tokens", PlayerId, typeof(Tokens)).ExecuteAsync(db))
            .Get<Tokens>("tokens").GetTokens().Where(token => token.Token is Tokens.DailyLoginToken).ToArray();

    [Fact]
    public async Task OneTokenForTodayAndNoneAfterRedeem()
    {
        EarthDB db = Open();
        await TokenUtils.EnsureDailyLoginTokenQuery(PlayerId, Today).ExecuteAsync(db);
        await TokenUtils.EnsureDailyLoginTokenQuery(PlayerId, Today).ExecuteAsync(db);

        Tokens.TokenWithId only = Assert.Single(await DailyTokens(db));
        var token = Assert.IsType<Tokens.DailyLoginToken>(only.Token);
        Assert.Equal(ChallengesController.DailyLoginChallengeIds[0], token.ChallengeId);
        Assert.Equal(ChallengesController.DailyLoginChallengeIds[0], TokensController.TokenToApiResponse(token).ClientProperties["challengeid"]);

        // as TokensController.Redeem does it
        await new EarthDB.Query(true)
            .Get("tokens", PlayerId, typeof(Tokens))
            .Then(results =>
            {
                Tokens tokens = results.Get<Tokens>("tokens");
                Tokens.Token removed = tokens.RemoveToken(only.Id)!;
                return new EarthDB.Query(true)
                    .Update("tokens", PlayerId, tokens)
                    .Then(TokenUtils.DoActionsOnRedeemedToken(removed, PlayerId, Now, _data.Load()), false);
            })
            .ExecuteAsync(db);

        TokenClaims claims = (await new EarthDB.Query(false).Get("tokenClaims", PlayerId, typeof(TokenClaims)).ExecuteAsync(db)).Get<TokenClaims>("tokenClaims");
        Assert.True(TokenUtils.IsDailyLoginClaimedToday(claims, Today));
        Assert.Equal(1, claims.DailyLoginStreak);
        Assert.Equal(1, (await new EarthDB.Query(false).Get("inventory", PlayerId, typeof(Inventory)).ExecuteAsync(db)).Get<Inventory>("inventory").GetItemCount(CommonCrystal));

        await TokenUtils.EnsureDailyLoginTokenQuery(PlayerId, Today).ExecuteAsync(db);
        Assert.Empty(await DailyTokens(db));

        await TokenUtils.EnsureDailyLoginTokenQuery(PlayerId, "2026-11-29").ExecuteAsync(db);
        var next = Assert.IsType<Tokens.DailyLoginToken>(Assert.Single(await DailyTokens(db)).Token);
        Assert.Equal(ChallengesController.DailyLoginChallengeIds[1], next.ChallengeId);
    }

    [Fact]
    public async Task StaleTokenWithoutADayIsReplaced()
    {
        EarthDB db = Open();
        await new EarthDB.Query(true)
            .Get("tokens", PlayerId, typeof(Tokens))
            .Then(results =>
            {
                Tokens tokens = results.Get<Tokens>("tokens");
                tokens.AddToken(Guid.NewGuid().ToString(), new Tokens.DailyLoginToken(Today, ChallengesController.DailyLoginRewards()));
                return new EarthDB.Query(true).Update("tokens", PlayerId, tokens);
            })
            .ExecuteAsync(db);

        await TokenUtils.EnsureDailyLoginTokenQuery(PlayerId, Today).ExecuteAsync(db);

        var token = Assert.IsType<Tokens.DailyLoginToken>(Assert.Single(await DailyTokens(db)).Token);
        Assert.Equal(ChallengesController.DailyLoginChallengeIds[0], token.ChallengeId);
    }

    private const string PlayerId = "d1f1305ac173cf47";
    private const string Today = "2026-11-28";

    private static long Now => DateTimeOffset.Parse($"{Today}T12:00:00Z", null, System.Globalization.DateTimeStyles.AssumeUniversal).ToUnixTimeMilliseconds();

    private static TokenClaims ClaimsOnDay(int day, bool claimedToday)
    {
        var claims = new TokenClaims();
        if (day > 1 || claimedToday)
        {
            claims.LastDailyLoginDate = claimedToday ? Today : "2026-11-27";
            claims.DailyLoginStreak = claimedToday ? day : day - 1;
            if (claimedToday)
            {
                claims.RedeemedDailyLoginDates.Add(Today);
            }
        }

        return claims;
    }

    private static JsonObject List(TokenClaims claims)
    {
        var progress = new ChallengeProgressVersion();
        progress.EnsureDate(Now);
        string end = TimeFormatter.FormatTime(Now + 3_600_000);
        var listing = ChallengesController.BuildChallengeListing(PlayerId, progress, claims, Now, end, end);
        return JsonNode.Parse(Json.Serialize(listing.Challenges))!.AsObject();
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(7, false)]
    [InlineData(1, true)]
    [InlineData(4, true)]
    [InlineData(7, true)]
    public void ListingHasParentAndSevenDays(int currentDay, bool claimedToday)
    {
        JsonObject challenges = List(ClaimsOnDay(currentDay, claimedToday));

        JsonObject parent = challenges[ChallengesController.DailyLoginParentId]!.AsObject();
        Assert.Equal("SignIn", parent["duration"]!.GetValue<string>());
        Assert.Null(parent["parentId"]);
        Assert.Equal("Active", parent["state"]!.GetValue<string>());
        Assert.Equal(0, parent["order"]!.GetValue<int>());

        for (int day = 1; day <= 7; day++)
        {
            JsonObject entry = challenges[ChallengesController.DailyLoginChallengeIds[day - 1]]!.AsObject();
            bool reached = day <= currentDay;
            Assert.Equal("SignIn", entry["duration"]!.GetValue<string>());
            Assert.Equal(ChallengesController.DailyLoginParentId, entry["parentId"]!.GetValue<string>());
            Assert.Equal(day, entry["order"]!.GetValue<int>());
            Assert.Equal(reached ? "Completed" : "Locked", entry["state"]!.GetValue<string>());
            Assert.Equal(reached, entry["isComplete"]!.GetValue<bool>());
            Assert.Equal(reached ? 100 : 0, entry["percentComplete"]!.GetValue<int>());
            Assert.Equal(reached ? entry["totalThreshold"]!.GetValue<int>() : 0, entry["currentCount"]!.GetValue<int>());
        }

        Assert.Equal(8, challenges.Count(entry => entry.Value!["duration"]!.GetValue<string>() == "SignIn"));
    }

    [Fact]
    public void DailyLoginRecordsNeverSayClaimed()
    {
        foreach ((int day, bool claimed) in new[] { (1, false), (3, true), (7, true), (7, false) })
        {
            JsonObject challenges = List(ClaimsOnDay(day, claimed));
            Assert.All(challenges.Where(entry => entry.Value!["duration"]!.GetValue<string>() == "SignIn"),
                entry => Assert.NotEqual("Claimed", entry.Value!["state"]!.GetValue<string>()));
        }
    }

    [Fact]
    public void ClaimAdvancesTheStreakOnce()
    {
        TokenClaims claims = ClaimsOnDay(4, false);
        Assert.Equal(4, TokenUtils.GetDailyLoginCycleDay(claims, Today));
        Assert.False(TokenUtils.IsDailyLoginClaimedToday(claims, Today));

        Assert.True(TokenUtils.RecordDailyLoginClaim(claims, Today));
        Assert.False(TokenUtils.RecordDailyLoginClaim(claims, Today));

        Assert.True(TokenUtils.IsDailyLoginClaimedToday(claims, Today));
        Assert.Equal(4, claims.DailyLoginStreak);
        Assert.Equal(4, TokenUtils.GetDailyLoginCycleDay(claims, Today));
        Assert.Equal(5, TokenUtils.GetDailyLoginCycleDay(claims, "2026-11-29"));
    }

    [Fact]
    public void TokenIsAChallengeCompletedForTheDay()
    {
        string dayId = ChallengesController.DailyLoginChallengeIds[3];
        var token = new Tokens.DailyLoginToken(Today, ChallengesController.DailyLoginRewards(), dayId);

        var response = TokensController.TokenToApiResponse(token);

        Assert.Equal(dayId, response.ClientProperties["challengeid"]);
        Assert.Equal(dayId, response.ClientProperties["challengereferenceid"]);
        Assert.Equal(0x132ecad9u, ClientTokenTypes.Fnv1a32("challenge.completed"));
        output.WriteLine(Json.Serialize(response));
        output.WriteLine(Json.Serialize(List(ClaimsOnDay(4, false))[ChallengesController.DailyLoginParentId]));
        output.WriteLine(Json.Serialize(List(ClaimsOnDay(4, false))[dayId]));
    }
}
