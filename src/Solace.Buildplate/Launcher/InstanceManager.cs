using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json.Serialization;
using Serilog;
using Solace.Buildplate.Connector.Model;
using Solace.Buildplate.Model;
using Solace.Common;
using Solace.Common.Utils;
using Solace.EventBus.Client;

namespace Solace.Buildplate.Launcher;

public sealed class InstanceManager
{
    private readonly Starter _starter;
    private readonly Publisher _publisher;
    private readonly string _publicAddress;

    private RequestHandler _requestHandler = null!;
    private bool _shuttingDown;
    private readonly Lock _lock = new Lock();
    private readonly ConcurrentDictionary<string, Instance> _activeInstances = new();

    [JsonConverter(typeof(JsonStringEnumConverter))]
    private enum InstanceType
    {
        BUILD,
        PLAY,
        SHARED_BUILD,
        SHARED_PLAY,
        ENCOUNTER,
        PLAYER_ADVENTURE,
    }

    private sealed record StartRequest(
        string? PlayerId,
        string? EncounterId,
        string BuildplateId,
        bool Night,
        InstanceType Type,
        long ShutdownTime
    );

    private sealed record StartNotification(
        string InstanceId,
        string? PlayerId,
        string? EncounterId,
        string BuildplateId,
        string Address,
        int Port,
        InstanceType Type
    );

    // Response to the connector plugin's playerLogin request (see
    // ViennaConnectorPlugin.PlayerLoginResponse): accepted + the instance the
    // player should be routed to.
    private sealed record PlayerLoginResponse(bool Accepted, string? InstanceId);

    public InstanceManager(Starter starter, Publisher publisher, string publicAddress)
    {
        _starter = starter;
        _publisher = publisher;
        _publicAddress = publicAddress;
    }

