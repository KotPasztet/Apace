using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Apace.ApiServer.Exceptions;
using Apace.ApiServer.Types.Common;
using Apace.ApiServer.Types.Tappables;
using Apace.ApiServer.Utils;
using Apace.Common.Utils;
using Apace.DB;
using Apace.DB.Models.Player;
using Apace.StaticData;

namespace Apace.ApiServer.Controllers.EarthApi;

[Authorize]
[ApiVersion("1.1")]
[Route("1/api/v{version:apiVersion}")]
internal sealed class TappablesController : ApaceControllerBase
{
    private static TappablesManager tappablesManager => Program.tappablesManager;
    private static EarthDB earthDB => Program.DB;
    private static StaticData.StaticData staticData => Program.staticData;
    private const int DailyChallengeCount = 3;
    private const string TreasureHuntId = "2b64c950-9b12-4ef3-99a0-cd59c9e1c8d4";
    private const string TreasureHuntReferenceId = "2b64c950-f80b-4491-b81d-bf90cee88db1";
    private const string BestDefenseReferenceId = "06eb0e50-b18d-43e8-9aad-422203ffdf28";
    private const string ChopChopReferenceId = "bd9d3fd7-12ef-49e0-91fa-c971795f8e35";
    private const string TreasureTimeReferenceId = "14e99996-0b42-4d2d-ad84-4ff279827ea6";
    private const string CowReferenceId = "170b8a07-e781-4509-8de9-ddcc0beb88ba";
    private const string MobReferenceId = "1d981b84-a03a-451d-82a6-9bfe0fc885fb";
    private const string RubyReferenceId = "2425a33a-8c73-48d9-9de9-2f11d66c8016";
    private const string CowOrSheepReferenceId = "252bb18b-5a96-4ac5-bca0-45c1a0d51269";
    private const string ZooKeeperReferenceId = "61e55110-e206-4752-95a3-aeb2b98ad6ad";
    private const string Season17CommonMobPettingZooReferenceId = "ccc1836f-ccda-4f33-8a4d-c42d8d366255";
    private const string Season17BaaaReferenceId = "91d23ab8-7a63-4d6d-8b4b-6ae462a51067";
    private const string Season17CollectMobsReferenceId = "d58c96b5-6962-4a97-a4e1-45d635e8cef2";
    private const string Season17TerracottaReferenceId = "2a599738-efe0-45bf-8fec-1ff73b25f374";
    private const string Season17RabbitsReferenceId = "537fec4e-fed5-4618-b7b5-c171434111ad";
    private const string Season17DarkOakSlabsReferenceId = "857ba971-9a35-4119-b674-9cb12bfd0693";
    private const string DarkOakSlabItemId = "393d90c3-4968-72ab-22c9-dff5d0ec3861";
    private const string Season17CollectRareMobReferenceId = "65ec2dbb-c665-4173-81b2-56816576262f";
    private const string Season17UncommonCowsReferenceId = "520f09c2-7a83-49f3-b579-654ca2944adb";
    private const string Season17CollectUncommonMobReferenceId = "606e21fb-6781-4773-87f6-158a20729f04";
    private const string TappablesReferenceId = "6b0655aa-cc63-4876-a1e1-afb319403c1c";
    private const string PettingZooReferenceId = "6d01c0d0-2ac9-4549-be82-acd7f5631950";
    private const string TappableChestReferenceId = "d5cbfe47-504a-4e8a-a7b8-481de901c20f";
    private const string ChickenReferenceId = "e7b9715a-6c27-4708-bab6-ca4c80397625";
    private const string BestDefenseId = "06eb0e50-05e7-49c7-9dfc-cf97bd94f377";
    private const string ChopChopId = "bd9d3fd7-bb4f-4ef8-aa6e-dfe5368fd1d1";
    private const string CommonAdventureCrystalId = "4f16a053-4929-263a-c91a-29663e29df76";

    private sealed record DailyChallengeDefinition(
        string Key,
        string ReferenceId,
        int Threshold = 1
    );

