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

    // Stopped instances whose "stopped" event carried a failure reason, kept so a
    // readiness waiter that only sees the instance disappear can surface why it
    // stopped (consumed by ConsumeStopReason). Failure stops are rare, but bound the
    // map anyway so reasons nobody waited for cannot accumulate forever.
    private const int MaxTrackedStoppedInstances = 128;
    private readonly Dictionary<string, InstanceInfo> _stoppedInstances = [];

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

    /// <summary>
    /// <paramref name="reuseExisting"/>: when false, a new instance is always started, even if the player already has a running instance of the same type for this buildplate.
    /// </summary>
    public async Task<string?> RequestBuildplateInstance(string? playerId, string? encounterId, string buildplateId, InstanceType type, long shutdownTime, bool night, bool reuseExisting = true)
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

        string? reusedInstanceId = null;
        lock (_instances)
        {
            HashSet<string>? instanceIds = _instancesByBuildplateId.GetOrDefault(buildplateId);
            if (reuseExisting && instanceIds is not null)
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
                            reusedInstanceId = loopInstanceId;
                            break;
                        }
                    }
                }
            }
        }

        if (reusedInstanceId is not null)
        {
            // The launcher routes a playerLogin to the player's newest instance, and a reused
            // instance is not necessarily the newest one (e.g. an older buildplate instance
            // rejoined after playing an adventure), so tell the launcher which instance this
            // player is about to join.
            if (playerId is not null)
            {
                await RouteLoginAsync(reusedInstanceId, playerId);
            }

            return reusedInstanceId;
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

    /// <summary>
    /// Ends the running instances of the given player adventures early (a second adventure crystal replaces the first adventure).
    /// Matches on the adventure id the instance was started for, so trading post and buildplate instances are never touched.
    /// </summary>
    public async Task StopAdventureInstancesAsync(IEnumerable<string> adventureIds)
    {
        HashSet<string> ids = [.. adventureIds];
        if (ids.Count == 0)
        {
            return;
        }

        List<string> instanceIds = [];
        lock (_instances)
        {
            foreach (InstanceInfo instanceInfo in _instances.Values)
            {
                if (instanceInfo.Type is InstanceType.PLAYER_ADVENTURE &&
                    !instanceInfo.ShuttingDown &&
                    instanceInfo.EncounterId is not null &&
                    ids.Contains(instanceInfo.EncounterId))
                {
                    instanceIds.Add(instanceInfo.InstanceId);
                }
            }
        }

        foreach (string instanceId in instanceIds)
        {
            Log.Information("Stopping adventure instance {InstanceId} because the player started a new adventure", instanceId);
            await StopInstanceAsync(instanceId);
        }
    }

    private async Task StopInstanceAsync(string instanceId)
    {
        await _requestSenderLock.WaitAsync();
        try
        {
            Task<string?> responseTask = _requestSender.RequestAsync("buildplates", "stopInstance", Json.Serialize(new StopInstanceRequest(instanceId)));
            Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(8));
            if (await Task.WhenAny(responseTask, timeoutTask) != responseTask)
            {
                Log.Warning("stopInstance request timed out for instance {InstanceId}; resetting sender", instanceId);
                await ResetRequestSenderAsync();
                return;
            }

            string? response = await responseTask;
            if (response is null || !Json.Deserialize<bool>(response))
            {
                Log.Warning("Launcher did not stop instance {InstanceId} (already stopped, or the launcher was not updated to handle stopInstance)", instanceId);
                return;
            }
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Could not stop instance {InstanceId}", instanceId);
            return;
        }
        finally
        {
            _requestSenderLock.Release();
        }

        // the launcher's shuttingDown event follows shortly, mark the instance now so nothing reuses it in the meantime
        lock (_instances)
        {
            if (_instances.TryGetValue(instanceId, out InstanceInfo? instanceInfo))
            {
                _instances[instanceId] = instanceInfo with { ShuttingDown = true };
            }
        }
    }

    /// <summary>
    /// Tells the launcher which instance a player is about to join, so their login is routed there instead of to their newest instance.
    /// Reuses the launcher's guest routing, but does not mark the player as a guest of the instance on this side. Never fails the caller.
    /// </summary>
    private async Task RouteLoginAsync(string instanceId, string playerId)
    {
        await _requestSenderLock.WaitAsync();
        try
        {
            Task<string?> responseTask = _requestSender.RequestAsync("buildplates", "routeLogin", Json.Serialize(new RouteLoginRequest(instanceId, playerId)));
            Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(8));
            if (await Task.WhenAny(responseTask, timeoutTask) != responseTask)
            {
                Log.Warning("Could not route player {PlayerId} to instance {InstanceId}: request timed out; resetting sender", playerId, instanceId);
                await ResetRequestSenderAsync();
                return;
            }

            string? response = await responseTask;
            if (response is null || !Json.Deserialize<bool>(response))
            {
                Log.Warning("Launcher did not accept routing player {PlayerId} to instance {InstanceId}", playerId, instanceId);
            }
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Could not route player {PlayerId} to instance {InstanceId}", playerId, instanceId);
        }
        finally
        {
            _requestSenderLock.Release();
        }
    }

    public InstanceInfo? GetInstanceInfo(string instanceId)
    {
        lock (_instances)
        {
            return _instances.GetOrDefault(instanceId, null);
        }
    }

    // Returns and clears the launcher-reported failure reason for a stopped instance,
    // if its "stopped" event carried one.
    public string? ConsumeStopReason(string instanceId)
    {
        lock (_instances)
        {
            if (_stoppedInstances.Remove(instanceId, out InstanceInfo? instanceInfo))
            {
                return instanceInfo.StopReason;
            }

            return null;
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

    private sealed record StopInstanceRequest(
        string InstanceId
    );

    private sealed record RouteLoginRequest(
        string InstanceId,
        string PlayerId
    );

    // Whether a player may see the status of an instance: the host, a guest
    // who joined with a join code, or anyone if the instance has no owner
    // (encounters owned by a tapping player are covered by the host check).
    public bool IsParticipant(string playerId, string instanceId)
    {
        lock (_instances)
        {
            InstanceInfo? instanceInfo = _instances.GetOrDefault(instanceId, null);
            if (instanceInfo is null)
            {
                return false;
            }

            if (instanceInfo.PlayerId is null || instanceInfo.PlayerId == playerId)
            {
                return true;
            }

            return _guests.TryGetValue(playerId, out string? guestInstanceId) && guestInstanceId == instanceId;
        }
    }

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
                    // The payload is a JSON object (instanceId + optional error);
                    // launchers older than the error field published the bare instance
                    // id, so fall back to reading it as-is when the payload is not JSON.
                    string instanceId = @event.Data;
                    string? stopReason = null;
                    try
                    {
                        StoppedNotification? stoppedNotification = Json.Deserialize<StoppedNotification>(@event.Data);
                        if (stoppedNotification is not null && !string.IsNullOrEmpty(stoppedNotification.InstanceId))
                        {
                            instanceId = stoppedNotification.InstanceId;
                            stopReason = stoppedNotification.Error;
                        }
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        // legacy bare instance id payload
                    }

                    lock (_instances)
                    {
                        var instanceInfo = _instances.JavaRemove(instanceId);
                        if (instanceInfo is not null)
                        {
                            Log.Information($"Buildplate instance {instanceId} has stopped{(stopReason is null ? "" : $": {stopReason}")}");

                            if (stopReason is not null)
                            {
                                if (_stoppedInstances.Count >= MaxTrackedStoppedInstances)
                                {
                                    _stoppedInstances.Clear();
                                }

                                _stoppedInstances[instanceId] = instanceInfo with { StopReason = stopReason };
                            }

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

    private sealed record StoppedNotification(
        string InstanceId,
        string? Error
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
        bool ShuttingDown,

        // Why the instance stopped, from the launcher's "stopped" event; null unless
        // the launcher attached a failure reason.
        string? StopReason = null
    );
}
