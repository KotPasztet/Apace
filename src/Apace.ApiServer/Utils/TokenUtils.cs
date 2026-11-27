using System.Globalization;
using Serilog;
using Apace.Common.Utils;
using Apace.ApiServer.Controllers.EarthApi;
using Apace.DB;
using Apace.DB.Models.Player;

namespace Apace.ApiServer.Utils;

public static class TokenUtils
{

    public static EarthDB.Query AddToken(string playerId, Tokens.Token token)
    {
        var getQuery = new EarthDB.Query(true);
        getQuery.Get("tokens", playerId, typeof(Tokens));
        getQuery.Then(results =>
        {
            Tokens tokens = results.Get<Tokens>("tokens");
            string id = U.RandomUuid().ToString();
            tokens.AddToken(id, token);
            var updateQuery = new EarthDB.Query(true);
            updateQuery.Update("tokens", playerId, tokens);
            updateQuery.Extra("tokenId", id);
            return updateQuery;
        });
        return getQuery;
    }

    // one token per adventure instance, more loot is added to it
    public static EarthDB.Query AddOrUpdateAdventureRewardToken(string playerId, string instanceId, IReadOnlyDictionary<string, int> items)
    {
        var getQuery = new EarthDB.Query(true);
        getQuery.Get("tokens", playerId, typeof(Tokens));
        getQuery.Then(results =>
        {
            Tokens tokens = results.Get<Tokens>("tokens");

            Tokens.TokenWithId? existing = tokens.GetTokens()
                .FirstOrDefault(token => token.Token is Tokens.AdventureRewardToken adventureReward && adventureReward.InstanceId == instanceId);

            Rewards rewards = existing is not null
                ? Rewards.FromDBRewardsModel(((Tokens.AdventureRewardToken)existing.Token).Rewards)
                : new Rewards();
            foreach ((string itemId, int count) in items)
            {
                rewards.AddItem(itemId, count);
            }

            string id = existing?.Id ?? U.RandomUuid().ToString();
            tokens.AddToken(id, new Tokens.AdventureRewardToken(instanceId, rewards.ToDBRewardsModel()));

            return new EarthDB.Query(true)
                .Update("tokens", playerId, tokens)
                .Extra("tokenId", id);
        });
        return getQuery;
    }

    public static async Task EnsureDailyLoginToken(string playerId, CancellationToken cancellationToken)
    {
        string today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        try
        {
            await EnsureDailyLoginTokenQuery(playerId, today).ExecuteAsync(Program.DB, cancellationToken);
        }
        catch (EarthDB.DatabaseException exception)
        {
            Log.Warning(exception, "Could not create daily login token for {PlayerId}", playerId);
        }
    }

    internal static EarthDB.Query EnsureDailyLoginTokenQuery(string playerId, string today)
        => new EarthDB.Query(true)
                .Get("tokenClaims", playerId, typeof(TokenClaims))
                .Get("tokens", playerId, typeof(Tokens))
                .Then(results =>
                {
                    TokenClaims tokenClaims = results.Get<TokenClaims>("tokenClaims");
                    Tokens tokens = results.Get<Tokens>("tokens");

                    string challengeId = ChallengesController.DailyLoginChallengeIds[GetDailyLoginCycleDay(tokenClaims, today) - 1];
                    bool changed = false;
                    bool hasCurrent = false;
                    foreach (Tokens.TokenWithId existingToken in tokens.GetTokens())
                    {
                        if (existingToken.Token is not Tokens.DailyLoginToken dailyLoginToken)
                        {
                            continue;
                        }

                        bool current = dailyLoginToken.Date == today && dailyLoginToken.ChallengeId == challengeId && Guid.TryParse(existingToken.Id, out _);
                        if (current && !hasCurrent && !tokenClaims.RedeemedDailyLoginDates.Contains(today))
                        {
                            hasCurrent = true;
                            continue;
                        }

                        tokens.RemoveToken(existingToken.Id);
                        changed = true;
                    }

                    if (tokenClaims.RedeemedDailyLoginDates.Contains(today) || hasCurrent)
                    {
                        return changed
                            ? new EarthDB.Query(true).Update("tokens", playerId, tokens)
                            : EarthDB.Query.Empty;
                    }

                    // the streak advances when the token is claimed (DoActionsOnRedeemedToken), not when it is created
                    tokens.AddToken(U.RandomUuid().ToString(), new Tokens.DailyLoginToken(today, ChallengesController.DailyLoginRewards(), challengeId));

                    return new EarthDB.Query(true).Update("tokens", playerId, tokens);
                });

