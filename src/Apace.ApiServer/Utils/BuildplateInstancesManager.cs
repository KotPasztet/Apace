using Serilog;
using System.Text.Json.Serialization;
using Apace.Common;
using Apace.Common.Utils;
using Apace.EventBus.Client;

namespace Apace.ApiServer.Utils;

public sealed class BuildplateInstancesManager
{
    private readonly EventBusClient _eventBusClient;
    private readonly SemaphoreSlim _requestSenderLock = new(1, 1);
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private Subscriber _subscriber = null!;
    private RequestSender _requestSender = null!;

    private readonly Dictionary<string, TaskCompletionSource<bool>?> _pendingInstances = [];
    private readonly Dictionary<string, InstanceInfo> _instances = [];
    private readonly Dictionary<string, HashSet<string>> _instancesByBuildplateId = [];
    private readonly Dictionary<string, string> _joinCodes = [];
    private readonly Dictionary<string, string> _instanceIdsByJoinCode = [];
    // Players currently routed to another player's instance as guests (playerId -> host instanceId)
    private readonly Dictionary<string, string> _guests = [];

    // unambiguous characters only, the code is typed in manually by players
    private const string JoinCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public string GetJoinCode(string instanceId)
    {
        lock (_instances)
        {
            if (_joinCodes.TryGetValue(instanceId, out string? existing))
            {
                return existing;
            }

            string code;
            do
            {
                code = string.Create(8, 0, (span, _) =>
                {
                    for (int i = 0; i < span.Length; i++)
                    {
                        span[i] = JoinCodeAlphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(JoinCodeAlphabet.Length)];
                    }
                });
            }
            while (_instanceIdsByJoinCode.ContainsKey(code));

            _joinCodes[instanceId] = code;
            _instanceIdsByJoinCode[code] = instanceId;
            return code;
        }
    }

    public InstanceInfo? GetInstanceInfoByJoinCode(string joinCode)
    {
        lock (_instances)
        {
            string normalized = joinCode.Trim().ToUpperInvariant();
            return _instanceIdsByJoinCode.TryGetValue(normalized, out string? instanceId) ? _instances.GetOrDefault(instanceId, null) : null;
        }
    }

    private BuildplateInstancesManager(EventBusClient eventBusClient)
    {
        _eventBusClient = eventBusClient;
    }

    public static async Task<BuildplateInstancesManager> CreateAsync(EventBusClient eventBusClient)
    {
        var buildplateInstancesManager = new BuildplateInstancesManager(eventBusClient);

        buildplateInstancesManager._subscriber = await eventBusClient.AddSubscriberAsync("buildplates", new SubscriberListener(
           buildplateInstancesManager.HandleEvent,
           async () =>
           {
               Log.Fatal("Buildplates event bus subscriber error");
               Log.CloseAndFlush();
               Environment.Exit(1);
           }
       ));

        buildplateInstancesManager._requestSender = await eventBusClient.AddRequestSenderAsync();

        return buildplateInstancesManager;
    }

    public async Task<string?> RequestBuildplateInstance(string? playerId, string? encounterId, string buildplateId, InstanceType type, long shutdownTime, bool night)
    {
        if (playerId is null && type is not InstanceType.ENCOUNTER and not InstanceType.PLAYER_ADVENTURE)
        {
            throw new ArgumentException($"{nameof(playerId)} cannot be null when {nameof(type)} is not {nameof(InstanceType.ENCOUNTER)} or {nameof(InstanceType.PLAYER_ADVENTURE)}.");
        }

        if (encounterId is not null && type is not InstanceType.ENCOUNTER and not InstanceType.PLAYER_ADVENTURE)
        {
            throw new ArgumentException($"{nameof(encounterId)} can only be set when {nameof(type)} is {nameof(InstanceType.ENCOUNTER)} or {nameof(InstanceType.PLAYER_ADVENTURE)}.");
        }

        // The player is requesting their own instance (new or reused) - forget any
        // guest routing left over from an earlier join so their next playerLogin is
        // routed to this instance instead of the host's.
        if (playerId is not null)
        {
            await RemoveGuestAsync(playerId);
        }

        await _startLock.WaitAsync();
        try
        {
        if (playerId is not null && encounterId is not null)
        {
            Log.Information($"Finding buildplate instance for buildplate {buildplateId} type {type} encounter {encounterId} player {playerId}");
        }
        else if (playerId is not null)
        {
            Log.Information($"Finding buildplate instance for buildplate {buildplateId} type {type} player {playerId}");
        }
        else if (encounterId is not null)
        {
            Log.Information($"Finding buildplate instance for buildplate {buildplateId} type {type} encounter {encounterId}");
        }
        else
        {
            Log.Information($"Finding buildplate instance for buildplate {buildplateId} type {type}");
        }

        lock (_instances)
        {
            HashSet<string>? instanceIds = _instancesByBuildplateId.GetOrDefault(buildplateId);
            if (instanceIds is not null)
            {
                foreach (string loopInstanceId in instanceIds)
                {
                    InstanceInfo? instanceInfo = _instances.GetOrDefault(loopInstanceId);
                    if (instanceInfo is not null && !instanceInfo.ShuttingDown)
                    {
                        bool sameEncounter = instanceInfo.EncounterId == encounterId || type is InstanceType.PLAYER_ADVENTURE;
                        if (instanceInfo.Type == type &&
                            instanceInfo.PlayerId == playerId &&
                            sameEncounter
                        )
                        {
                            Log.Information($"Found existing buildplate instance {loopInstanceId}");
                            return loopInstanceId;
                        }
                    }
                }
            }
        }

        Log.Information("Did not find existing instance, starting new instance");
        string? instanceId = await SendStartRequestWithTimeoutAsync(playerId, encounterId, buildplateId, night, type, shutdownTime);
        if (instanceId is null)
        {
            Log.Error("Buildplate start request was rejected/ignored");
            return null;
        }

        TaskCompletionSource<bool> completableFuture = new();
        lock (_instances)
        {
            if (_instances.ContainsKey(instanceId))
            {
                completableFuture.SetResult(true);
            }
            else
            {
                lock (_pendingInstances)
                {
                    _pendingInstances[instanceId] = completableFuture;
                }
            }
        }

        if (!await completableFuture.Task)
        {
            Log.Warning($"Could not start buildplate instance {instanceId}");
            return null;
        }

        return instanceId;
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task<string?> SendStartRequestWithTimeoutAsync(string? playerId, string? encounterId, string buildplateId, bool night, InstanceType type, long shutdownTime)
    {
        await _requestSenderLock.WaitAsync();
        try
        {
            Task<string?> responseTask = _requestSender.RequestAsync("buildplates", "start", Json.Serialize(new StartRequest(playerId, encounterId, buildplateId, night, type, shutdownTime)));
            Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(8));
            if (await Task.WhenAny(responseTask, timeoutTask) == responseTask)
            {
                return await responseTask;
            }

            Log.Warning("Buildplate start request timed out for buildplate {BuildplateId}; resetting sender", buildplateId);
            await ResetRequestSenderAsync();
            return null;
        }
        finally
        {
            _requestSenderLock.Release();
        }
    }

    private async Task ResetRequestSenderAsync()
    {
        try
        {
            await _requestSender.CloseAsync();
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Could not close stale buildplates request sender");
        }

        _requestSender = await _eventBusClient.AddRequestSenderAsync();
    }

    public InstanceInfo? GetInstanceInfo(string instanceId)
    {
        lock (_instances)
        {
            return _instances.GetOrDefault(instanceId, null);
        }
    }

    public async Task<bool> AddGuestAsync(string playerId, string instanceId)
    {
        Log.Information($"Adding player {playerId} as guest to instance {instanceId}");

        string? response = await _requestSender.RequestAsync("buildplates", "addGuest", Json.Serialize(new AddGuestRequest(playerId, instanceId)));
        bool added = response is not null && Json.Deserialize<bool>(response);
        if (added)
        {
            lock (_instances)
            {
                _guests[playerId] = instanceId;
            }
        }
        return added;
    }

    // Forget a player's guest routing so their next playerLogin is routed to their
    // own instance again. Called when the player requests their own instance (new
    // or reused) after previously joining someone else's buildplate with a join code.
    public async Task RemoveGuestAsync(string playerId)
    {
        lock (_instances)
        {
            if (!_guests.ContainsKey(playerId))
            {
                return;
            }
        }

        string? response = await _requestSender.RequestAsync("buildplates", "removeGuest", Json.Serialize(new RemoveGuestRequest(playerId)));
        if (response is not null && Json.Deserialize<bool>(response))
        {
            lock (_instances)
            {
                _guests.Remove(playerId);
            }
            Log.Information($"Removed player {playerId} from guest routing");
        }
        else
        {
            Log.Warning($"Could not remove player {playerId} from guest routing");
        }
    }

    private sealed record AddGuestRequest(string PlayerId, string InstanceId);

    private sealed record RemoveGuestRequest(string PlayerId);

    public async Task<string?> GetBuildplatePreviewAsync(byte[] serverData, bool night)
    {
        Log.Information("Requesting buildplate preview");

        string? preview = await _requestSender.RequestAsync("buildplates", "preview", Json.Serialize(new PreviewRequest(Convert.ToBase64String(serverData), night)));
        if (preview is null)
        {
            Log.Error("Preview request was rejected/ignored");
        }

        return preview;
    }

    private Task HandleEvent(SubscriberEvent @event)
    {
        switch (@event.Type)
        {
            case "started":
                {
                    StartNotification startNotification;
                    try
                    {
                        startNotification = Json.Deserialize<StartNotification>(@event.Data)!;
                        if (startNotification.PlayerId is null && startNotification.Type is not InstanceType.ENCOUNTER and not InstanceType.PLAYER_ADVENTURE)
                        {
                            Log.Warning("Bad start notification");
                            return Task.CompletedTask;
                        }

                        lock (_instances)
                        {
                            Log.Information($"Buildplate instance {startNotification.InstanceId} has started");
                            _instances[startNotification.InstanceId] = new InstanceInfo(
                                startNotification.Type,
                                startNotification.InstanceId,
                                startNotification.PlayerId,
                                startNotification.EncounterId,
                                startNotification.BuildplateId,
                                startNotification.Address,
                                startNotification.Port,
                                false,
                                false
                            );

                            _instancesByBuildplateId.ComputeIfAbsent(startNotification.BuildplateId, buildplateId => [])!.Add(startNotification.InstanceId);
                        }

                        lock (_pendingInstances)
                        {
                            TaskCompletionSource<bool>? completableFuture = _pendingInstances.JavaRemove(startNotification.InstanceId);
                            completableFuture?.SetResult(true);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"Bad start notification: {ex}");
                    }
                }

                break;
            case "ready":
                {
                    string instanceId = @event.Data;
                    lock (_instances)
                    {
                        InstanceInfo? instanceInfo = _instances.GetOrDefault(instanceId, null);
                        if (instanceInfo is not null)
                        {
                            Log.Information($"Buildplate instance {instanceId} is ready");
                            _instances[instanceId] = new InstanceInfo(
                                instanceInfo.Type,
                                instanceInfo.InstanceId,
                                instanceInfo.PlayerId,
                                instanceInfo.EncounterId,
                                instanceInfo.BuildplateId,
                                instanceInfo.Address,
                                instanceInfo.Port,
                                true,
                                instanceInfo.ShuttingDown
                            );
                        }
                    }
                }

                break;
            case "shuttingDown":
                {
                    string instanceId = @event.Data;
                    lock (_instances)
                    {
                        InstanceInfo? instanceInfo = _instances.GetValueOrDefault(instanceId);
                        if (instanceInfo is not null)
                        {
                            Log.Information($"Buildplate instance {instanceId} is shutting down");
                            _instances[instanceId] = new InstanceInfo(
                                instanceInfo.Type,
                                instanceInfo.InstanceId,
                                instanceInfo.PlayerId,
                                instanceInfo.EncounterId,
                                instanceInfo.BuildplateId,
                                instanceInfo.Address,
                                instanceInfo.Port,
                                instanceInfo.Ready,
                                true
                            );
                        }
                    }
                }

                break;
            case "stopped":
                {
                    string instanceId = @event.Data;
                    lock (_instances)
                    {
                        var instanceInfo = _instances.JavaRemove(instanceId);
                        if (instanceInfo is not null)
                        {
                            Log.Information($"Buildplate instance {instanceId} has stopped");

                            var instanceIds = _instancesByBuildplateId.GetOrDefault(instanceInfo.BuildplateId);
                            instanceIds?.Remove(instanceInfo.InstanceId);
                        }

                        if (_joinCodes.Remove(instanceId, out string? joinCode))
                        {
                            _instanceIdsByJoinCode.Remove(joinCode);
                        }

                        foreach (var guest in _guests.Where(entry => entry.Value == instanceId).ToList())
                        {
                            _guests.Remove(guest.Key);
                        }
                    }
                }

                break;
            default:
                break;
        }

        return Task.CompletedTask;
    }

    private sealed record StartRequest(
        string? PlayerId,
        string? EncounterId,
        string BuildplateId,
        bool Night,
        InstanceType Type,
        long ShutdownTime
    );

    private sealed record PreviewRequest(
        string ServerDataBase64,
        bool Night
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

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum InstanceType
    {
#pragma warning disable CA1707 // Identifiers should not contain underscores
        BUILD,
        PLAY,
        SHARED_BUILD,
        SHARED_PLAY,
        ENCOUNTER,
        PLAYER_ADVENTURE,
#pragma warning restore CA1707 // Identifiers should not contain underscores
    }

    public sealed record InstanceInfo(
        InstanceType Type,

        string InstanceId,

        string? PlayerId,
        string? EncounterId,
        string BuildplateId,

        string Address,
        int Port,

        bool Ready,
        bool ShuttingDown
    );
}