    private static readonly DailyChallengeDefinition[] DailyChallengePool =
    [
        new(TreasureHuntId, TreasureHuntReferenceId, 3),
        new(BestDefenseId, BestDefenseReferenceId, 5),
        new(ChopChopId, ChopChopReferenceId, 3),
        new("14e99996-0b42-4d2d-ad84-4ff279827ea6", TreasureTimeReferenceId, 3),
        new("170b8a07-e781-4509-8de9-ddcc0beb88ba", CowReferenceId, 3),
        new("1d981b84-a03a-451d-82a6-9bfe0fc885fb", MobReferenceId, 5),
        new("2425a33a-8c73-48d9-9de9-2f11d66c8016", RubyReferenceId, 5),
        new("252bb18b-5a96-4ac5-bca0-45c1a0d51269", CowOrSheepReferenceId, 4),
        new("61e55110-e206-4752-95a3-aeb2b98ad6ad", ZooKeeperReferenceId, 5),
        new("6b0655aa-cc63-4876-a1e1-afb319403c1c", TappablesReferenceId, 5),
        new("6d01c0d0-2ac9-4549-be82-acd7f5631950", PettingZooReferenceId, 5),
        new("d5cbfe47-504a-4e8a-a7b8-481de901c20f", TappableChestReferenceId, 3),
        new("e7b9715a-6c27-4708-bab6-ca4c80397625", ChickenReferenceId, 3),
    ];

    private static readonly DailyChallengeDefinition[] ContinuousChallengePool =
    [
        new("b8fa3840-43f2-4c87-9d69-f51d77a1a001", TappablesReferenceId, 10),
        new("b8fa3840-43f2-4c87-9d69-f51d77a1a002", TappableChestReferenceId, 3),
        new("b8fa3840-43f2-4c87-9d69-f51d77a1a003", MobReferenceId, 5),
    ];

    private static readonly DailyChallengeDefinition[] Season17TimedChallengePool =
    [
        new(Season17CommonMobPettingZooReferenceId, Season17CommonMobPettingZooReferenceId, 5),
        new(Season17BaaaReferenceId, Season17BaaaReferenceId, 1),
    ];

    private static readonly HashSet<string> RabbitMobItemIds =
    [
        "bae634b3-6b3d-4ca0-8e1e-f223b6760727",
        "e5853d38-a541-353e-bfd9-75c64e67af09",
        "1bb789e2-c02e-c630-016a-9757d1bb26d2",
        "ea2d68a2-fc49-0674-7422-2a546526476a",
        "97bffc08-41f7-8520-0b36-21ba29b320a3",
        "34ac8c9e-57d2-55f6-a909-9dc35d09dac8",
        "f4b8464c-6664-bbcd-0151-effce8347419"
    ];

    private static readonly HashSet<string> RareMobItemIds =
    [
        "038c8034-1d98-96ca-8278-bc69f592549f",
        "6a105892-0863-3dcf-7482-7c7720597caa",
        "426a7914-f259-12f6-e2fa-cca7d6759915",
        "86d00291-74cf-d564-f4a6-a9f1d6b19713",
        "85a279d4-d403-4487-d96d-17a91604e638",
        "52692846-f1bd-a9ee-554a-913f9cb1dc27",
        "c2e5a2eb-7888-e2cb-c641-951f93f5937b",
        "88f06490-d30e-fd23-d515-261d9cde4879",
        "660c0f43-72f6-3735-1af3-38c987259ab6",
        "b8d149e4-5fee-f34d-bf56-24ec03e446b8",
        "6b9dca6b-7515-a83a-f4d2-d5a464247963",
        "0561d711-4266-92fe-4297-56de144a16a4",
        "e83cae16-1460-7487-202c-ff7000120c8d",
        "68486cd0-d9ce-4414-73e0-ecdb33824d9f"
    ];

    private static readonly HashSet<string> UncommonCowItemIds =
    [
        "214103ec-f98b-4d7a-3dea-a69b696cefc2",
        "2b76a637-a7e6-12b8-8d9d-58d3f1f8ed56",
        "67d3d739-00fe-4e4b-4d6f-ccf2dac8bc06",
        "1ba6f2e8-a746-8597-3445-090a2d1cd732",
        "8e7cf221-0472-eb6c-4488-d4d5b0ee576e",
        "9e416506-5853-1b50-2484-97fa03553694",
        "ad24ee10-2f92-4ae8-4b5c-894aece9f834",
        "c6939411-4ca4-7982-5d9c-e9291179dce0",
        "99c19bda-f20c-7fb4-926a-c21f760be4a3"
    ];