    public static async Task<InstanceManager> CreateAsync(EventBusClient eventBusClient, Starter starter, string publicAddress)
    {
        var publisher = await eventBusClient.AddPublisherAsync();

        var instanceManager = new InstanceManager(starter, publisher, publicAddress);

        instanceManager._requestHandler = await eventBusClient.AddRequestHandlerAsync("buildplates", new RequestHandlerLister(
            async request =>
            {
                if (request.Type is "start")
                {
                    instanceManager._lock.Enter();
                    if (instanceManager._shuttingDown)
                    {
                        instanceManager._lock.Exit();
                        return null;
                    }

                    instanceManager._lock.Exit();

                    StartRequest startRequest;
                    try
                    {
                        startRequest = Json.Deserialize<StartRequest>(request.Data)!;
                    }
                    catch (Exception exception)
                    {
                        Log.Warning(exception, "Bad start request");
                        return null;
                    }

                    bool survival;
                    bool saveEnabled;
                    InventoryType inventoryType;
                    Instance.BuildplateSource buildplateSource;
                    long? shutdownTime;
                    switch (startRequest.Type)
                    {
                        case InstanceType.BUILD:
                            {
                                survival = false;
                                saveEnabled = true;
                                inventoryType = InventoryType.SYNCED;
                                buildplateSource = Instance.BuildplateSource.PLAYER;
                                shutdownTime = null;
                            }

                            break;
                        case InstanceType.PLAY:
                            {
                                survival = true;
                                saveEnabled = false;
                                inventoryType = InventoryType.DISCARD;
                                buildplateSource = Instance.BuildplateSource.PLAYER;
                                shutdownTime = null;
                            }

                            break;
                        case InstanceType.SHARED_BUILD:
                            {
                                survival = false;
                                saveEnabled = false;
                                inventoryType = InventoryType.DISCARD;
                                buildplateSource = Instance.BuildplateSource.SHARED;
                                shutdownTime = null;
                            }

                            break;
                        case InstanceType.SHARED_PLAY:
                            {
                                survival = true;
                                saveEnabled = false;
                                inventoryType = InventoryType.DISCARD;
                                buildplateSource = Instance.BuildplateSource.SHARED;
                                shutdownTime = null;
                            }

                            break;
                        case InstanceType.ENCOUNTER:
                        case InstanceType.PLAYER_ADVENTURE:
                            {
                                survival = true;
                                saveEnabled = false;
                                inventoryType = InventoryType.BACKPACK;
                                buildplateSource = Instance.BuildplateSource.ENCOUNTER;
                                shutdownTime = startRequest.ShutdownTime;
                            }

                            break;
                        default:
                            {
                                Log.Warning("Bad start request");
                                return null;
                            }
                    }

                    if (buildplateSource == Instance.BuildplateSource.PLAYER && startRequest.PlayerId is null)
                    {
                        Log.Warning("Bad start request");
                        return null;
                    }

                    string instanceId = U.RandomUuid().ToString();

                    Log.Information($"Starting buildplate instance {instanceId}");

                    Instance? instance = instanceManager._starter.StartInstance(instanceId, startRequest.PlayerId, startRequest.BuildplateId, buildplateSource, survival, startRequest.Night, saveEnabled, inventoryType, shutdownTime);
                    if (instance is null)
                    {
                        Log.Error($"Error starting buildplate instance {instanceId}");
                        return null;
                    }

                    instanceManager._activeInstances[instanceId] = instance;

                    instanceManager.SendEventBusMessage("started", Json.Serialize(new StartNotification(
                        instanceId,
                        startRequest.PlayerId,
                        startRequest.EncounterId,
                        startRequest.BuildplateId,
                        instanceManager._publicAddress,
                        Starter.BRIDGE_PORT,
                        startRequest.Type
                    )));

                    Task.Run(async () =>
                    {
                        try
                        {
                            await instance.WaitForShutdownAsync();

                            instanceManager.SendEventBusMessage("stopped", instance.InstanceId);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Failed to send stopped message");
                        }

                        instanceManager._activeInstances.TryRemove(instanceId, out _);
                    }).Forget();

                    return instanceId;
                }
                else if (request.Type is "playerLogin")
                {
                    // Sent by the persistent bridge's connector plugin when a Bedrock
                    // player logs in: resolve which buildplate instance (dimension on
                    // the persistent Fabric server) the player belongs to. Payload is
                    // a plain JSON string with the player id. Route to the player's
                    // most recently created instance: a player can have several live
                    // instances at once (an encounter plus a freshly started
                    // buildplate), and a FirstOrDefault over the
                    // ConcurrentDictionary would pick an arbitrary one.
                    string? playerId;
                    try
                    {
                        playerId = Json.Deserialize<string>(request.Data);
                    }
                    catch (Exception exception)
                    {
                        Log.Warning(exception, "Bad playerLogin request");
                        return null;
                    }
                    if (string.IsNullOrEmpty(playerId))
                    {
                        return null;
                    }

                    lock (instanceManager._lock)
                    {
                        Instance? instance = instanceManager._activeInstances.Values.Where(instance => instance.PlayerId == playerId).OrderByDescending(instance => instance.CreatedAt).FirstOrDefault();
                        if (instance is null)
                        {
                            Log.Information($"playerLogin for player {playerId}: no active instance, rejecting");
                            return Json.Serialize(new PlayerLoginResponse(false, null));
                        }
                        Log.Information($"playerLogin for player {playerId}: routing to instance {instance.InstanceId}");
                        return Json.Serialize(new PlayerLoginResponse(true, instance.InstanceId));
                    }
                }
                else if (request.Type is "preview")
                {
                    PreviewRequest previewRequest;
                    byte[] serverData;
                    try
                    {
                        previewRequest = Json.Deserialize<PreviewRequest>(request.Data)!;
                        serverData = Convert.FromBase64String(previewRequest.ServerDataBase64);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"Bad preview request: {ex}");
                        return null;
                    }

                    Log.Information("Generating buildplate preview");

                    string? preview = PreviewGenerator.GeneratePreview(serverData, previewRequest.Night, Program.StaticDataPath);
                    if (preview is null)
                    {
                        Log.Warning("Could not generate preview for buildplate");
                    }

                    return preview;
                }
                else
                {
                    return null;
                }
            },
            async () =>
            {
                Log.Error("Event bus request handler error");
            }
        ));

        return instanceManager;
    }

    private void SendEventBusMessage(string type, string message)
        => _publisher.PublishAsync("buildplates", type, message).ContinueWith(task =>
        {
            if (!task.Result)
            {
                Log.Error("Event bus publisher error");
            }
        });

    public async Task ShutdownAsync()
    {
        await _requestHandler.CloseAsync();

        _lock.Enter();
        _shuttingDown = true;
        Log.Information($"Shutdown signal received, no new buildplate instances will be started, waiting for {_activeInstances.Count} instances to finish");
        while (!_activeInstances.IsEmpty)
        {
            int activeInstanceCount = _activeInstances.Count;
            _lock.Exit();

            await Task.Delay(1000);

            _lock.Enter();
            if (_activeInstances.Count != activeInstanceCount)
            {
                Log.Information($"Waiting for {_activeInstances.Count} instances to finish");
            }
        }

        _lock.Exit();

        await _publisher.FlushAsync();
        await _publisher.CloseAsync();
    }
}