    // port of Solace v0.0.7 ChallengeProgressVersion.GetDailyLoginDay: the cycle day is derived from the
    // last CLAIM date, so a streak whose last claim was neither today nor yesterday starts over at day 1
    public static int GetDailyLoginCycleDay(TokenClaims tokenClaims, string today)
    {
        int streak = tokenClaims.LastDailyLoginDate switch
        {
            null => 1,
            var date when date == today => Math.Max(1, tokenClaims.DailyLoginStreak),
            var date when date == GetPreviousDate(today) => Math.Max(1, tokenClaims.DailyLoginStreak) + 1,
            _ => 1
        };

        return (streak - 1) % 7 + 1;
    }

    public static int GetDailyLoginCycleDay(TokenClaims tokenClaims, long timestamp)
        => GetDailyLoginCycleDay(tokenClaims, GetDate(timestamp));

    public static bool IsDailyLoginClaimedToday(TokenClaims tokenClaims, string today)
        => tokenClaims.RedeemedDailyLoginDates.Contains(today);

    public static bool IsDailyLoginClaimedToday(TokenClaims tokenClaims, long timestamp)
        => IsDailyLoginClaimedToday(tokenClaims, GetDate(timestamp));

    // advances the streak for a claim on the given day; a last claim that is neither the same day nor the
    // previous one resets the streak to 1, and re-claiming the same day is a no-op
    private static void ClaimDailyLogin(TokenClaims tokenClaims, string date)
    {
        if (tokenClaims.LastDailyLoginDate == date)
        {
            return;
        }

        tokenClaims.DailyLoginStreak = tokenClaims.LastDailyLoginDate == GetPreviousDate(date)
            ? Math.Max(1, tokenClaims.DailyLoginStreak) + 1
            : 1;
        tokenClaims.LastDailyLoginDate = date;
    }

    internal static bool RecordDailyLoginClaim(TokenClaims tokenClaims, string date)
    {
        bool firstClaim = tokenClaims.RedeemedDailyLoginDates.Add(date);
        if (firstClaim)
        {
            ClaimDailyLogin(tokenClaims, date);
        }

        return firstClaim;
    }