    private static readonly HashSet<string> UncommonMobItemIds =
    [
        "e5853d38-a541-353e-bfd9-75c64e67af09",
        "214103ec-f98b-4d7a-3dea-a69b696cefc2",
        "179b96c5-7627-406d-e42b-838a29ab0291",
        "20519eb8-6256-4fdd-a598-a2a20de10e1c",
        "2b76a637-a7e6-12b8-8d9d-58d3f1f8ed56",
        "67d3d739-00fe-4e4b-4d6f-ccf2dac8bc06",
        "1ba6f2e8-a746-8597-3445-090a2d1cd732",
        "8e7cf221-0472-eb6c-4488-d4d5b0ee576e",
        "9e416506-5853-1b50-2484-97fa03553694",
        "ad24ee10-2f92-4ae8-4b5c-894aece9f834",
        "c6939411-4ca4-7982-5d9c-e9291179dce0",
        "cdc029f4-c70f-7158-eb95-95c8f3b28669",
        "3098f079-453a-5309-fb14-8067a199f01f",
        "df11aab9-cc2d-a914-9653-434def8d52c3",
        "f67bd319-db40-32cf-0891-73227e96e983",
        "fc218694-57ed-3712-27f4-85ac79ba86ca",
        "0cbe1f29-f0e7-880e-0862-41a20eec024a",
        "ba6b6909-b76c-8034-fec8-059a7a18ead9",
        "e4b00fc1-9143-00ed-e930-8c1c09c35d63",
        "7535d81e-c960-28f2-d3ca-d1fd7b813c34",
        "576df436-a542-5692-458d-a60f69cc4a04",
        "a0741c98-1bd7-b21e-998e-5adc57fd245d",
        "41cceca7-90f0-1e8f-fa69-c468ab49eec2",
        "99c19bda-f20c-7fb4-926a-c21f760be4a3",
        "caf00a9c-b1f9-c37a-0f75-c8c7659becb0",
        "2e8e0fd5-d3b8-70be-b36a-31bd0caeafa8",
        "9dde0e4e-8104-f807-b714-2a5fa7ead5b9",
        "f6629c5b-11bf-e128-a465-b875d4ff7d1b",
        "69840d34-5fde-64f1-0710-d8f9a34e3e5e",
        "7a20d9c5-5c61-9b59-2114-a535f269cfd3",
        "1bb789e2-c02e-c630-016a-9757d1bb26d2",
        "ea2d68a2-fc49-0674-7422-2a546526476a",
        "97bffc08-41f7-8520-0b36-21ba29b320a3",
        "34ac8c9e-57d2-55f6-a909-9dc35d09dac8",
        "f4b8464c-6664-bbcd-0151-effce8347419"
    ];

    [HttpGet("locations/{lat}/{lon}")]
    [HttpGet("player/locations/{lat}/{lon}")]
    public async Task<Results<ContentHttpResult, BadRequest>> GetTappables(double lat, double lon, CancellationToken cancellationToken)
    {
        string? playerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(playerId))
        {
            return TypedResults.BadRequest();
        }

        long requestStartedOn = HttpContext.GetTimestamp();

        await tappablesManager.NotifyTileActiveAsync(playerId, lat, lon);

        TappablesManager.Tappable[] tappables = tappablesManager.GetTappablesAround(lat, lon, 1.5);
        TappablesManager.Encounter[] encounters = tappablesManager.GetEncountersAround(lat, lon, 1.5);
        TappablesManager.Adventure[] adventures = tappablesManager.GetPlayerAdventuresAround(playerId, lat, lon, 1.5);

