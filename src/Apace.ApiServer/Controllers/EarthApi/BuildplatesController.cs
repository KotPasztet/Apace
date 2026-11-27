using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using Apace.ApiServer.Exceptions;
using Apace.ApiServer.Types.Buildplates;
using Apace.ApiServer.Types.Common;
using Apace.ApiServer.Types.Inventory;
using Apace.ApiServer.Types.Tappables;
using Apace.ApiServer.Utils;
using Apace.Common.Utils;
using Apace.DB;
using Apace.DB.Models.Global;
using Apace.DB.Models.Player;
using Apace.ObjectStore.Client;
using Apace.StaticData;
using Buildplates = Apace.DB.Models.Player.Buildplates;

namespace Apace.ApiServer.Controllers.EarthApi;

[Authorize]
[ApiVersion("1.1")]
[Route("1/api/v{version:apiVersion}")]
internal sealed class BuildplatesController : ApaceControllerBase
{
    // Tapping a pin that is about to expire should not give a few seconds of play: every encounter instance lasts at least this long
    private const long MinEncounterInstanceLifetimeMs = 30 * 60 * 1000;

    // Number of 1-second readiness polls before giving up on an instance (the loop in WaitForInstanceReadyAsync)
    private const int WaitForReadyMaxSeconds = 90;

    private static readonly SemaphoreSlim AdventureScrollLock = new(1, 1);
    private static EarthDB earthDB => Program.DB;
    private static BuildplateInstancesManager buildplateInstancesManager => Program.buildplateInstancesManager;
    private static Catalog catalog => Program.staticData.Catalog;
    private static TappablesManager tappablesManager => Program.tappablesManager;
    private static readonly Lazy<AdventureFootprints?> adventureFootprints = new(() => LoadAdventureFootprints(Program.staticData));

    [HttpGet("buildplates")]
    public async Task<Results<ContentHttpResult, BadRequest>> GetBuildplates(CancellationToken cancellationToken)
    {
        string? playerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(playerId))
        {
            return TypedResults.BadRequest();
        }

        Buildplates buildplatesModel;
        try
        {
            EarthDB.Results results = await new EarthDB.Query(false)
                .Get("buildplates", playerId, typeof(Buildplates))
                .ExecuteAsync(earthDB, cancellationToken);
            buildplatesModel = results.Get<Buildplates>("buildplates");
        }
        catch (EarthDB.DatabaseException ex)
        {
            throw new ServerErrorException(ex);
        }

        await using var objectStoreClient = await Program.GetObjectStoreClient();

        OwnedBuildplate[] ownedBuildplates = await Task.WhenAll(buildplatesModel.GetBuildplates().Select(async buildplateEntry =>
        {
            byte[]? previewData = await objectStoreClient.GetAsync(buildplateEntry.Buildplate.PreviewObjectId);
            if (previewData is null)
            {
                Log.Error($"Preview object {buildplateEntry.Buildplate.PreviewObjectId} for buildplate {buildplateEntry.Id} could not be loaded from object store");
                return null!;
            }

            string model = Encoding.ASCII.GetString(previewData);
            return new OwnedBuildplate(
                buildplateEntry.Id,
                "00000000-0000-0000-0000-000000000000",
                new Dimension(buildplateEntry.Buildplate.Size, buildplateEntry.Buildplate.Size),
                new Offset(0, buildplateEntry.Buildplate.Offset, 0),
                buildplateEntry.Buildplate.Scale,
                OwnedBuildplate.TypeE.SURVIVAL,
                SurfaceOrientation.HORIZONTAL,
                model,
                0,    // TODO
                false,    // TODO
                0,    // TODO
                false,    // TODO
                TimeFormatter.FormatTime(buildplateEntry.Buildplate.LastModified),
                0,    // TODO
                ""
            );
        }).Where(ownedBuildplate => ownedBuildplate is not null));

