using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Serialization;
using Serilog;
using Solace.Buildplate.Connector.Model;
using Solace.Common;
using Solace.Common.Utils;
using Solace.EventBus.Client;

namespace Solace.Buildplate.Launcher;

#pragma warning disable CA1001 // Types that own disposable fields should be disposable
public sealed class Instance
#pragma warning restore CA1001 // Types that own disposable fields should be disposable
{
    private const long HOST_PLAYER_CONNECT_TIMEOUT = 120_000;

    public static Instance Run(EventBusClient eventBusClient, string? playerId, string buildplateId, BuildplateSource buildplateSource, string instanceId, bool survival, bool night, bool saveEnabled, InventoryType inventoryType, long? shutdownTime, DirectoryInfo baseDir, string eventBusConnectionString)
    {
        if (playerId is null && buildplateSource is BuildplateSource.PLAYER)
        {
            throw new ArgumentException($"{nameof(playerId)} cannot be null when {nameof(buildplateSource)} is {nameof(BuildplateSource.PLAYER)}");
        }

        var instance = new Instance(eventBusClient, playerId, buildplateId, buildplateSource, instanceId, survival, night, saveEnabled, inventoryType, shutdownTime, baseDir, eventBusConnectionString);
        instance._threadStartedSemaphore.Wait();
        instance._thread = instance.RunAsync();
        instance._threadStartedSemaphore.Wait();
        instance._threadStartedSemaphore.Release();
        return instance;
    }

    private readonly EventBusClient _eventBusClient;

    private readonly string? _playerId;
    private readonly string _buildplateId;
    private readonly BuildplateSource _buildplateSource;
    public readonly string InstanceId;
    public string? PlayerId => _playerId;
    private readonly bool _survival;
    private readonly bool _night;
    private readonly bool _saveEnabled;
    private readonly InventoryType _inventoryType;
    private readonly long? _shutdownTime;

    private readonly DirectoryInfo _baseDir;
    private readonly string _eventBusAddress;
    private readonly string _eventBusQueueName;

    private Task? _thread;
    private readonly SemaphoreSlim _threadStartedSemaphore = new SemaphoreSlim(1, 1);
    private readonly ILogger _logger;

    private Publisher? _publisher;
    private RequestSender? _requestSender;

    private Subscriber? _subscriber;
    private RequestHandler? _requestHandler;

    private string? _dimensionKey;
    public string? DimensionKey => _dimensionKey;
    private readonly TaskCompletionSource _shutdownTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _shutdownLock = new Lock();
    private volatile bool _shuttingDown;

    private volatile bool _hostPlayerConnected;

    private Instance(EventBusClient eventBusClient, string? playerId, string buildplateId, BuildplateSource buildplateSource, string instanceId, bool survival, bool night, bool saveEnabled, InventoryType inventoryType, long? shutdownTime, DirectoryInfo baseDir, string eventBusConnectionString)
    {
        _eventBusClient = eventBusClient;

        _playerId = playerId;
        _buildplateId = buildplateId;
        _buildplateSource = buildplateSource;
        InstanceId = instanceId;
        _survival = survival;
        _night = night;
        _saveEnabled = saveEnabled;
        _inventoryType = inventoryType;
        _shutdownTime = shutdownTime;

        _baseDir = baseDir;
        _eventBusAddress = eventBusConnectionString;
        _eventBusQueueName = "buildplate_" + InstanceId;

        _logger = Log.Logger.ForContext("InstanceId", InstanceId);
    }

    private async Task RunAsync()
    {
        await Task.Yield();

        _threadStartedSemaphore.Release();

        try
        {
            switch (_buildplateSource)
            {
                case BuildplateSource.PLAYER:
                    _logger.Information($"Starting for player {_playerId} buildplate {_buildplateId} (survival = {_survival}, saveEnabled = {_saveEnabled}, inventoryType = {_inventoryType})");
                    break;
                case BuildplateSource.SHARED:
                    _logger.Information($"Starting for shared buildplate {_buildplateId} (player = {_playerId}, survival = {_survival}, saveEnabled = {_saveEnabled}, inventoryType = {_inventoryType})");
                    break;
                case BuildplateSource.ENCOUNTER:
                    _logger.Information($"Starting for encounter buildplate {_buildplateId} (player = {_playerId}, survival = {_survival}, saveEnabled = {_saveEnabled}, inventoryType = {_inventoryType})");
                    break;
            }

            _logger.Information($"Listening on event bus queue {_eventBusQueueName} at {_eventBusAddress}");

            _publisher = await _eventBusClient.AddPublisherAsync();
            _requestSender = await _eventBusClient.AddRequestSenderAsync();

            _logger.Information("Loading buildplate data");

            _logger.Information("Sending buildplate load request to event bus");

            BuildplateLoadResponse? buildplateLoadResponse = _buildplateSource switch
            {
                BuildplateSource.PLAYER => await SendEventBusRequestRaw<BuildplateLoadResponse>("load", new BuildplateLoadRequest(_playerId!, _buildplateId), true),
                BuildplateSource.SHARED => await SendEventBusRequestRaw<BuildplateLoadResponse>("loadShared", new SharedBuildplateLoadRequest(_buildplateId), true),
                BuildplateSource.ENCOUNTER => await SendEventBusRequestRaw<BuildplateLoadResponse>("loadEncounter", new EncounterBuildplateLoadRequest(_buildplateId), true),
                _ => throw new UnreachableException(),
            };

            _logger.Information("Buildplate load response received: {Result}", buildplateLoadResponse is null ? "null" : "ok");

            Debug.Assert(buildplateLoadResponse is not null);

            byte[] serverData;
            try
            {
                serverData = Convert.FromBase64String(buildplateLoadResponse.ServerDataBase64);
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Buildplate load response contained invalid base64 data");
                return;
            }

            try
            {
                var worldDataDir = await SetupServerFiles(serverData);
                if (worldDataDir is null)
                {
                    _logger.Error("Could not set up files for server");
                    return;
                }
            }
            catch (IOException exception)
            {
                _logger.Error(exception, "Could not set up files for server");
                return;
            }

            _logger.Information("Registering event bus listeners");

            _subscriber = await _eventBusClient.AddSubscriberAsync(_eventBusQueueName, new SubscriberListener(
                HandleConnectorEvent,
                async () =>
                {
                    _logger.Error("Event bus subscriber error");
                    BeginShutdown();
                }
            ));

            _requestHandler = await _eventBusClient.AddRequestHandlerAsync(_eventBusQueueName, new RequestHandlerLister(
                async request =>
                {
                    object? responseObject = await HandleConnectorRequest(request);
                    return responseObject is not null ? Json.Serialize(responseObject) : null;
                },
                async () =>
                {
                    _logger.Error("Event bus request handler error");
                    BeginShutdown();
                }
            ));

            _logger.Information("Creating instance {InstanceId} on persistent Fabric server", InstanceId);

            CreateInstanceResponse? createInstanceResponse = await SendCreateInstanceRequestAsync(new CreateInstanceRequest(
                InstanceId,
                "fountain:wrapper",
                BuildGeneratorSettings(buildplateLoadResponse.ServerDataBase64)
            ));

            if (createInstanceResponse is null)
            {
                _logger.Error("Could not create instance {InstanceId} on persistent Fabric server", InstanceId);
                return;
            }

            if (!createInstanceResponse.Success)
            {
                _logger.Error("Failed to create instance {InstanceId} on persistent Fabric server: {Error}", InstanceId, createInstanceResponse.Error);
                return;
            }

            _dimensionKey = createInstanceResponse.DimensionKey;
            _logger.Information("Instance {InstanceId} is ready as dimension {DimensionKey}", InstanceId, _dimensionKey);

            SendEventBusInstanceStatusNotification("ready");

            if (_shutdownTime is not null)
            {
                StartShutdownTimer();
            }
            else
            {
                StartHostPlayerConnectTimeout();
            }

            await _shutdownTcs.Task;
        }
        catch (Exception exception)
        {
            _logger.Error(exception, $"Unhandled exception: {exception.Message}");
        }
        finally
        {
            if (_subscriber is not null)
            {
                await _subscriber.CloseAsync();
            }

            if (_requestHandler is not null)
            {
                await _requestHandler.CloseAsync();
            }

            if (_publisher is not null)
            {
                await _publisher.FlushAsync();
                await _publisher.CloseAsync();
            }

            if (_requestSender is not null)
            {
                await _requestSender.FlushAsync();
                await _requestSender.CloseAsync();
            }

            CleanupBaseDir();

            _logger.Information("Finished");
        }
    }

    private async Task HandleConnectorEvent(SubscriberEvent @event)
    {
        switch (@event.Type)
        {
            case "saved":
                {
                    if (_saveEnabled)
                    {
                        WorldSavedMessage? worldSavedMessage = ReadJson<WorldSavedMessage>(@event.Data);
                        if (worldSavedMessage is not null)
                        {
                            if (_hostPlayerConnected)
                            {
                                _logger.Information("Saving snapshot");
                                SendEventBusRequest<object>("saved", worldSavedMessage, false)
                                    .Forget();
                            }
                            else
                            {
                                _logger.Information("Not saving snapshot because host player never connected");
                            }
                        }
                    }
                    else
                    {
                        _logger.Information("Ignoring save data because saving is disabled");
                    }
                }

                break;
            case "inventoryAdd":
                {
                    InventoryAddItemMessage? inventoryAddItemMessage = ReadJson<InventoryAddItemMessage>(@event.Data);
                    if (inventoryAddItemMessage is not null)
                    {
                        SendEventBusRequest<object>("inventoryAdd", inventoryAddItemMessage, false)
                            .Forget();
                    }
                }

                break;
            case "inventoryUpdateWear":
                {
                    InventoryUpdateItemWearMessage? inventoryUpdateItemWearMessage = ReadJson<InventoryUpdateItemWearMessage>(@event.Data);
                    if (inventoryUpdateItemWearMessage is not null)
                    {
                        SendEventBusRequest<object>("inventoryUpdateWear", inventoryUpdateItemWearMessage, false)
                            .Forget();
                    }
                }

                break;

            case "inventorySetHotbar":
                {
                    InventorySetHotbarMessage? inventorySetHotbarMessage = ReadJson<InventorySetHotbarMessage>(@event.Data);
                    if (inventorySetHotbarMessage is not null)
                    {
                        SendEventBusRequest<object>("inventorySetHotbar", inventorySetHotbarMessage, false)
                            .Forget();
                    }
                }

                break;
        }
    }

    private async Task<object?> HandleConnectorRequest(RequestHandlerRequest request)
    {
        switch (request.Type)
        {
            case "playerConnected":
                {
                    PlayerConnectedRequest? playerConnectedRequest = ReadJson<PlayerConnectedRequest>(request.Data);
                    if (playerConnectedRequest is not null)
                    {
                        if (_playerId is not null && !_hostPlayerConnected && playerConnectedRequest.Uuid != _playerId)
                        {
                            _logger.Information($"Rejecting player connection for player {playerConnectedRequest.Uuid} because the host player must connect first");
                            return new PlayerConnectedResponse(false, null);
                        }

                        PlayerConnectedResponse? playerConnectedResponse = await SendEventBusRequest<PlayerConnectedResponse>("playerConnected", playerConnectedRequest, true);
                        if (playerConnectedResponse is not null)
                        {
                            _logger.Information($"Player {playerConnectedRequest.Uuid} has connected");

                            if (_playerId is not null && !_hostPlayerConnected && playerConnectedRequest.Uuid == _playerId)
                            {
                                _hostPlayerConnected = true;
                            }

                            return playerConnectedResponse;
                        }
                        else
                        {
                            Log.Debug("[playerConnected] invalid api response");
                        }
                    }
                    else
                    {
                        Log.Debug("[playerConnected] failed to read json");
                    }
                }

                break;
            case "playerDisconnected":
                {
                    PlayerDisconnectedRequest? playerDisconnectedRequest = ReadJson<PlayerDisconnectedRequest>(request.Data);
                    if (playerDisconnectedRequest is not null)
                    {
                        PlayerDisconnectedResponse? playerDisconnectedResponse = await SendEventBusRequest<PlayerDisconnectedResponse>("playerDisconnected", playerDisconnectedRequest, true);
                        if (playerDisconnectedResponse is not null)
                        {
                            _logger.Information($"Player {playerDisconnectedRequest.PlayerId} has disconnected");

                            if (_shutdownTime is null && _playerId is not null && playerDisconnectedRequest.PlayerId == _playerId)
                            {
                                _logger.Information("Host player has disconnected, beginning shutdown");
                                BeginShutdown();
                            }

                            return playerDisconnectedResponse;
                        }
                    }
                }

                break;
            case "playerDead":
                {
                    string? playerId = ReadJson<string>(request.Data);
                    if (playerId is not null)
                    {
                        bool? respawn = await SendEventBusRequest<bool?>("playerDead", playerId, true);
                        if (respawn is not null)
                        {
                            return respawn.Value;
                        }
                    }
                }

                break;
            case "getInventory":
                {
                    string? playerId = ReadJson<string>(request.Data);
                    if (playerId is not null)
                    {
                        InventoryResponse? inventoryResponse = await SendEventBusRequest<InventoryResponse>("getInventory", playerId, true);
                        if (inventoryResponse is not null)
                        {
                            return inventoryResponse;
                        }
                        else
                        {
                            Log.Debug("[getInventory] invalid api response");

                        }
                    }
                    else
                    {
                        Log.Debug("[getInventory] failed to read json");
                    }
                }

                break;
            case "inventoryRemove":
                {
                    InventoryRemoveItemRequest? inventoryRemoveItemRequest = ReadJson<InventoryRemoveItemRequest>(request.Data);
                    if (inventoryRemoveItemRequest is not null)
                    {
                        if (inventoryRemoveItemRequest.InstanceId is not null)
                        {
                            bool? success = await SendEventBusRequest<bool?>("inventoryRemove", inventoryRemoveItemRequest, true);
                            if (success is not null)
                            {
                                return success.Value;
                            }
                        }
                        else
                        {
                            int? removedCount = await SendEventBusRequest<int?>("inventoryRemove", inventoryRemoveItemRequest, true);
                            if (removedCount is not null)
                            {
                                return removedCount.Value;
                            }
                        }
                    }
                }

                break;
            case "findPlayer":
                {
                    FindPlayerIdRequest? findPlayerIdRequest = ReadJson<FindPlayerIdRequest>(request.Data);
                    if (findPlayerIdRequest is not null)
                    {
                        // TODO
                        return findPlayerIdRequest.MinecraftName;
                    }
                    else
                    {
                        Log.Debug("[findPlayer] failed to read json");
                    }
                }

                break;
            case "getInitialPlayerState":
                {
                    string? playerId = ReadJson<string>(request.Data);
                    if (playerId is not null)
                    {
                        InitialPlayerStateResponse? initialPlayerStateResponse = await SendEventBusRequest<InitialPlayerStateResponse>("getInitialPlayerState", playerId, true);
                        if (initialPlayerStateResponse is not null)
                        {
                            return initialPlayerStateResponse;
                        }
                    }
                    else
                    {
                        Log.Debug("[getInitialPlayerState] failed to read json");
                    }
                }

                break;
        }

        return null;
    }

    private T? ReadJson<T>(string str)
    {
        try
        {
            return Json.Deserialize<T>(str);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to decode event bus message JSON: {ex}");
            BeginShutdown();
            return default;
        }
    }

    private void SendEventBusInstanceStatusNotification(string status)
    {
        Debug.Assert(_publisher is not null);

        _publisher.PublishAsync("buildplates", status, InstanceId).ContinueWith(task =>
        {
            if (!task.Result)
            {
                Log.Error("Event bus publisher error");
                BeginShutdown();
            }
        });
    }

    private sealed record RequestWithInstanceId(
        string InstanceId,
        object Request
    );

    private Task<T?> SendEventBusRequest<T>(string type, object obj, bool returnResponse)
    {
        var request = new RequestWithInstanceId(InstanceId, obj);

        return SendEventBusRequestRaw<T>(type, request, returnResponse);
    }

    private async Task<T?> SendEventBusRequestRaw<T>(string type, object obj, bool returnResponse)
    {
        Debug.Assert(_requestSender is not null);

        try
        {
            string? response = await _requestSender.RequestAsync("buildplates", type, Json.Serialize(obj));

            if (response is null)
            {
                if (!returnResponse)
                {
                    Log.Warning($"Event bus request '{type}' returned no response for fire-and-forget message");
                    return default;
                }

                Log.Error("Event bus request failed (no response)");
                BeginShutdown();
                return default;
            }

            if (returnResponse)
            {
                Debug.Assert(typeof(T) != typeof(object));
                return Json.Deserialize<T>(response);
            }
            else
            {
                Debug.Assert(typeof(T) == typeof(object));
                return default;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Event bus request failed: {ex}");
            BeginShutdown();
            return default;
        }
    }

    private async Task<CreateInstanceResponse?> SendCreateInstanceRequestAsync(CreateInstanceRequest request)
    {
        Debug.Assert(_requestSender is not null);

        try
        {
            string? response = await _requestSender.RequestAsync(PersistentProcessManager.PERSISTENT_QUEUE_NAME, "createInstance", Json.Serialize(request));

            if (response is null)
            {
                Log.Error("Event bus createInstance request failed (no response)");
                BeginShutdown();
                return null;
            }

            return Json.Deserialize<CreateInstanceResponse>(response);
        }
        catch (Exception ex)
        {
            Log.Error($"Event bus createInstance request failed: {ex}");
            BeginShutdown();
            return null;
        }
    }

    private async Task SendDestroyInstanceAsync()
    {
        Debug.Assert(_requestSender is not null);

        try
        {
            string? response = await _requestSender.RequestAsync(PersistentProcessManager.PERSISTENT_QUEUE_NAME, "destroyInstance", Json.Serialize(new DestroyInstanceRequest(InstanceId)));

            if (response is null)
            {
                Log.Warning("Event bus destroyInstance request returned no response");
                return;
            }

            DestroyInstanceResponse? destroyInstanceResponse = Json.Deserialize<DestroyInstanceResponse>(response);
            if (destroyInstanceResponse is { Success: false })
            {
                _logger.Warning("Failed to destroy instance {InstanceId}: {Error}", InstanceId, destroyInstanceResponse.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Event bus destroyInstance request failed: {ex}");
        }
    }

    private GeneratorSettings BuildGeneratorSettings(string worldDataBase64)
        => new GeneratorSettings(
            32,                     // buildplateWidth
            32,                     // buildplateDepth
            63,                     // buildplateGroundLevel
            5,                      // buildplateUndergroundHeight
            _survival ? 0 : 1,      // gameType
            1,                      // difficulty
            _night ? 18000 : 6000,  // dayTime
            true,                   // keepInventory
            worldDataBase64
        );

    private async Task<DirectoryInfo?> SetupServerFiles(byte[] serverData)
    {
        var worldDir = new DirectoryInfo(Path.Combine(_baseDir.FullName, "world"));
        if (!worldDir.TryCreate())
        {
            _logger.Error("Could not create server world directory");
            return null;
        }

        var worldEntitiesDir = new DirectoryInfo(Path.Combine(worldDir.FullName, "entities"));
        if (!worldEntitiesDir.TryCreate())
        {
            _logger.Error("Could not create server world entities directory");
            return null;
        }

        var worldRegionDir = new DirectoryInfo(Path.Combine(worldDir.FullName, "region"));
        if (!worldRegionDir.TryCreate())
        {
            _logger.Error("Could not create server world regions directory");
            return null;
        }

        using (var byteArrayInputStream = new MemoryStream(serverData))
        using (var zipInputStream = new ZipArchive(byteArrayInputStream))
        {
            foreach (ZipArchiveEntry entry in zipInputStream.Entries)
            {
                if (entry.IsDirectory)
                {
                    continue;
                }

                string path = Path.Combine(worldDir.FullName, entry.FullName);

                using (Stream zipStream = entry.Open())
                using (FileStream fs = File.OpenWriteNew(path))
                {
                    zipStream.CopyTo(fs);
                }
            }
        }

        return worldDir;
    }

    private void CleanupBaseDir()
    {
        _logger.Information("Cleaning up runtime directory");

        try
        {
            if (_baseDir.Exists)
            {
                _baseDir.Delete(recursive: true);
            }
        }
        catch (Exception exception)
        {
            _logger.Error(exception, $"Exception while cleaning up runtime directory: {exception.Message}");
        }
    }

    private void StartHostPlayerConnectTimeout()
        => Task.Run(async () =>
        {
            await Task.Delay(checked((int)HOST_PLAYER_CONNECT_TIMEOUT));

            if (_shuttingDown)
            {
                return;
            }

            if (!_hostPlayerConnected)
            {
                _logger.Information("Host player has not connected yet, shutting down");
                BeginShutdown();
            }
        }).Forget();

    private void StartShutdownTimer()
        => Task.Run(async () =>
        {
            await Task.Yield();

            if (_shutdownTime is { } shutdownTime)
            {
                long currentTime = U.CurrentTimeMillis();
                while (currentTime < shutdownTime)
                {
                    long duration = shutdownTime - currentTime;
                    if (duration > 0)
                    {
                        _logger.Information("Server will shut down in {} milliseconds", duration);
                        await Task.Delay(checked((int)(duration > 2000 ? (duration / 2) : duration)));
                    }

                    currentTime = U.CurrentTimeMillis();
                }
            }

            _logger.Information("Shutdown time has been reached, shutting down");
            BeginShutdown();
        }).Forget();

    private void BeginShutdown()
        => Task.Run(async () =>
        {
            await Task.Yield();

            _shutdownLock.Enter();
            try
            {
                if (_shuttingDown)
                {
                    _logger.Debug("Already shutting down, not beginning shutdown");
                    return;
                }

                _shuttingDown = true;
            }
            finally
            {
                _shutdownLock.Exit();
            }

            _logger.Information("Beginning shutdown");

            SendEventBusInstanceStatusNotification("shuttingDown");

            if (_dimensionKey is not null)
            {
                await SendDestroyInstanceAsync();
            }

            _shutdownTcs.TrySetResult();
        }).Forget();

    public async Task WaitForShutdownAsync()
    {
        while (_thread is null)
        {
            await Task.Delay(50);
        }

        await _thread;
    }

    private sealed record BuildplateLoadRequest(
        string PlayerId,
        string BuildplateId
    );

    private sealed record SharedBuildplateLoadRequest(
        string SharedBuildplateId
    );

    private sealed record EncounterBuildplateLoadRequest(
        string EncounterBuildplateId
    );

    private sealed record BuildplateLoadResponse(
        string ServerDataBase64
    );

    private sealed record CreateInstanceRequest(
        string InstanceId,
        string GeneratorType,
        GeneratorSettings GeneratorSettings
    );

    private sealed record GeneratorSettings(
        int BuildplateWidth,
        int BuildplateDepth,
        int BuildplateGroundLevel,
        int BuildplateUndergroundHeight,
        int GameType,
        int Difficulty,
        long DayTime,
        bool KeepInventory,
        string WorldData
    );

    private sealed record CreateInstanceResponse(
        bool Success,
        string? DimensionKey,
        string? Error
    );

    private sealed record DestroyInstanceRequest(
        string InstanceId
    );

    private sealed record DestroyInstanceResponse(
        bool Success,
        string? Error
    );

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum BuildplateSource
    {
        PLAYER,
        SHARED,
        ENCOUNTER
    }
}