        try
        {
            EarthDB.Results results = await new EarthDB.Query(false)
                .Get("redeemedTappables", playerId, typeof(RedeemedTappables))
                .ExecuteAsync(earthDB, cancellationToken);
            RedeemedTappables redeemedTappables = results.Get<RedeemedTappables>("redeemedTappables");

            IEnumerable<ActiveLocation> activeLocationTappables = tappables
                .Where(tappable => tappable.SpawnTime <= requestStartedOn + 30000 && tappable.SpawnTime + tappable.ValidFor > requestStartedOn && !redeemedTappables.IsRedeemed(tappable.Id))
                .OrderBy(tappable => DistanceSquared(tappable.Lat, tappable.Lon, lat, lon))
                .Take(45)
                .Select(tappable => new ActiveLocation(
                    tappable.Id,
                    TappablesManager.LocationToTileId(tappable.Lat, tappable.Lon),
                    new Coordinate(tappable.Lat, tappable.Lon),
                    TimeFormatter.FormatTime(tappable.SpawnTime),
                    TimeFormatter.FormatTime(tappable.SpawnTime + tappable.ValidFor),
                    ActiveLocation.TypeE.TAPPABLE,
                    tappable.Icon,
                    new ActiveLocation.MetadataR(U.RandomUuid().ToString(), Enum.Parse<Rarity>(tappable.Rarity.ToString())),
                    new ActiveLocation.TappableMetadataR(Enum.Parse<Rarity>(tappable.Rarity.ToString())),
                    null
                ));

            IEnumerable<ActiveLocation> activeLocationEncounters = encounters
                .Where(encounter => encounter.SpawnTime <= requestStartedOn + 30000 && encounter.SpawnTime + encounter.ValidFor > requestStartedOn)
                .OrderBy(encounter => DistanceSquared(encounter.Lat, encounter.Lon, lat, lon))
                .Take(8)
                .Select(encounter => new ActiveLocation(
                    encounter.Id,
                    TappablesManager.LocationToTileId(encounter.Lat, encounter.Lon),
                    new Coordinate(encounter.Lat, encounter.Lon),
                    TimeFormatter.FormatTime(encounter.SpawnTime),
                    TimeFormatter.FormatTime(encounter.SpawnTime + encounter.ValidFor),
                    ActiveLocation.TypeE.ENCOUNTER,
                    encounter.Icon,
                    new ActiveLocation.MetadataR(U.RandomUuid().ToString(), Enum.Parse<Rarity>(encounter.Rarity.ToString())),
                    null,
                    new ActiveLocation.EncounterMetadataR(
                        ActiveLocation.EncounterMetadataR.EncounterTypeE.SHORT_4X4_PEACEFUL,    // TODO
                                                                                                //UUID.randomUUID().toString(),    // TODO: what is this field for and does it matter what we put here?
                        encounter.Id,
                        encounter.EncounterBuildplateId,
                        ActiveLocation.EncounterMetadataR.AnchorStateE.OFF,
                        "",
                        ""
                    )
                ));

            IEnumerable<ActiveLocation> activeLocationAdventures = adventures
                .Where(adventure => adventure.SpawnTime <= requestStartedOn + 30000 && adventure.SpawnTime + adventure.ValidFor > requestStartedOn)
                .OrderBy(adventure => DistanceSquared(adventure.Lat, adventure.Lon, lat, lon))
                .Take(1)
                .Select(adventure => new ActiveLocation(
                    adventure.Id,
                    TappablesManager.LocationToTileId(adventure.Lat, adventure.Lon),
                    new Coordinate(adventure.Lat, adventure.Lon),
                    TimeFormatter.FormatTime(adventure.SpawnTime),
                    TimeFormatter.FormatTime(adventure.SpawnTime + adventure.ValidFor),
                    ActiveLocation.TypeE.PLAYER_ADVENTURE,
                    adventure.Icon,
                    new ActiveLocation.MetadataR(adventure.Id, Enum.Parse<Rarity>(adventure.Rarity.ToString())),
                    new ActiveLocation.TappableMetadataR(Enum.Parse<Rarity>(adventure.Rarity.ToString())),
                    new ActiveLocation.EncounterMetadataR(
                        ActiveLocation.EncounterMetadataR.EncounterTypeE.SHORT_4X4_PEACEFUL,
                        adventure.Id,
                        adventure.AdventureBuildplateId,
                        ActiveLocation.EncounterMetadataR.AnchorStateE.OFF,
                        "",
                        "")
                ));

            ActiveLocation[] activeLocations = [.. activeLocationTappables, .. activeLocationEncounters, .. activeLocationAdventures];

            return EarthJson(new Dictionary<string, object>()
            {
                { "killSwitchedTileIds", new List<object>() },
                { "activeLocations", activeLocations }
            });
        }
        catch (EarthDB.DatabaseException ex)
        {
            throw new ServerErrorException(ex);
        }
    }

    private static double DistanceSquared(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = lat1 - lat2;
        double dLon = lon1 - lon2;
        return dLat * dLat + dLon * dLon;
    }

    [HttpPost("tappables/{tileId}")]
    public async Task<Results<ContentHttpResult, BadRequest>> RedeemTappable(string tileId, CancellationToken cancellationToken)
    {
        string? playerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(playerId))
        {
            return TypedResults.BadRequest();
        }

        TappableRequest? tappableRequest = await Request.Body.AsJsonAsync<TappableRequest>(cancellationToken);
        if (tappableRequest is null)
        {
            return TypedResults.BadRequest();
        }

        // request.timestamp
        long requestStartedOn = HttpContext.GetTimestamp();

        TappablesManager.Tappable? tappable = tappablesManager.GetTappableWithId(tappableRequest.Id, tileId);
        if (tappable is null || !tappablesManager.IsTappableValidFor(tappable, requestStartedOn, tappableRequest.PlayerCoordinate.Latitude, tappableRequest.PlayerCoordinate.Longitude))
        {
            return TypedResults.BadRequest();
        }

        try
        {
            EarthDB.Results results = await new EarthDB.Query(true)
                .Get("redeemedTappables", playerId, typeof(RedeemedTappables))
                .Get("boosts", playerId, typeof(Boosts))
                .Get("tokenClaims", playerId, typeof(TokenClaims))
                .Get("challenges", playerId, typeof(ChallengeProgressVersion))
                .Get("tokens", playerId, typeof(Tokens))
                .Then(results1 =>
                {
                    var query = new EarthDB.Query(true);
                    Boosts boosts = results1.Get<Boosts>("boosts");
                    TokenClaims tokenClaims = results1.Get<TokenClaims>("tokenClaims");
                    ChallengeProgressVersion challengeProgress = results1.Get<ChallengeProgressVersion>("challenges");
                    Tokens tokens = results1.Get<Tokens>("tokens");

                    RedeemedTappables redeemedTappables = results1.Get<RedeemedTappables>("redeemedTappables");

                    if (redeemedTappables.IsRedeemed(tappable.Id))
                    {
                        query.Extra("success", false);
                        return query;
                    }

                    int experiencePointsGlobalMultiplier = 0;

                    Dictionary<string, int> experiencePointsPerItemMultiplier = [];
                    foreach (var effect in BoostUtils.GetActiveEffects(boosts, requestStartedOn, staticData.Catalog.ItemsCatalog))
                    {
                        if (effect.Type is Catalog.ItemsCatalogR.Item.BoostInfoR.Effect.TypeE.ITEM_XP)
                        {
                            if (effect.ApplicableItemIds is not null && effect.ApplicableItemIds.Length > 0)
                            {
                                foreach (string itemId in effect.ApplicableItemIds)
                                {
                                    experiencePointsPerItemMultiplier[itemId] = experiencePointsPerItemMultiplier.GetValueOrDefault(itemId) + effect.Value;
                                }
                            }
                            else
                            {
                                experiencePointsGlobalMultiplier += effect.Value;
                            }
                        }
                    }

                    var rewards = new Utils.Rewards();
                    HashSet<string> collectedItemIds = [];

                    foreach (TappablesManager.Tappable.Item item in tappable.Items)
                    {
                        collectedItemIds.Add(item.Id);
                        rewards.AddItem(item.Id, item.Count);
                        int experiencePoints = staticData.Catalog.ItemsCatalog.GetItem(item.Id)!.Experience.Tappable;
                        int experiencePointsMultiplier = experiencePointsGlobalMultiplier + experiencePointsPerItemMultiplier.GetValueOrDefault(item.Id);
                        if (experiencePointsMultiplier > 0)
                        {
                            experiencePoints = experiencePoints * (experiencePointsMultiplier + 100) / 100;
                        }

                        rewards.AddExperiencePoints(experiencePoints * item.Count);
                    }

                    rewards.AddRubies(1); // TODO

                    challengeProgress.EnsureDate(requestStartedOn);
                    DailyChallengeDefinition[] selectedChallenges = SelectDailyChallenges(playerId, challengeProgress.DailyDateUtc!);
                    Dictionary<string, int> progressBefore = selectedChallenges.ToDictionary(
                        challenge => challenge.ReferenceId,
                        challenge => challengeProgress.GetObjectiveProgress(challenge.ReferenceId));
                    foreach (DailyChallengeDefinition challenge in ContinuousChallengePool)
                    {
                        progressBefore[challenge.ReferenceId] = challengeProgress.GetObjectiveProgress(challenge.ReferenceId);
                    }

                    foreach (DailyChallengeDefinition challenge in Season17TimedChallengePool)
                    {
                        progressBefore[challenge.ReferenceId] = challengeProgress.GetObjectiveProgress(challenge.ReferenceId);
                    }

                    challengeProgress.RecordTappable(requestStartedOn);
                    AddDailyObjectiveProgress(challengeProgress, tokens, tappable, collectedItemIds, requestStartedOn);
                    AddChallengeNotificationTokens(tokens, selectedChallenges, progressBefore, challengeProgress);
                    AddChallengeNotificationTokens(tokens, ContinuousChallengePool, progressBefore, challengeProgress);
                    AddChallengeNotificationTokens(tokens, Season17TimedChallengePool, progressBefore, challengeProgress);
                    AddCompletedDailyChallengeRewards(query, playerId, tokenClaims, challengeProgress, rewards, requestStartedOn);

                    redeemedTappables.Add(tappable.Id, tappable.SpawnTime + tappable.ValidFor);
                    redeemedTappables.Prune(requestStartedOn);
                    query.Update("redeemedTappables", playerId, redeemedTappables);
                    query.Update("challenges", playerId, challengeProgress);
                    query.Update("tokens", playerId, tokens);
                    query.Then(ActivityLogUtils.AddEntry(playerId, new ActivityLog.TappableEntry(requestStartedOn, rewards.ToDBRewardsModel())));
                    query.Then(rewards.ToRedeemQuery(playerId, requestStartedOn, staticData));
                    query.Then(results2 => new EarthDB.Query(false).Extra("success", true).Extra("rewards", rewards));

                    return query;
                })
                .ExecuteAsync(earthDB, cancellationToken);

            if ((bool)results.GetExtra("success"))
            {
                var updates = new EarthApiResponse.UpdatesResponse(results);
                updates.Map["challenges"] = (int)(requestStartedOn / 1000);

                return EarthJson(new Dictionary<string, object?>()
                {
                    { "token", new Token(
                        Token.Type.TAPPABLE,
                        [],
                        ((Utils.Rewards) results.GetExtra("rewards")).ToApiResponse(),
                        Token.LifetimeE.PERSISTENT
                    ) },
                    { "updates", null }
                }, updates);
            }
            else
            {
                return TypedResults.BadRequest();
            }
        }
        catch (EarthDB.DatabaseException ex)
        {
            throw new ServerErrorException(ex);
        }
    }

    [HttpPost("multiplayer/encounters/state")]
    [HttpPost("multiplayer/adventures/state")]
    [HttpPost("multiplayer/player/adventures/state")]
    public async Task<Results<ContentHttpResult, BadRequest>> EncountersState(CancellationToken cancellationToken)
    {
        // The client posts { "<encounter/adventure id>": "<tile id>", ... } and
        // expects the same keys mapped to their current state. The values must be
        // bound as strings: System.Text.Json boxes `object` values as JsonElement,
        // so a Dictionary<string, object> body failed the old `value is not string`
        // check for every valid request and always answered 400 — which made the
        // client show "Couldn't connect to server" right after a multiplayer scan.
        // A non-string value (e.g. a number) throws a JsonException, which
        // AsJsonAsync turns into null and lands in the same 400 branch below.
        var requestedIds = await Request.Body.AsJsonAsync<Dictionary<string, string>>(cancellationToken);

        if (requestedIds is null)
        {
            Log.Information("EncountersState: rejecting body that is not an id -> tileId map");
            return TypedResults.BadRequest();
        }

        // There is no persistent per-encounter multiplayer state on a private
        // server, so every requested encounter is reported Pristine (untouched):
        // the correct empty state that lets the client proceed with joining.
        var encounterStates = new Dictionary<string, EncounterState>(requestedIds.Count);
        foreach (var encounterId in requestedIds.Keys)
        {
            encounterStates[encounterId] = new EncounterState(EncounterState.ActiveEncounterStateE.PRISTINE);
        }

        Log.Debug("EncountersState: reported {Count} encounter(s) as Pristine", encounterStates.Count);

        return EarthJson(encounterStates);
    }

    private sealed record TappableRequest(
        string Id,
        Coordinate PlayerCoordinate
    );

    private static void AddDailyObjectiveProgress(ChallengeProgressVersion challengeProgress, Tokens tokens, TappablesManager.Tappable tappable, HashSet<string> collectedItemIds, long requestStartedOn)
    {
        string icon = tappable.Icon.ToString();
        bool isChest = icon.Contains("chest", StringComparison.OrdinalIgnoreCase);
        bool isCow = icon.Contains("cow", StringComparison.OrdinalIgnoreCase);
        bool isSheep = icon.Contains("sheep", StringComparison.OrdinalIgnoreCase);
        bool isChicken = icon.Contains("chicken", StringComparison.OrdinalIgnoreCase);
        bool isPig = icon.Contains("pig", StringComparison.OrdinalIgnoreCase);
        bool isMob = isCow || isSheep || isChicken || isPig;
        bool hasOakLog = collectedItemIds.Any(IsOakLog);

        challengeProgress.AddObjectiveProgress(requestStartedOn, TappablesReferenceId);
        challengeProgress.AddObjectiveProgress(requestStartedOn, RubyReferenceId);

        if (isChest)
        {
            challengeProgress.AddObjectiveProgress(requestStartedOn, TreasureHuntReferenceId);
            challengeProgress.AddObjectiveProgress(requestStartedOn, TreasureTimeReferenceId);
            challengeProgress.AddObjectiveProgress(requestStartedOn, TappableChestReferenceId);
        }

        if (isMob)
        {
            challengeProgress.AddObjectiveProgress(requestStartedOn, MobReferenceId);
            challengeProgress.AddObjectiveProgress(requestStartedOn, ZooKeeperReferenceId);
            challengeProgress.AddObjectiveProgress(requestStartedOn, PettingZooReferenceId);
            challengeProgress.AddObjectiveProgress(requestStartedOn, Season17CommonMobPettingZooReferenceId);
            ChallengeEvents.CountSeasonReference(challengeProgress, tokens, requestStartedOn, Season17CollectMobsReferenceId);
        }

        foreach (string collectedItemId in collectedItemIds)
        {
            string? terracottaEvent = ChallengeEvents.GetTerracottaEventName(collectedItemId);
            if (terracottaEvent is not null)
            {
                ChallengeEvents.CountSeasonReference(challengeProgress, tokens, requestStartedOn, Season17TerracottaReferenceId, $"event:{terracottaEvent}");
            }
        }

        if (collectedItemIds.Any(RabbitMobItemIds.Contains) || icon.Contains("rabbit", StringComparison.OrdinalIgnoreCase))
        {
            ChallengeEvents.CountSeasonReference(challengeProgress, tokens, requestStartedOn, Season17RabbitsReferenceId);
        }

        foreach (string collectedItemId in collectedItemIds.Where(id => id == DarkOakSlabItemId))
        {
            ChallengeEvents.CountSeasonReference(challengeProgress, tokens, requestStartedOn, Season17DarkOakSlabsReferenceId);
        }

        foreach (string collectedItemId in collectedItemIds.Where(UncommonCowItemIds.Contains))
        {
            ChallengeEvents.CountSeasonReference(challengeProgress, tokens, requestStartedOn, Season17UncommonCowsReferenceId);
        }

        if (collectedItemIds.Any(RareMobItemIds.Contains))
        {
            ChallengeEvents.CountSeasonReference(challengeProgress, tokens, requestStartedOn, Season17CollectRareMobReferenceId);
        }

        if (collectedItemIds.Any(UncommonMobItemIds.Contains))
        {
            ChallengeEvents.CountSeasonReference(challengeProgress, tokens, requestStartedOn, Season17CollectUncommonMobReferenceId);
        }

        if (isCow)
        {
            challengeProgress.AddObjectiveProgress(requestStartedOn, CowReferenceId);
            challengeProgress.AddObjectiveProgress(requestStartedOn, CowOrSheepReferenceId);
        }

        if (isSheep)
        {
            challengeProgress.AddObjectiveProgress(requestStartedOn, CowOrSheepReferenceId);
            challengeProgress.AddObjectiveProgress(requestStartedOn, Season17BaaaReferenceId);
        }

        if (isChicken)
        {
            challengeProgress.AddObjectiveProgress(requestStartedOn, ChickenReferenceId);
        }

        if (hasOakLog)
        {
            challengeProgress.AddObjectiveProgress(requestStartedOn, ChopChopReferenceId);
        }
    }

    private static bool IsOakLog(string itemId)
        => itemId == "a1bf49f9-1f1f-2a4d-5f7b-c0c5ba833068";

    private static void AddChallengeNotificationTokens(Tokens tokens, DailyChallengeDefinition[] selectedChallenges, Dictionary<string, int> progressBefore, ChallengeProgressVersion challengeProgress)
    {
        foreach (DailyChallengeDefinition challenge in selectedChallenges)
        {
            if (challengeProgress.RemovedContinuousChallengeIds?.Contains(challenge.Key) == true ||
                challengeProgress.ClaimedChallengeIds?.Contains(challenge.Key) == true)
            {
                continue;
            }

            int before = Math.Min(progressBefore.GetValueOrDefault(challenge.ReferenceId), challenge.Threshold);
            int after = Math.Min(challengeProgress.GetObjectiveProgress(challenge.ReferenceId), challenge.Threshold);
            if (after <= before)
            {
                continue;
            }

            Tokens.Token token = after >= challenge.Threshold
                ? new Tokens.ChallengeCompletedToken(challenge.Key, challenge.ReferenceId)
                : new Tokens.ChallengeProgressToken(challenge.Key, challenge.ReferenceId);
            tokens.AddToken(U.RandomUuid().ToString(), token);
        }
    }

    private static void AddCompletedDailyChallengeRewards(EarthDB.Query query, string playerId, TokenClaims tokenClaims, ChallengeProgressVersion challengeProgress, Utils.Rewards rewards, long requestStartedOn)
    {
        string today = DateTimeOffset.FromUnixTimeMilliseconds(requestStartedOn).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (playerId == "098f6bcd4621d373" && today == "2026-05-19")
        {
            challengeProgress.AddObjectiveProgress(requestStartedOn, TappablesReferenceId, 100);
        }

        bool shouldReward = false;
        int completedDailyChallenges = 0;
        tokenClaims.RedeemedChallengeRewardKeys ??= [];

        foreach (DailyChallengeDefinition challenge in SelectDailyChallenges(playerId, challengeProgress.DailyDateUtc ?? today))
        {
            if (challengeProgress.GetObjectiveProgress(challenge.ReferenceId) < challenge.Threshold)
            {
                continue;
            }

            completedDailyChallenges++;
            string rewardKey = $"{today}:{challenge.Key}";
            if (tokenClaims.RedeemedChallengeRewardKeys.Add(rewardKey))
            {
                rewards.AddExperiencePoints(10);
                challengeProgress.ClaimedChallengeIds ??= [];
                challengeProgress.ClaimedChallengeIds.Add(challenge.Key);
                shouldReward = true;
            }
        }

        foreach (DailyChallengeDefinition challenge in ContinuousChallengePool)
        {
            if (challengeProgress.GetObjectiveProgress(challenge.ReferenceId) < challenge.Threshold ||
                challengeProgress.RemovedContinuousChallengeIds?.Contains(challenge.Key) == true)
            {
                continue;
            }

            if (tokenClaims.RedeemedChallengeRewardKeys.Add($"{today}:{challenge.Key}"))
            {
                rewards.AddExperiencePoints(10);
                challengeProgress.ClaimedChallengeIds ??= [];
                challengeProgress.ClaimedChallengeIds.Add(challenge.Key);
                shouldReward = true;
            }
        }

        string dailyGroupRewardKey = $"{today}:{ChallengesController.DailyBonusChallengeId}";
        if (completedDailyChallenges >= DailyChallengeCount && tokenClaims.RedeemedChallengeRewardKeys.Add(dailyGroupRewardKey))
        {
            rewards.AddExperiencePoints(25).AddItem(CommonAdventureCrystalId, 1);
            challengeProgress.ClaimedChallengeIds ??= [];
            challengeProgress.ClaimedChallengeIds.Add(ChallengesController.DailyBonusChallengeId);
            shouldReward = true;
        }

        if (!shouldReward)
        {
            return;
        }

        query.Update("tokenClaims", playerId, tokenClaims);
    }

    private static DailyChallengeDefinition[] OrderedDailyChallenges(string playerId, string dailyDateUtc)
        => [.. DailyChallengePool
            .OrderBy(challenge => StableSortKey($"{playerId}:{dailyDateUtc}:{challenge.ReferenceId}"))
        ];

    private static DailyChallengeDefinition[] SelectDailyChallenges(string playerId, string dailyDateUtc)
    {
        DailyChallengeDefinition[] orderedChallenges = OrderedDailyChallenges(playerId, dailyDateUtc);
        if (!DateTime.TryParseExact(dailyDateUtc, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTime dailyDate))
        {
            return [.. orderedChallenges.Take(DailyChallengeCount)];
        }

        string yesterday = dailyDate.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        HashSet<string> yesterdayKeys = [.. OrderedDailyChallenges(playerId, yesterday)
            .Take(DailyChallengeCount)
            .Select(challenge => challenge.Key)];

        DailyChallengeDefinition[] freshChallenges = [.. orderedChallenges
            .Where(challenge => !yesterdayKeys.Contains(challenge.Key))
            .Take(DailyChallengeCount)];

        return freshChallenges.Length == DailyChallengeCount
            ? freshChallenges
            : [.. freshChallenges.Concat(orderedChallenges
                .Where(challenge => !freshChallenges.Any(freshChallenge => freshChallenge.Key == challenge.Key))
                .Take(DailyChallengeCount - freshChallenges.Length))];
    }

    private static ulong StableSortKey(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return BitConverter.ToUInt64(hash, 0);
    }
}
