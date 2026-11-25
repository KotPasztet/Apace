using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Apace.DB;
using Apace.ApiServer.Utils;
using Apace.Common.Utils;
using System.Globalization;
using System.Security.Claims;
using Apace.DB.Models.Player;
using ApiRewards = Apace.ApiServer.Types.Common.Rewards;
using RedeemRewards = Apace.ApiServer.Utils.Rewards;

namespace Apace.ApiServer.Controllers.EarthApi;

[Authorize]
[ApiVersion("1.1")]
[Route("1/api/v{version:apiVersion}/challenges")]
internal sealed class ChallengeActionsController : ApaceControllerBase
{
    [HttpPost("{challengeId}/modifyState")]
    [HttpPut("{challengeId}/modifyState")]
    public async Task<Results<ContentHttpResult, BadRequest>> ModifyState(string challengeId, CancellationToken cancellationToken)
    {
        string? playerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(playerId))
        {
            return TypedResults.BadRequest();
        }

        long now = HttpContext.GetTimestamp();

        // sign-in challenges are claimed through the daily login token, their state lives in TokenClaims
        if (ChallengesController.TryGetDailyLoginDay(challengeId, out int requestedDay))
        {
            return await ClaimDailyLoginChallenge(playerId, challengeId, requestedDay, now, cancellationToken);
        }

        ApiRewards? apiRewards = ChallengesController.GetSeasonChallengeRewards(challengeId);
        RedeemRewards rewards = ToRedeemRewards(apiRewards);

        // log what the client sent (the same request may be used for more than selecting and claiming)
        string requestBody = "";
        try
        {
            using var bodyReader = new StreamReader(Request.Body);
            requestBody = await bodyReader.ReadToEndAsync(cancellationToken);
        }
        catch (Exception)
        {
            // only used for the log line
        }

        bool claimedNow = false;
        bool claimAllowed = true;
        string? nextActiveChallengeId = null;

        EarthDB.Results results = await new EarthDB.Query(true)
            .Get("challenges", playerId, typeof(ChallengeProgressVersion))
            .Then(queryResults =>
            {
                ChallengeProgressVersion progress = queryResults.Get<ChallengeProgressVersion>("challenges");
                progress.EnsureDate(now);
                progress.ClaimedChallengeIds ??= [];

                // a season challenge can only be claimed once it is unlocked and its task is done, anything else the client sends for it
                // (for example selecting a challenge) just makes it the selected challenge when it is unlocked
                var (isSeason, seasonUnlocked, seasonComplete) = ChallengesController.GetSeasonChallengeStatus(progress, challengeId);
                claimAllowed = !isSeason || (seasonUnlocked && seasonComplete);

                bool firstClaim = claimAllowed && progress.ClaimedChallengeIds.Add(challengeId);
                progress.ActiveSeasonId = ChallengesController.ActiveSeasonId;
                string? requestedActiveId = !claimAllowed && seasonUnlocked ? challengeId : progress.ActiveSeasonChallengeId;
                progress.ActiveSeasonChallengeId = ChallengesController.SelectActiveSeasonChallengeId(progress, requestedActiveId);
                progress.UpdatedAt = now;
                claimedNow = firstClaim;
                nextActiveChallengeId = progress.ActiveSeasonChallengeId;

                var query = new EarthDB.Query(true)
                    .Update("challenges", playerId, progress);

                if (firstClaim && apiRewards is not null)
                {
                    query.Then(rewards.ToRedeemQuery(playerId, now, Program.staticData), false);
                }

                return query;
            })
            .ExecuteAsync(Program.DB, cancellationToken);

        Serilog.Log.Information($"[challenges] player {playerId} modifyState for challenge {challengeId} (claim allowed = {claimAllowed}, first claim = {claimedNow}, rewards found = {apiRewards is not null}, active season challenge is now {nextActiveChallengeId}) request: {requestBody}");

        var updates = new EarthApiResponse.UpdatesResponse(results);
        updates.Map["challenges"] = (int)(now / 1000);