    private static string GetDate(long timestamp)
        => DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string GetPreviousDate(string date)
        => DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // does not handle redeeming the token itself (removing it from the list of tokens belonging to the player)
    public static EarthDB.Query DoActionsOnRedeemedToken(Tokens.Token token, string playerId, long currentTime, StaticData.StaticData staticData)
    {
        var getQuery = new EarthDB.Query(true);

        switch (token.Type)
        {
            case Tokens.Token.TypeE.LEVEL_UP:
                {
                    var levelUpToken = (Tokens.LevelUpToken)token;

                    getQuery.Then(results =>
                    {
                        var updateQuery = new EarthDB.Query(true);

                        updateQuery.Then(ActivityLogUtils.AddEntry(playerId, new ActivityLog.LevelUpEntry(currentTime, levelUpToken.Level)));

                        updateQuery.Then(Rewards.FromDBRewardsModel(levelUpToken.Rewards).ToRedeemQuery(playerId, currentTime, staticData));

                        return updateQuery;
                    }, false);
                }

                break;
            case Tokens.Token.TypeE.JOURNAL_ITEM_UNLOCKED:
                {
                    var journalItemUnlockedToken = (Tokens.JournalItemUnlockedToken)token;
                    getQuery.Then(results =>
                    {
                        var updateQuery = new EarthDB.Query(true);

                        updateQuery.Then(ActivityLogUtils.AddEntry(playerId, new ActivityLog.JournalItemUnlockedEntry(currentTime, journalItemUnlockedToken.ItemId)));

                        /*int experiencePoints = staticData.catalog.itemsCatalog.getItem(journalItemUnlockedToken.itemId).experience().journal();
                        if (experiencePoints > 0)
                        {
                            updateQuery.then(new Rewards().addExperiencePoints(experiencePoints).toRedeemQuery(playerId, currentTime, staticData));
                        }*/

                        return updateQuery;
                    }, false);
                }

                break;
            case Tokens.Token.TypeE.DAILY_LOGIN:
                {
                    var dailyLoginToken = (Tokens.DailyLoginToken)token;
                    getQuery.Get("tokenClaims", playerId, typeof(TokenClaims));
                    getQuery.Then(results =>
                    {
                        TokenClaims tokenClaims = results.Get<TokenClaims>("tokenClaims");

                        // the streak advances on claim (not on token creation); RedeemedDailyLoginDates keeps
                        // double claims of the same day from advancing the streak or granting rewards twice
                        bool firstClaim = RecordDailyLoginClaim(tokenClaims, dailyLoginToken.Date);

                        var updateQuery = new EarthDB.Query(true)
                            .Update("tokenClaims", playerId, tokenClaims);

                        return firstClaim
                            ? updateQuery.Then(Rewards.FromDBRewardsModel(dailyLoginToken.Rewards).ToRedeemQuery(playerId, currentTime, staticData), false)
                            : updateQuery;
                    }, false);
                }

                break;
            case Tokens.Token.TypeE.OOBE_ADVENTURE_CRYSTAL:
                {
                    var oobeAdventureCrystalToken = (Tokens.OobeAdventureCrystalToken)token;
                    getQuery.Then(Rewards.FromDBRewardsModel(oobeAdventureCrystalToken.Rewards).ToRedeemQuery(playerId, currentTime, staticData), false);
                }

                break;
            case Tokens.Token.TypeE.ADVENTURE_REWARD:
                {
                    var adventureRewardToken = (Tokens.AdventureRewardToken)token;
                    getQuery.Then(Rewards.FromDBRewardsModel(adventureRewardToken.Rewards).ToRedeemQuery(playerId, currentTime, staticData), false);
                }

                break;
            case Tokens.Token.TypeE.CHALLENGE_PROGRESS:
                break;
            case Tokens.Token.TypeE.CHALLENGE_COMPLETED:
                {
                    var completedToken = (Tokens.ChallengeCompletedToken)token;
                    getQuery.Get("challenges", playerId, typeof(ChallengeProgressVersion));
                    getQuery.Then(results =>
                    {
                        ChallengeProgressVersion progress = results.Get<ChallengeProgressVersion>("challenges");
                        bool firstClaim = ClaimCompletedChallenge(progress, completedToken.ChallengeId, currentTime);

                        var updateQuery = new EarthDB.Query(true)
                            .Update("challenges", playerId, progress);

                        if (firstClaim)
                        {
                            Types.Common.Rewards? apiRewards = ChallengesController.GetSeasonChallengeRewards(completedToken.ChallengeId);
                            if (apiRewards is not null)
                            {
                                updateQuery.Then(ToRedeemRewards(apiRewards).ToRedeemQuery(playerId, currentTime, staticData), false);
                            }
                        }

                        return updateQuery;
                    }, false);
                }

                break;
        }

        getQuery.Extra("token", token);

        return getQuery;
    }

    internal static bool ClaimCompletedChallenge(ChallengeProgressVersion progress, string challengeId, long currentTime)
    {
        progress.EnsureDate(currentTime);
        progress.ClaimedChallengeIds ??= [];
        var (isSeason, unlocked, complete) = ChallengesController.GetSeasonChallengeStatus(progress, challengeId);
        bool firstClaim = (!isSeason || (unlocked && complete)) && progress.ClaimedChallengeIds.Add(challengeId);
        progress.ActiveSeasonId = ChallengesController.ActiveSeasonId;
        progress.ActiveSeasonChallengeId = ChallengesController.SelectActiveSeasonChallengeId(progress, progress.ActiveSeasonChallengeId);
        progress.UpdatedAt = currentTime;
        return firstClaim;
    }

    private static Rewards ToRedeemRewards(Types.Common.Rewards rewards)
    {
        var result = new Rewards();
        if (rewards.Rubies is > 0)
        {
            result.AddRubies(rewards.Rubies.Value);
        }

        if (rewards.ExperiencePoints is > 0)
        {
            result.AddExperiencePoints(rewards.ExperiencePoints.Value);
        }

        foreach (var item in rewards.Inventory)
        {
            result.AddItem(item.Id, item.Amount);
        }

        foreach (string buildplateId in rewards.Buildplates)
        {
            result.AddBuildplate(buildplateId);
        }

        foreach (var challenge in rewards.Challenges)
        {
            result.AddChallenge(challenge.Id);
        }

        return result;
    }
}