        return EarthJson(ownedBuildplates);
    }

    [HttpPost("multiplayer/buildplate/{buildplateId}/instances")]
    public async Task<Results<ContentHttpResult, InternalServerError, NotFound, BadRequest>> CreateBuildInstance(string buildplateId, CancellationToken cancellationToken)
    {
        // TODO: coordinates etc.

        string? playerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return string.IsNullOrEmpty(playerId)
            ? TypedResults.BadRequest()
            : await GetNewBuildplateInstanceResponse(playerId, buildplateId, BuildplateInstancesManager.InstanceType.BUILD, cancellationToken);
    }

    [HttpPost("multiplayer/buildplate/{buildplateId}/play/instances")]
    public async Task<Results<ContentHttpResult, InternalServerError, NotFound, BadRequest>> CreatePlayInstance(string buildplateId, CancellationToken cancellationToken)
    {
        // TODO: coordinates etc.

        string? playerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return string.IsNullOrEmpty(playerId)
            ? TypedResults.BadRequest()
            : await GetNewBuildplateInstanceResponse(playerId, buildplateId, BuildplateInstancesManager.InstanceType.PLAY, cancellationToken);
    }

    [HttpPost("buildplates/{buildplateId}/share")]
    public async Task<Results<ContentHttpResult, BadRequest, NotFound, InternalServerError>> ShareBuildplate(string buildplateId, CancellationToken cancellationToken)
    {
        string? playerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(playerId))
        {
            return TypedResults.BadRequest();
        }

        long requestStartedOn = HttpContext.GetTimestamp();

        DB.Models.Player.Inventory inventory;
        Hotbar hotbar;
        Buildplates.Buildplate? buildplate;
        try
        {
            EarthDB.Results results = await new EarthDB.Query(false)
                .Get("inventory", playerId, typeof(DB.Models.Player.Inventory))
                .Get("hotbar", playerId, typeof(Hotbar))
                .Get("buildplates", playerId, typeof(Buildplates))
                .ExecuteAsync(earthDB, cancellationToken);

            inventory = results.Get<DB.Models.Player.Inventory>("inventory");
            hotbar = results.Get<Hotbar>("hotbar");
            buildplate = results.Get<Buildplates>("buildplates").GetBuildplate(buildplateId);
        }
        catch (EarthDB.DatabaseException exception)
        {
            throw new ServerErrorException(exception);
        }

        if (buildplate is null)
        {
            return TypedResults.NotFound();
        }

        await using var objectStoreClient = await Program.GetObjectStoreClient();

        byte[]? serverData = await objectStoreClient.GetAsync(buildplate.ServerDataObjectId);
        if (serverData is null)
        {
            Log.Error($"Data object {buildplate.ServerDataObjectId} for buildplate {buildplateId} could not be loaded from object store");
            return TypedResults.InternalServerError();
        }

        string? sharedBuildplateServerDataObjectId = await objectStoreClient.StoreAsync(serverData);
        if (sharedBuildplateServerDataObjectId is null)
        {
            Log.Error("Could not store data object for shared buildplate in object store");
            return TypedResults.InternalServerError();
        }

        string sharedBuildplateId = U.RandomUuid().ToString();
        var sharedBuildplate = new SharedBuildplates.SharedBuildplate(
            playerId,
            buildplate.Size,
            buildplate.Offset,
            buildplate.Scale,
            buildplate.Night,
            requestStartedOn,
            buildplate.LastModified,
            sharedBuildplateServerDataObjectId
        );

        for (int index = 0; index < 7; index++)
        {
            Hotbar.Item? item = hotbar.Items[index];
            SharedBuildplates.SharedBuildplate.HotbarItem? sharedBuildplateHotbarItem;
            if (item is null)
            {
                sharedBuildplateHotbarItem = null;
            }
            else if (item.InstanceId is null)
            {
                sharedBuildplateHotbarItem = new SharedBuildplates.SharedBuildplate.HotbarItem(item.Uuid, item.Count, null, 0);
            }
            else
            {
                sharedBuildplateHotbarItem = new SharedBuildplates.SharedBuildplate.HotbarItem(item.Uuid, 1, item.InstanceId, inventory.GetItemInstance(item.Uuid, item.InstanceId)?.Wear ?? 0);
            }

            sharedBuildplate.Hotbar[index] = sharedBuildplateHotbarItem;
        }

        try
        {
            EarthDB.Results results = await new EarthDB.Query(true)
                .Get("sharedBuildplates", "", typeof(SharedBuildplates))
                .Then(results1 =>
                {
                    SharedBuildplates sharedBuildplates = results1.Get<SharedBuildplates>("sharedBuildplates");

                    sharedBuildplates.AddSharedBuildplate(sharedBuildplateId, sharedBuildplate);

                    return new EarthDB.Query(true)
                        .Update("sharedBuildplates", "", sharedBuildplates);
                })
                .ExecuteAsync(earthDB, cancellationToken);
        }
        catch (EarthDB.DatabaseException exception)
        {
            await objectStoreClient.DeleteAsync(sharedBuildplateServerDataObjectId);
            throw new ServerErrorException(exception);
        }

        return EarthJson($"minecraftearth://sharedbuildplate?id={sharedBuildplateId}");
    }

    [HttpGet("buildplates/shared/{sharedBuildplateId}")]
    public async Task<Results<ContentHttpResult, BadRequest, NotFound, InternalServerError>> GetSharedBuildplate(string sharedBuildplateId, CancellationToken cancellationToken)
    {
        string? playerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(playerId))
        {
            return TypedResults.BadRequest();
        }

        SharedBuildplates.SharedBuildplate? sharedBuildplate;
        try
        {
            EarthDB.Results results = await new EarthDB.Query(false)
                    .Get("sharedBuildplates", "", typeof(SharedBuildplates))
                        .ExecuteAsync(earthDB, cancellationToken);
            SharedBuildplates sharedBuildplates = results.Get<SharedBuildplates>("sharedBuildplates");
            sharedBuildplate = sharedBuildplates.GetSharedBuildplate(sharedBuildplateId);
        }
        catch (EarthDB.DatabaseException exception)
        {
            throw new ServerErrorException(exception);
        }

        if (sharedBuildplate is null)
        {
            return TypedResults.NotFound();
        }
        
        await using var objectStoreClient = await Program.GetObjectStoreClient();

        byte[]? serverData = await objectStoreClient.GetAsync(sharedBuildplate.ServerDataObjectId);
        if (serverData is null)
        {
            Log.Error($"Data object {sharedBuildplate.ServerDataObjectId} for shared buildplate {sharedBuildplateId} could not be loaded from object store");
            return TypedResults.InternalServerError();
        }

        string? preview = await buildplateInstancesManager.GetBuildplatePreviewAsync(serverData, sharedBuildplate.Night);
        if (preview is null)
        {
            Log.Error("Could not get preview for buildplate");
            return TypedResults.InternalServerError();
        }

        return EarthJson(new SharedBuildplate(
            sharedBuildplate.PlayerId,    // TODO: supposed to return username here, not player ID
            TimeFormatter.FormatTime(sharedBuildplate.Created),
            new SharedBuildplate.BuildplateDataR(
                new Dimension(sharedBuildplate.Size, sharedBuildplate.Size),
                new Offset(0, sharedBuildplate.Offset, 0),
                sharedBuildplate.Scale,
                SharedBuildplate.BuildplateDataR.TypeE.SURVIVAL,
                SurfaceOrientation.HORIZONTAL,
                preview,
                0
            ),
            new Types.Inventory.Inventory(
                [.. sharedBuildplate.Hotbar.Select(item => item is not null ? new HotbarItem(
                    item.Uuid,
                    item.Count,
                    item.InstanceId,
                    item.InstanceId is not null ? ItemWear.WearToHealth(item.Uuid, item.Wear, catalog.ItemsCatalog) : 0.0f
                ) : null)],
                [.. sharedBuildplate.Hotbar
                    .Where(item => item is not null && item.InstanceId is null)
                    .Select(item => item!.Uuid)
                    .Distinct()
                    .Select(uuid => new StackableInventoryItem(
                        uuid,
                        0,
                        1,
                        // TODO: what unlocked/last seen timestamp are we supposed to use here - the player who shared the buildplate or the player who is viewing the buildplate?
                        new StackableInventoryItem.OnR(TimeFormatter.FormatTime(0)),
                        new StackableInventoryItem.OnR(TimeFormatter.FormatTime(0))
                    ))],
                [.. sharedBuildplate.Hotbar
                    .Where(item => item is not null && item.InstanceId is not null)
                    .Select(item => item!.Uuid)
                    .Distinct()
                    .Select(uuid => new NonStackableInventoryItem(
                        uuid,
                        [],
                        1,
                        // TODO: what unlocked/last seen timestamp are we supposed to use here - the player who shared the buildplate or the player who is viewing the buildplate?
                        new NonStackableInventoryItem.OnR(TimeFormatter.FormatTime(0)),
                        new NonStackableInventoryItem.OnR(TimeFormatter.FormatTime(0))
                    ))]
            )
        ));
    }

    [HttpPost("multiplayer/buildplate/shared/{sharedBuildplateId}/play/instances")]
    public async Task<Results<ContentHttpResult, NotFound, BadRequest, InternalServerError>> GetSharedBuildplateInstance(string sharedBuildplateId, CancellationToken cancellationToken)
    {
        string? playerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(playerId))
        {
            return TypedResults.BadRequest();
        }

        // TODO: coordinates etc.

        SharedBuildplateInstanceRequest sharedBuildplateInstanceRequest = (await Request.Body.AsJsonAsync<SharedBuildplateInstanceRequest>(cancellationToken))!;

        return await GetNewSharedBuildplateInstanceResponse(playerId, sharedBuildplateId, sharedBuildplateInstanceRequest.FullSize ? BuildplateInstancesManager.InstanceType.SHARED_PLAY : BuildplateInstancesManager.InstanceType.SHARED_BUILD, cancellationToken);
    }

    [HttpPost("multiplayer/join/instances")]
    public async Task<Results<ContentHttpResult, NotFound, BadRequest, InternalServerError>> JoinInstance(CancellationToken cancellationToken)
    {
        string? playerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(playerId))
        {
            return TypedResults.BadRequest();
        }

        // Rate limit join attempts per player. Returns the same result as an
        // unknown join code so a brute forcer cannot tell throttling apart
        // from a wrong guess.
        if (!RateLimiter.IsAllowed($"join_instance:{playerId}", 10, TimeSpan.FromMinutes(1)))
        {
            Log.Warning($"Player {playerId} hit the join instance rate limit");
            return TypedResults.NotFound();
        }

        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        string body = await reader.ReadToEndAsync(cancellationToken);
        Log.Debug($"Join request from {playerId}: {body}");

        string? joinCode = null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            // the client sends the scanned code as "id"
            if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && (document.RootElement.TryGetProperty("id", out var codeElement)
                    || document.RootElement.TryGetProperty("playerJoinCode", out codeElement))
                && codeElement.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                joinCode = codeElement.GetString();
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return TypedResults.BadRequest();
        }

        if (string.IsNullOrWhiteSpace(joinCode))
        {
            return TypedResults.BadRequest();
        }

        BuildplateInstancesManager.InstanceInfo? instanceInfo = buildplateInstancesManager.GetInstanceInfoByJoinCode(joinCode);
        if (instanceInfo is null || instanceInfo.ShuttingDown)
        {
            Log.Information($"Player {playerId} tried to join with unknown code {joinCode}");
            return TypedResults.NotFound();
        }

        var (readyInstanceInfo, _) = await WaitForInstanceReadyAsync(instanceInfo.InstanceId, cancellationToken);
        instanceInfo = readyInstanceInfo;
        if (instanceInfo is null)
        {
            return TypedResults.NotFound();
        }

        if (instanceInfo.PlayerId != playerId && !await buildplateInstancesManager.AddGuestAsync(playerId, instanceInfo.InstanceId))
        {
            Log.Warning($"Could not add player {playerId} as guest to instance {instanceInfo.InstanceId}");
            return TypedResults.NotFound();
        }

        BuildplateInstance? buildplateInstance = await InstanceInfoToApiResponse(instanceInfo, cancellationToken);
        if (buildplateInstance is null)
        {
            return TypedResults.NotFound();
        }

        Log.Information($"Player {playerId} joined instance {instanceInfo.InstanceId} with code {joinCode}");
        return EarthJson(buildplateInstance);
    }

    private sealed record EncounterInstanceRequest(
        string TileId
    );

    private sealed record AdventureScrollRequest(
        Coordinate? Coordinate,
        Coordinate? PlayerCoordinate,
        float? Latitude,
        float? Longitude,
        float? Lat,
        float? Lon,
        string[]? ScrollsToDeactivate = null
    );

    [HttpPost("multiplayer/encounters/{encounterId}/instances")]
    public async Task<Results<ContentHttpResult, NotFound, BadRequest, InternalServerError>> CreateEncounterInstance(string encounterId, CancellationToken cancellationToken)
    {
        string? playerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(playerId))
        {
            return TypedResults.BadRequest();
        }

        var encounterInstanceRequest = await Request.Body.AsJsonAsync<EncounterInstanceRequest>(cancellationToken);

        return encounterInstanceRequest is null
            ? TypedResults.BadRequest()
            : await GetNewEncounterBuildplateInstanceResponse(playerId, encounterId, encounterInstanceRequest.TileId, tappablesManager, cancellationToken);
    }

    [HttpPost("multiplayer/adventures/{adventureId}/instances")]
    [HttpPost("multiplayer/player/adventures/{adventureId}/instances")]
    public async Task<Results<ContentHttpResult, NotFound, BadRequest, InternalServerError>> CreateAdventureInstance(string adventureId, CancellationToken cancellationToken)
    {
        string? playerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(playerId))
        {
            return TypedResults.BadRequest();
        }

        var adventureInstanceRequest = await Request.Body.AsJsonAsync<EncounterInstanceRequest>(cancellationToken);

        return await GetNewAdventureBuildplateInstanceResponse(playerId, adventureId, adventureInstanceRequest?.TileId, tappablesManager, cancellationToken);
    }

    [HttpPost("adventures/scrolls/{itemId}")]
    public async Task<Results<ContentHttpResult, NotFound, BadRequest, InternalServerError>> RedeemAdventureScroll(string itemId, CancellationToken cancellationToken)
    {
        string? playerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(playerId))
        {
            return TypedResults.BadRequest();
        }

        AdventureScrollRequest? request = await Request.Body.AsJsonAsync<AdventureScrollRequest>(cancellationToken);
        if (request is null || !TryGetCoordinate(request, out float lat, out float lon))
        {
            return TypedResults.BadRequest();
        }

        long requestStartedOn = HttpContext.GetTimestamp();
        string[] scrollsToDeactivate = request.ScrollsToDeactivate ?? [];
        if (scrollsToDeactivate.Length > 0)
        {
            Log.Information("Player {PlayerId} replaces adventures {AdventureIds}", playerId, scrollsToDeactivate);
        }

        await AdventureScrollLock.WaitAsync(cancellationToken);
        try
        {
            TappablesManager.Adventure? recentAtLocation = tappablesManager.GetRecentPlayerAdventureAtLocation(playerId, lat, lon, requestStartedOn, scrollsToDeactivate);
            if (recentAtLocation is not null)
            {
                return EarthJson(AdventureToActiveLocation(recentAtLocation));
            }

            Catalog.ItemsCatalogR.Item? catalogItem;
            string? instanceId = null;
            DB.Models.Player.Inventory inventory;
            try
            {
                EarthDB.Results readResults = await new EarthDB.Query(false)
                    .Get("inventory", playerId, typeof(DB.Models.Player.Inventory))
                    .ExecuteAsync(earthDB, cancellationToken);
                inventory = readResults.Get<DB.Models.Player.Inventory>("inventory");
            }
            catch (EarthDB.DatabaseException exception)
            {
                throw new ServerErrorException(exception);
            }

            catalogItem = catalog.ItemsCatalog.GetItem(itemId);
            if (catalogItem is null)
            {
                (catalogItem, instanceId) = ResolveNonStackableByInstanceId(inventory, itemId);
            }

            if (catalogItem is null || catalogItem.Type is not Catalog.ItemsCatalogR.Item.TypeE.ADVENTURE_SCROLL)
            {
                return TypedResults.NotFound();
            }

            string? templateId = Program.staticData.AdventuresConfig.TryPickTemplateForCrystalItem(catalogItem.Name, Random.Shared);
            if (templateId is null)
            {
                return TypedResults.NotFound();
            }

            TappablesManager.Adventure? recentAdventure = tappablesManager.GetRecentPlayerAdventure(playerId, lat, lon, templateId, requestStartedOn, scrollsToDeactivate);
            if (recentAdventure is not null)
            {
                return EarthJson(AdventureToActiveLocation(recentAdventure));
            }

            DB.Models.Player.Inventory updatedInventory = inventory.Copy();
            bool consumed = instanceId is null
                ? updatedInventory.TakeItems(catalogItem.Id, 1)
                : updatedInventory.TakeItems(catalogItem.Id, [instanceId]) is not null;
            if (!consumed)
            {
                return TypedResults.BadRequest();
            }

            try
            {
                await new EarthDB.Query(true)
                    .Update("inventory", playerId, updatedInventory)
                    .ExecuteAsync(earthDB, cancellationToken);
            }
            catch (EarthDB.DatabaseException exception)
            {
                throw new ServerErrorException(exception);
            }

            string rarity = catalogItem.Rarity.ToString();

            // the new crystal replaces the player's current adventure(s), remember which ones before PlacePlayerAdventure drops the recent one
            string[] previousAdventureIds = [.. tappablesManager.GetAllPlayerAdventures(playerId).Select(previous => previous.Id), .. scrollsToDeactivate];

            TappablesManager.Adventure adventure = tappablesManager.PlacePlayerAdventure(
                playerId,
                lat,
                lon,
                requestStartedOn,
                60 * 60 * 1000,
                AdventureMapIcons.ToClientMapIcon(catalogItem.Name, rarity),
                Enum.Parse<TappablesManager.Adventure.RarityE>(rarity),
                templateId,
                scrollsToDeactivate);

            // End the previous adventure's running instance too, otherwise it lingers until its hour is up
            // and can even be handed back for the new adventure when both crystals roll the same template.
            await buildplateInstancesManager.StopAdventureInstancesAsync(previousAdventureIds.Where(id => id != adventure.Id));

            PrewarmAdventureInstance(playerId, adventure);

            return EarthJson(AdventureToActiveLocation(adventure));
        }
        finally
        {
            AdventureScrollLock.Release();
        }
    }

    private static void PrewarmAdventureInstance(string playerId, TappablesManager.Adventure adventure)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await buildplateInstancesManager.RequestBuildplateInstance(
                    playerId,
                    adventure.Id,
                    adventure.AdventureBuildplateId,
                    BuildplateInstancesManager.InstanceType.PLAYER_ADVENTURE,
                    adventure.SpawnTime + adventure.ValidFor,
                    false,
                    reuseExisting: false);
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "Could not prewarm adventure instance {AdventureId}", adventure.Id);
            }
        });
    }

    [HttpGet("adventures/scrolls")]
    public Results<ContentHttpResult, BadRequest> GetActiveAdventureScrolls()
    {
        string? playerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(playerId))
        {
            return TypedResults.BadRequest();
        }

        long requestStartedOn = HttpContext.GetTimestamp();
        ActiveLocation[] activeLocations = [.. tappablesManager.GetAllPlayerAdventures(playerId)
            .Where(adventure => adventure.SpawnTime + adventure.ValidFor > requestStartedOn)
            .OrderBy(adventure => adventure.SpawnTime)
            .Select(AdventureToActiveLocation)];

        return EarthJson(activeLocations);
    }

    [HttpGet("multiplayer/partitions/{partitionId}/instances/{instanceId}")]