        return EarthJson(new Dictionary<string, object?>
        {
            ["challengeId"] = challengeId,
            ["state"] = claimAllowed ? "Claimed" : "Active",
            ["rewards"] = claimAllowed ? apiRewards ?? rewards.ToApiResponse() : new RedeemRewards().ToApiResponse(),
            ["updates"] = new Dictionary<string, object>()
        }, updates);
    }

    private static async Task<Results<ContentHttpResult, BadRequest>> ClaimDailyLoginChallenge(
        string playerId,
        string challengeId,
        int requestedDay,
        long now,
        CancellationToken cancellationToken)
    {
        string today = DateTimeOffset.FromUnixTimeMilliseconds(now).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await TokenUtils.EnsureDailyLoginToken(playerId, cancellationToken);

        EarthDB.Results results = await new EarthDB.Query(true)
            .Get("tokens", playerId, typeof(Tokens))
            .Get("tokenClaims", playerId, typeof(TokenClaims))
            .Then(claimResults =>
            {
                Tokens tokens = claimResults.Get<Tokens>("tokens");
                TokenClaims tokenClaims = claimResults.Get<TokenClaims>("tokenClaims");

                Tokens.TokenWithId? dailyLoginToken = tokens.GetTokens()
                    .FirstOrDefault(token => token.Token is Tokens.DailyLoginToken dailyLogin && dailyLogin.Date == today);

                if (requestedDay != TokenUtils.GetDailyLoginCycleDay(tokenClaims, today) ||
                    TokenUtils.IsDailyLoginClaimedToday(tokenClaims, today) ||
                    dailyLoginToken is null)
                {
                    return new EarthDB.Query(false)
                        .Extra("claimed", false);
                }

                Tokens.Token removedToken = tokens.RemoveToken(dailyLoginToken.Id)!;
                return new EarthDB.Query(true)
                    .Update("tokens", playerId, tokens)
                    .Then(TokenUtils.DoActionsOnRedeemedToken(removedToken, playerId, now, Program.staticData), false)
                    .Extra("claimed", true)
                    .Extra("rewards", RedeemRewards.FromDBRewardsModel(((Tokens.DailyLoginToken)removedToken).Rewards).ToApiResponse());
            })
            .ExecuteAsync(Program.DB, cancellationToken);

        if (!(bool)results.GetExtra("claimed"))
        {
            return TypedResults.BadRequest();
        }

        var updates = new EarthApiResponse.UpdatesResponse(results);
        updates.Map["challenges"] = (int)(now / 1000);

        return EarthJson(new Dictionary<string, object?>
        {
            ["challengeId"] = challengeId,
            ["state"] = "Claimed",
            ["rewards"] = results.GetExtra("rewards"),
            ["updates"] = new Dictionary<string, object>()
        }, updates);
    }

    [HttpPost("timed/generate")]
    [HttpPut("timed/generate")]
    public ContentHttpResult GenerateTimedChallenges()
        => EarthJson(new Dictionary<string, object?>
        {
            ["updates"] = new Dictionary<string, object>()
        });

    [HttpPost("reset")]
    [HttpPut("reset")]
    public ContentHttpResult ResetChallenges()
        => EarthJson(new Dictionary<string, object?>
        {
            ["updates"] = new Dictionary<string, object>()
        });

    [HttpPost("continuous/{id}/remove")]
    [HttpDelete("continuous/{id}/remove")]
    public async Task<Results<ContentHttpResult, BadRequest>> RemoveContinuousChallenge(string id, CancellationToken cancellationToken)
    {
        string? playerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(playerId))
        {
            return TypedResults.BadRequest();
        }

        long now = HttpContext.GetTimestamp();
        EarthDB.Results results = await new EarthDB.Query(true)
            .Get("challenges", playerId, typeof(ChallengeProgressVersion))
            .Then(queryResults =>
            {
                ChallengeProgressVersion progress = queryResults.Get<ChallengeProgressVersion>("challenges");
                progress.EnsureDate(now);
                progress.RemovedContinuousChallengeIds ??= [];
                progress.RemovedContinuousChallengeIds.Add(id);
                progress.UpdatedAt = now;

                return new EarthDB.Query(true)
                    .Update("challenges", playerId, progress);
            })
            .ExecuteAsync(Program.DB, cancellationToken);

        var updates = new EarthApiResponse.UpdatesResponse(results);
        updates.Map["challenges"] = (int)(now / 1000);

        return EarthJson(new Dictionary<string, object?>
        {
            ["challengeId"] = id,
            ["updates"] = new Dictionary<string, object>()
        }, updates);
    }

    private static RedeemRewards ToRedeemRewards(ApiRewards? rewards)
    {
        var result = new RedeemRewards();
        if (rewards is null)
        {
            return result;
        }

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