#pragma warning disable IDE0060 // Remove unused parameter
    public async Task<Results<ContentHttpResult, BadRequest, NotFound>> GetInstanceStatus(string partitionId, string instanceId, CancellationToken cancellationToken)
#pragma warning restore IDE0060 // Remove unused parameter
    {
        string? playerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(playerId))
        {
            return TypedResults.BadRequest();
        }

        BuildplateInstancesManager.InstanceInfo? instanceInfo = buildplateInstancesManager.GetInstanceInfo(instanceId);
        if (instanceInfo is null || instanceInfo.ShuttingDown)
        {
            return TypedResults.NotFound();
        }

        // Only the host and guests who joined with a join code may see the
        // instance status (which contains the join code and server address).
        // Ownerless instances (encounters) are world content and stay open.
        if (!buildplateInstancesManager.IsParticipant(playerId, instanceId))
        {
            Log.Information($"Player {playerId} was denied status access to instance {instanceId}");
            return TypedResults.NotFound();
        }

        if (instanceInfo.Type is BuildplateInstancesManager.InstanceType.BUILD or BuildplateInstancesManager.InstanceType.PLAY)
        {
            Buildplates.Buildplate? buildplate;
            try
            {
                EarthDB.Results results = await new EarthDB.Query(false)
                        .Get("buildplates", instanceInfo.PlayerId ?? playerId, typeof(Buildplates))
                        .ExecuteAsync(earthDB, cancellationToken);
                buildplate = results.Get<Buildplates>("buildplates").GetBuildplate(instanceInfo.BuildplateId);
            }
            catch (EarthDB.DatabaseException ex)
            {
                throw new ServerErrorException(ex);
            }

            if (buildplate is null)
            {
                return TypedResults.NotFound();
            }
        }

        // TODO: the client is supposed to poll until the buildplate server is ready, but instead it just crashes if we tell it that the buildplate server is not ready yet
        // TODO: so instead we just stall the request until it's ready, this is really ugly and eventually we need to figure out why it's crashing and implement this properly
        // TODO: this also relies on the buildplate server starting in less than ~20 seconds as the client will eventually time out the HTTP request and crash anyway
        //BuildplateInstance buildplateInstance = this.instanceInfoToApiResponse(instanceInfo);
        BuildplateInstancesManager.InstanceInfo? instanceInfo1;
        int waitCount = 0;
        do
        {
            instanceInfo1 = buildplateInstancesManager.GetInstanceInfo(instanceId);
            if (instanceInfo1 is null || instanceInfo1.ShuttingDown)
            {
                return TypedResults.NotFound();
            }

            if (!instanceInfo1.Ready)
            {
                await Task.Delay(1000, cancellationToken);

                waitCount++;
            }
        }
        while (!instanceInfo1.Ready && waitCount < 35);
        BuildplateInstance? buildplateInstance = await InstanceInfoToApiResponse(instanceInfo1, cancellationToken);

        if (buildplateInstance is null)
        {
            return TypedResults.NotFound();
        }

        return EarthJson(buildplateInstance);
    }

    private static async Task<Results<ContentHttpResult, InternalServerError, NotFound, BadRequest>> GetNewBuildplateInstanceResponse(string playerId, string buildplateId, BuildplateInstancesManager.InstanceType type, CancellationToken cancellationToken)
    {
        Buildplates.Buildplate? buildplate;
        
        try
        {
            EarthDB.Results results = await new EarthDB.Query(false)
                .Get("buildplates", playerId, typeof(Buildplates))
                .ExecuteAsync(earthDB, cancellationToken);

            buildplate = results.Get<Buildplates>("buildplates").GetBuildplate(buildplateId);
        }
        catch (EarthDB.DatabaseException exception)
        {
            throw new ServerErrorException(exception);
        }

        if (buildplate is null)
        {
            return TypedResults.NotFound();
        }

        string? instanceId = await buildplateInstancesManager.RequestBuildplateInstance(playerId, null, buildplateId, type, 0, buildplate.Night);
        if (instanceId is null)
        {
            return TypedResults.InternalServerError();
        }

        (BuildplateInstancesManager.InstanceInfo? instanceInfo, string? stopReason) = await WaitForInstanceReadyAsync(instanceId, cancellationToken);
        if (instanceInfo is null)
        {
            Log.Warning("Buildplate instance {InstanceId} stopped before ready: {Reason}", instanceId, stopReason);
            return InstanceErrorJson(stopReason);
        }

        BuildplateInstance? buildplateInstance = await InstanceInfoToApiResponse(instanceInfo, cancellationToken);

        if (buildplateInstance is null)
        {
            return TypedResults.NotFound();
        }

        return EarthJson(buildplateInstance);
    }

    private static async Task<Results<ContentHttpResult, NotFound, BadRequest, InternalServerError>> GetNewSharedBuildplateInstanceResponse(string playerId, string sharedBuildplateId, BuildplateInstancesManager.InstanceType type, CancellationToken cancellationToken)
    {
        SharedBuildplates.SharedBuildplate? sharedBuildplate;
        try
        {
            EarthDB.Results results = await new EarthDB.Query(false)
                .Get("sharedBuildplates", "", typeof(SharedBuildplates))
                .ExecuteAsync(earthDB, cancellationToken);
            sharedBuildplate = results.Get<SharedBuildplates>("sharedBuildplates").GetSharedBuildplate(sharedBuildplateId);
        }
        catch (EarthDB.DatabaseException exception)
        {
            throw new ServerErrorException(exception);
        }

        if (sharedBuildplate is null)
        {
            return TypedResults.NotFound();
        }

        string? instanceId = await buildplateInstancesManager.RequestBuildplateInstance(playerId, null, sharedBuildplateId, type, 0, sharedBuildplate.Night);
        if (instanceId is null)
        {
            return TypedResults.InternalServerError();
        }

        var (instanceInfo, _) = await WaitForInstanceReadyAsync(instanceId, cancellationToken);
        if (instanceInfo is null)
        {
            return TypedResults.InternalServerError();
        }

        BuildplateInstance? buildplateInstance = await InstanceInfoToApiResponse(instanceInfo, cancellationToken);
        if (buildplateInstance is null)
        {
            return TypedResults.InternalServerError();
        }

        return EarthJson(buildplateInstance);
    }

    private static async Task<Results<ContentHttpResult, NotFound, BadRequest, InternalServerError>> GetNewEncounterBuildplateInstanceResponse(string playerId, string encounterId, string tileId, TappablesManager tappablesManager, CancellationToken cancellationToken)
    {
        TappablesManager.Encounter? encounter = tappablesManager.GetEncounterWithId(encounterId, tileId);
        if (encounter is null)
        {
            return TypedResults.NotFound();
        }

        Log.Information("Trading post tapped: encounter {EncounterId} uses buildplate {BuildplateId}", encounterId, encounter.EncounterBuildplateId);

        // Start the instance for the tapping player: the launcher's playerLogin lookup only
        // matches instances owned by the connecting player, so an ownerless instance would
        // get the player kicked (and routed into a different own instance when one exists).
        string? instanceId = await buildplateInstancesManager.RequestBuildplateInstance(
            playerId,
            encounterId,
            encounter.EncounterBuildplateId,
            BuildplateInstancesManager.InstanceType.ENCOUNTER,
            long.Max(encounter.SpawnTime + encounter.ValidFor, U.CurrentTimeMillis() + MinEncounterInstanceLifetimeMs),
            false);

        if (instanceId is null)
        {
            return TypedResults.InternalServerError();
        }

        var (instanceInfo, _) = await WaitForInstanceReadyAsync(instanceId, cancellationToken);
        if (instanceInfo is null)
        {
            return TypedResults.InternalServerError();
        }

        BuildplateInstance? buildplateInstance = await InstanceInfoToApiResponse(instanceInfo, cancellationToken);
        if (buildplateInstance is null)
        {
            return TypedResults.InternalServerError();
        }

        return EarthJson(buildplateInstance);
    }

    private static async Task<Results<ContentHttpResult, NotFound, BadRequest, InternalServerError>> GetNewAdventureBuildplateInstanceResponse(string playerId, string adventureId, string? tileId, TappablesManager tappablesManager, CancellationToken cancellationToken)
    {
        TappablesManager.Adventure? adventure = tappablesManager.GetPlayerAdventureWithId(playerId, adventureId, tileId);
        if (adventure is null)
        {
            return TypedResults.NotFound();
        }

        string? instanceId = await buildplateInstancesManager.RequestBuildplateInstance(playerId, adventureId, adventure.AdventureBuildplateId, BuildplateInstancesManager.InstanceType.PLAYER_ADVENTURE, adventure.SpawnTime + adventure.ValidFor, false);

        if (instanceId is null)
        {
            return TypedResults.InternalServerError();
        }

        var (instanceInfo, _) = await WaitForInstanceReadyAsync(instanceId, cancellationToken);
        if (instanceInfo is null)
        {
            return TypedResults.InternalServerError();
        }

        BuildplateInstance? buildplateInstance = await InstanceInfoToApiResponse(instanceInfo, cancellationToken);
        if (buildplateInstance is null)
        {
            return TypedResults.InternalServerError();
        }

        return EarthJson(buildplateInstance);
    }

    private static async Task<(BuildplateInstancesManager.InstanceInfo? InstanceInfo, string? StopReason)> WaitForInstanceReadyAsync(string instanceId, CancellationToken cancellationToken)
    {
        BuildplateInstancesManager.InstanceInfo? instanceInfo;
        int waitCount = 0;
        bool loggedJoinWait = false;
        do
        {
            instanceInfo = buildplateInstancesManager.GetInstanceInfo(instanceId);
            if (instanceInfo is null || instanceInfo.ShuttingDown)
            {
                return (null, buildplateInstancesManager.ConsumeStopReason(instanceId) ?? "instance stopped before becoming ready");
            }

            if (!instanceInfo.Ready)
            {
                // WHY: guests dial the bridge at this address over RakNet/UDP; if it is
                // not reachable from their network the client just shows "connection
                // timed out", so make the expected address visible once per join wait.
                if (!loggedJoinWait)
                {
                    loggedJoinWait = true;
                    Log.Information($"Instance join wait started — bridge must be reachable at {instanceInfo.Address}:{instanceInfo.Port} (guests dial this)");
                }

                await Task.Delay(1000, cancellationToken);
                waitCount++;
            }
        }
        while (!instanceInfo.Ready && waitCount < WaitForReadyMaxSeconds);

        return instanceInfo.Ready
            ? (instanceInfo, null)
            : (null, $"Instance did not become ready within {WaitForReadyMaxSeconds} seconds");
    }

    // 500 with the failure reason in the body: bare InternalServerError responses
    // carry no information, so the launcher-reported stop reason travels as
    // {"error": "..."} using the same JSON conventions as EarthJson (which has no
    // error field of its own).
    private static ContentHttpResult InstanceErrorJson(string? error)
        => TypedResults.Content(Common.Json.Serialize(new { error }), "application/json", Encoding.UTF8, StatusCodes.Status500InternalServerError);

    private static bool TryGetCoordinate(AdventureScrollRequest request, out float lat, out float lon)
    {
        Coordinate? coordinate = request.Coordinate ?? request.PlayerCoordinate;
        if (coordinate is not null)
        {
            lat = coordinate.Latitude;
            lon = coordinate.Longitude;
            return true;
        }

        lat = request.Latitude ?? request.Lat ?? 0;
        lon = request.Longitude ?? request.Lon ?? 0;
        return (request.Latitude is not null || request.Lat is not null) && (request.Longitude is not null || request.Lon is not null);
    }

    private static (Catalog.ItemsCatalogR.Item? Item, string? InstanceId) ResolveNonStackableByInstanceId(DB.Models.Player.Inventory inventory, string instanceId)
    {
        foreach (DB.Models.Player.Inventory.NonStackableItem item in inventory.NonStackableItems)
        {
            if (item.Instances.Any(instance => instance.InstanceId == instanceId))
            {
                return (catalog.ItemsCatalog.GetItem(item.Id), instanceId);
            }
        }

        return (null, null);
    }

    private static ActiveLocation AdventureToActiveLocation(TappablesManager.Adventure adventure)
        => new(
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
                ""));

    [JsonConverter(typeof(JsonStringEnumConverter))]
    private enum Source
    {
        PLAYER,
        SHARED,
        ENCOUNTER
    }

    private static async Task<BuildplateInstance?> InstanceInfoToApiResponse(BuildplateInstancesManager.InstanceInfo instanceInfo, CancellationToken cancellationToken)
    {
        var (fullsize, gameplayMode, source) = instanceInfo.Type switch
        {
            BuildplateInstancesManager.InstanceType.BUILD => (false, BuildplateInstance.GameplayMetadataR.GameplayModeE.BUILDPLATE, Source.PLAYER),
            BuildplateInstancesManager.InstanceType.PLAY => (true, BuildplateInstance.GameplayMetadataR.GameplayModeE.BUILDPLATE_PLAY, Source.PLAYER),
            BuildplateInstancesManager.InstanceType.SHARED_BUILD => (true, BuildplateInstance.GameplayMetadataR.GameplayModeE.SHARED_BUILDPLATE_PLAY, Source.SHARED),
            BuildplateInstancesManager.InstanceType.SHARED_PLAY => (true, BuildplateInstance.GameplayMetadataR.GameplayModeE.SHARED_BUILDPLATE_PLAY, Source.SHARED),
            BuildplateInstancesManager.InstanceType.ENCOUNTER => (true, BuildplateInstance.GameplayMetadataR.GameplayModeE.ENCOUNTER, Source.ENCOUNTER),
            BuildplateInstancesManager.InstanceType.PLAYER_ADVENTURE => (true, BuildplateInstance.GameplayMetadataR.GameplayModeE.ENCOUNTER, Source.ENCOUNTER),
            _ => throw new UnreachableException(),
        };

        int size;
        int offset;
        int scale;
        switch (source)
        {
            case Source.PLAYER:
                {
                    Debug.Assert(instanceInfo.PlayerId is not null);

                    Buildplates.Buildplate? buildplate;
                    try
                    {
                        EarthDB.Results results = await new EarthDB.Query(false)
                            .Get("buildplates", instanceInfo.PlayerId, typeof(Buildplates))
                            .ExecuteAsync(earthDB, cancellationToken);
                        buildplate = results.Get<Buildplates>("buildplates").GetBuildplate(instanceInfo.BuildplateId);
                    }
                    catch (EarthDB.DatabaseException exception)
                    {
                        throw new ServerErrorException(exception);
                    }

                    if (buildplate is null)
                    {
                        return null;
                    }

                    size = buildplate.Size;
                    offset = buildplate.Offset;
                    scale = buildplate.Scale;
                }

                break;
            case Source.SHARED:
                {
                    SharedBuildplates.SharedBuildplate? sharedBuildplate;
                    try
                    {
                        EarthDB.Results results = await new EarthDB.Query(false)
                            .Get("sharedBuildplates", "", typeof(SharedBuildplates))
                            .ExecuteAsync(earthDB, cancellationToken);
                        sharedBuildplate = results.Get<SharedBuildplates>("sharedBuildplates").GetSharedBuildplate(instanceInfo.BuildplateId);
                    }
                    catch (EarthDB.DatabaseException exception)
                    {
                        throw new ServerErrorException(exception);
                    }

                    if (sharedBuildplate is null)
                    {
                        return null;
                    }

                    size = sharedBuildplate.Size;
                    offset = sharedBuildplate.Offset;
                    scale = sharedBuildplate.Scale;
                }

                break;
            case Source.ENCOUNTER:
                {
                    BuildplateGeometry? geometry = await GetEncounterBuildplateGeometry(instanceInfo.BuildplateId, cancellationToken);
                    if (geometry is null)
                    {
                        return null;
                    }

                    size = geometry.Size;
                    offset = geometry.Offset;
                    scale = geometry.Scale;
                }

                break;
            default:
                throw new UnreachableException();
        }

        // WHY: the client dials IpV4Address:Port directly (RakNet/UDP) and ignores the
        // fqdn, but reporting the hardcoded Vienna-era placeholder "67e.duckdns.org"
        // made the API lie about where the system is reachable. The launcher reports
        // its --publicAddress (the launcher settings' IPv4) as instanceInfo.Address,
        // so mirror that here; keep the legacy string only if no address was reported.
        return new BuildplateInstance(
            instanceInfo.InstanceId,
            "00000000-0000-0000-0000-000000000000",
            instanceInfo.Address is { Length: > 0 } ? instanceInfo.Address : "67e.duckdns.org",
            instanceInfo.Address,
            instanceInfo.Port,
            instanceInfo.Ready,
            instanceInfo.Ready ? BuildplateInstance.ApplicationStatusE.READY : BuildplateInstance.ApplicationStatusE.UNKNOWN,
            instanceInfo.Ready ? BuildplateInstance.ServerStatusE.RUNNING : BuildplateInstance.ServerStatusE.RUNNING,
            Common.Json.Serialize(new Dictionary<string, object>()
            {
                { "buildplateid", instanceInfo.BuildplateId }
            }),
            new BuildplateInstance.GameplayMetadataR(
                instanceInfo.BuildplateId,
                "00000000-0000-0000-0000-000000000000",
                instanceInfo.PlayerId,
                "2020.1217.02",
                buildplateInstancesManager.GetJoinCode(instanceInfo.InstanceId),
                InstanceDimension(instanceInfo.Type, instanceInfo.BuildplateId, size, adventureFootprints.Value),
                InstanceOffset(instanceInfo.Type, instanceInfo.BuildplateId, offset, adventureFootprints.Value),
                instanceInfo.Type is BuildplateInstancesManager.InstanceType.PLAY
                    ? Program.PlayableScale
                    : !fullsize ? scale : 1,
                fullsize,
                gameplayMode,
                SurfaceOrientation.HORIZONTAL,
                null,
                GetAdventureRarity(instanceInfo, Program.staticData),
                []
            ),
            "776932eeeb69",
            //new Coordinate(50.99636722700025f, -0.7234904312500047f)
            new Coordinate(0.0f, 0.0f)    // TODO
        );
    }

    internal static Rarity? GetAdventureRarity(BuildplateInstancesManager.InstanceInfo instanceInfo, StaticData.StaticData staticData)
        => instanceInfo.Type is BuildplateInstancesManager.InstanceType.ENCOUNTER or BuildplateInstancesManager.InstanceType.PLAYER_ADVENTURE
            && staticData.AdventuresConfig.GetBuildplateFolder(instanceInfo.BuildplateId) is { } folder
            ? AdventureFolderToRarity(folder)
            : null;

    internal static Rarity? AdventureFolderToRarity(string folder)
        => Enum.GetNames<Rarity>().Contains(folder, StringComparer.OrdinalIgnoreCase)
            ? Enum.Parse<Rarity>(folder, true)
            : null;

    internal static Dimension InstanceDimension(BuildplateInstancesManager.InstanceType type, string buildplateId, int size, AdventureFootprints? footprints)
    {
        if (type is BuildplateInstancesManager.InstanceType.ENCOUNTER or BuildplateInstancesManager.InstanceType.PLAYER_ADVENTURE
            && footprints?.GetFootprint(buildplateId) is { } footprint)
        {
            return new Dimension(footprint.X, footprint.Z);
        }

        return new Dimension(size, size);
    }

    internal static Offset InstanceOffset(BuildplateInstancesManager.InstanceType type, string buildplateId, int offset, AdventureFootprints? footprints)
    {
        if (type is BuildplateInstancesManager.InstanceType.ENCOUNTER or BuildplateInstancesManager.InstanceType.PLAYER_ADVENTURE
            && footprints?.GetFootprint(buildplateId)?.Offset is { } footprintOffset)
        {
            return new Offset(footprintOffset.X, offset + footprintOffset.Y, footprintOffset.Z);
        }

        return new Offset(0, offset, 0);
    }

    internal static AdventureFootprints? LoadAdventureFootprints(Apace.StaticData.StaticData staticData)
    {
        try
        {
            AdventureFootprints footprints = staticData.AdventureFootprints;
            Log.Information($"Loaded {footprints.Count} adventure footprints");
            return footprints;
        }
        catch (StaticDataException exception) when (exception.InnerException is FileNotFoundException or DirectoryNotFoundException)
        {
            Log.Information($"No adventures/{AdventureFootprints.FileName} in static data, adventures use their template size");
            return null;
        }
        catch (Exception exception)
        {
            Log.Error($"Could not load adventures/{AdventureFootprints.FileName}, adventures use their template size: {exception}");
            return null;
        }
    }

    private sealed record BuildplateGeometry(int Size, int Offset, int Scale);

    private static async Task<BuildplateGeometry?> GetEncounterBuildplateGeometry(string buildplateId, CancellationToken cancellationToken)
    {
        try
        {
            EarthDB.Results results = await new EarthDB.Query(false)
                .Get("encounterBuildplates", "", typeof(EncounterBuildplates))
                .ExecuteAsync(earthDB, cancellationToken);

            EncounterBuildplates.EncounterBuildplate? encounterBuildplate = results.Get<EncounterBuildplates>("encounterBuildplates").GetEncounterBuildplate(buildplateId);
            if (encounterBuildplate is not null)
            {
                return new BuildplateGeometry(encounterBuildplate.Size, encounterBuildplate.Offset, encounterBuildplate.Scale);
            }

            EarthDB.ObjectResults objectResults = await new EarthDB.ObjectQuery(false)
                .GetBuildplate(buildplateId)
                .ExecuteAsync(earthDB, cancellationToken);
            TemplateBuildplate? templateBuildplate = objectResults.GetBuildplate(buildplateId);
            return templateBuildplate is null
                ? null
                : new BuildplateGeometry(templateBuildplate.Size, templateBuildplate.Offset, templateBuildplate.Scale);
        }
        catch (EarthDB.DatabaseException exception)
        {
            throw new ServerErrorException(exception);
        }
    }

    private sealed record SharedBuildplateInstanceRequest(
        bool FullSize
    );
}
