using System.Diagnostics;
using Serilog;
using Solace.Common.Utils;
using Solace.LauncherUI.Programs;
using Solace.LauncherUI.Utils;
using Solace.EventBus.Client;

namespace Solace.LauncherUI;

public sealed class ServerComponent
{
    public string Name { get; }
    public string ExeName { get; }
    public Func<Settings, Serilog.ILogger, Process?> StartAction { get; }
    public int StartupDelayMs { get; }
    public Func<Settings, bool> IsEnabled { get; }

    public ServerStatus Status { get; set; } = ServerStatus.Offline;

    public ServerComponent(string name, string exeName, Func<Settings, Serilog.ILogger, Process?> startAction, int startupDelayMs = 0, Func<Settings, bool>? isEnabled = null)
    {
        Name = name;
        ExeName = exeName;
        StartAction = startAction;
        StartupDelayMs = startupDelayMs;
        IsEnabled = isEnabled ?? (_ => true);
    }
}

public sealed class ServerManager : IDisposable
{
    public event Action? OnStatusChanged;

    private ServerStatus _status = ServerStatus.Offline;
    public ServerStatus Status
    {
        get => _status;
        private set
        {
            if (_status != value)
            {
                _status = value;
                OnStatusChanged?.Invoke();
            }
        }
    }

    public bool AnyOnline { get; private set; }

    private int _startLockCount;
    public bool StartLocked => Volatile.Read(ref _startLockCount) > 0;

    private int _patchLockCount;

    /// <summary>True while a client patch job is running - the Earth server must not be (re)started then.</summary>
    public bool PatchLocked => Volatile.Read(ref _patchLockCount) > 0;

    public bool CanStart => !StartLocked && !PatchLocked && Status is not (ServerStatus.Starting or ServerStatus.Online);
    public bool CanRestart => Status is not (ServerStatus.Stopping or ServerStatus.Offline);
    public bool CanStop => Status is not (ServerStatus.Stopping or ServerStatus.Offline);

    public IReadOnlyList<ServerComponent> Components { get; }

    private readonly Lock _statusLock = new Lock();

    private CancellationTokenSource? _operationTokenSource;

    private readonly LogsLogService _logsLogService;

    public ServerManager(LogsLogService logsLogService)
    {
        _logsLogService = logsLogService;

        Components =
        [
            new("Event Bus", EventBusServer.ExeName, EventBusServer.Run),
            new("Object Store", ObjectStoreServer.ExeName, ObjectStoreServer.Run, 1000),
            new("Buildplate Launcher", BuildplateLauncher.ExeName, BuildplateLauncher.Run, 1500),
            new("API Server", ApiServer.ExeName, ApiServer.Run),
            new("Tappables Generator", TappablesGenerator.ExeName, TappablesGenerator.Run),
            new("Tile Renderer", TileRenderer.ExeName, TileRenderer.Run, 0, s => s.EnableTileRenderingLabel ?? true)
        ];

        RefreshComponentStatuses();
    }

    public void RefreshComponentStatuses(bool detectRunning = true, bool preserveCurrentStatus = false)
    {
        var settings = Settings.Instance;
        bool anyOnline = false;

        foreach (var comp in Components)
        {
            if (detectRunning)
            {
                bool isRunning = ProcessUtils.GetProgramProcesses(comp.ExeName).Any();

                comp.Status = comp.Status switch
                {
                    ServerStatus.Starting => isRunning ? ServerStatus.Online : ServerStatus.Starting,
                    ServerStatus.Stopping => isRunning ? ServerStatus.Stopping : ServerStatus.Offline,
                    _ => isRunning ? ServerStatus.Online : ServerStatus.Offline,
                };
            }

            if (comp.Status is ServerStatus.Online && comp.IsEnabled(settings))
            {
                anyOnline = true;
            }
        }

        AnyOnline = anyOnline;
        var newStatus = ComputeGlobalStatus();
        if (preserveCurrentStatus && Status is ServerStatus.Starting && newStatus is ServerStatus.Offline)
        {
            return;
        }

        if (preserveCurrentStatus && Status is ServerStatus.Stopping && newStatus is ServerStatus.Offline)
        {
            return;
        }

        Status = newStatus;
    }

    public IDisposable? AcquireStartLock()
    {
        lock (_statusLock)
        {
            if (Status is ServerStatus.Offline)
            {
                return null;
            }

            return new StartLockHandle(this);
        }
    }

    /// <summary>
    /// Held for the whole duration of a client patch job (queued or running).
    /// While held, starting the Earth server or any of its components is refused:
    /// MCEPatcher.Core swaps the process working directory and global tool state,
    /// which breaks the server components (DB access etc.).
    /// </summary>
    public IDisposable AcquirePatchLock() => new PatchLockHandle(this);

    private ServerStatus ComputeGlobalStatus()
    {
        if (Components.Any(c => c.Status is ServerStatus.Stopping))
        {
            return ServerStatus.Stopping;
        }

        if (Components.Any(c => c.Status is ServerStatus.Starting))
        {
            return ServerStatus.Starting;
        }

        var enabledComponents = Components.Where(c => c.IsEnabled(Settings.Instance)).ToList();
        var onlineCount = enabledComponents.Count(c => c.Status is ServerStatus.Online);
        var offlineCount = enabledComponents.Count(c => c.Status is ServerStatus.Offline);

        if (enabledComponents.Count == 0)
        {
            return ServerStatus.Offline;
        }

        if (onlineCount > 0 && offlineCount > 0)
        {
            return ServerStatus.PartiallyOnline;
        }

        return onlineCount > 0 ? ServerStatus.Online : ServerStatus.Offline;
    }

    private static async Task<bool> WaitForProcessStartAsync(string exeName, CancellationToken cancellationToken)
    {
        const int intervalMs = 200;
        const int maxAttempts = 50;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ProcessUtils.GetProgramProcesses(exeName).Any())
            {
                return true;
            }

            await Task.Delay(intervalMs, cancellationToken);
        }

        return false;
    }

    public async Task<bool> EnsureComponentsOnline(params string[] exeNames)
    {
        if (exeNames is null || exeNames.Length == 0)
        {
            return true;
        }

        List<ServerComponent> targets;
        var reservedToStart = new HashSet<ServerComponent>();

        lock (_statusLock)
        {
            if (StartLocked || PatchLocked)
            {
                Log.Logger.Warning("EnsureComponentsOnline blocked because the server start is locked (start lock or patch in progress).");
                return false;
            }

            // Identify which of our managed components match the requested EXEs
            targets = [.. Components.Where(c => exeNames.Contains(c.ExeName, StringComparer.OrdinalIgnoreCase))];

            if (targets.Count == 0)
            {
                return true;
            }

            if (Status is ServerStatus.Stopping)
            {
                return false;
            }

            foreach (var comp in targets)
            {
                if (comp.Status is ServerStatus.Offline)
                {
                    comp.Status = ServerStatus.Starting;
                    reservedToStart.Add(comp);
                }
            }

            if (targets.All(t => t.Status is ServerStatus.Online))
            {
                return true;
            }

            if (Status is ServerStatus.Offline or ServerStatus.PartiallyOnline)
            {
                Status = ServerStatus.Starting;
            }
        }

        var logger = Log.Logger;
        var settings = Settings.Instance;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)); // Safety timeout

        try
        {
            foreach (var comp in targets)
            {
                if (comp.Status is ServerStatus.Online)
                {
                    continue;
                }

                bool shouldStart = reservedToStart.Contains(comp);
                if (shouldStart)
                {
                    logger.Debug($"Page-level initialization: Starting {comp.Name}");
                }
                else
                {
                    logger.Debug($"Page-level initialization: Waiting for {comp.Name} to become online");
                }

                var process = shouldStart ? comp.StartAction(settings, logger) : null;
                if (comp.StartupDelayMs > 0)
                {
                    await Task.Delay(comp.StartupDelayMs, cts.Token);
                }

                if (await WaitForProcessStartAsync(comp.ExeName, cts.Token))
                {
                    comp.Status = ServerStatus.Online;
                }
                else
                {
                    comp.Status = ServerStatus.Offline;
                    if (process is not null && process.HasExited)
                    {
                        logger.Error($"{comp.Name} process exited immediately during selective startup.");
                    }
                    else
                    {
                        logger.Error($"{comp.Name} failed to start during page-level initialization.");
                    }
                }

                OnStatusChanged?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            logger.Warning("Selective component startup cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Error during selective component startup");
            return false;
        }
        finally
        {
            RefreshComponentStatuses();
        }

        return targets.All(t => t.Status is ServerStatus.Online);
    }

    public async Task Start(CancellationToken cancellationToken = default)
    {
        lock (_statusLock)
        {
            if (!CanStart)
            {
                return;
            }

            cancellationToken = InitOperation(cancellationToken);
            Status = ServerStatus.Starting;
        }

        try
        {
            await StartInternal(Log.Logger, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await Stop(default);
        }
    }

    public async Task Stop(CancellationToken cancellationToken = default)
    {
        lock (_statusLock)
        {
            if ((Status is ServerStatus.Offline && !AnyOnline) || Status is ServerStatus.Stopping)
            {
                return;
            }

            cancellationToken = InitOperation(cancellationToken);
            Status = ServerStatus.Stopping;
        }

        // The Minecraft Fabric/"MC Java" server is a child of Buildplate
        // Launcher. Ask it first to stop the server gracefully ("stop" on the
        // server console, so the worlds are saved) before the loop below tears
        // everything down. It waits up to 25 s; a little extra margin is added
        // so we never hang here and always fall through to the force kill.
        await TryStopFabricServerGracefully(Log.Logger, cancellationToken);

        foreach (var comp in Components)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (comp.Status is ServerStatus.Offline)
            {
                continue;
            }

            comp.Status = ServerStatus.Stopping;
            OnStatusChanged?.Invoke();

            await StopProgram(comp.ExeName, Log.Logger, cancellationToken);

            comp.Status = ServerStatus.Offline;
            OnStatusChanged?.Invoke();
        }

        // The Minecraft Fabric/"MC Java" server itself is a plain "java"
        // process spawned as a child of Buildplate Launcher — it is not one
        // of the tracked Components above, so stopping Buildplate Launcher
        // alone does not guarantee it dies too (it can be left running as
        // an orphaned process). Explicitly stop it here so the panel's Stop
        // button always actually stops the Java server as well.
        cancellationToken.ThrowIfCancellationRequested();
        await StopProgram("java", Log.Logger, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        AnyOnline = false;
        Status = ServerStatus.Offline;
    }

    public async Task Restart(CancellationToken cancellationToken = default)
    {
        lock (_statusLock)
        {
            if (Status is ServerStatus.Stopping or ServerStatus.Offline)
            {
                return;
            }
        }

        await Stop(cancellationToken);

        lock (_statusLock)
        {
            if (StartLocked)
            {
                Log.Logger.Warning("Restart aborted because server start is locked after stop.");
                return;
            }
        }

        await Start(cancellationToken);
    }

    public async Task KillAll(CancellationToken cancellationToken = default)
    {
        lock (_statusLock)
        {
            cancellationToken = InitOperation(cancellationToken);
            Status = ServerStatus.Stopping;
        }

        foreach (var comp in Components)
        {
            cancellationToken.ThrowIfCancellationRequested();

            comp.Status = ServerStatus.Stopping;
            OnStatusChanged?.Invoke();

            await StopProgram(comp.ExeName, Log.Logger, cancellationToken);

            comp.Status = ServerStatus.Offline;
            OnStatusChanged?.Invoke();
        }

        // Same reasoning as in Stop(): "java" (the actual Minecraft server)
        // isn't a tracked Component and must be force-stopped explicitly,
        // otherwise KillAll can leave it running orphaned.
        cancellationToken.ThrowIfCancellationRequested();
        await StopProgram("java", Log.Logger, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        AnyOnline = false;
        Status = ServerStatus.Offline;
    }

    public void Dispose()
        => _operationTokenSource?.Dispose();

    private async Task StartInternal(Serilog.ILogger logger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = Settings.Instance;

        lock (_statusLock)
        {
            if (StartLocked)
            {
                logger.Warning("Start prevented because server start is locked.");
                Status = ServerStatus.Offline;
                return;
            }
        }

        if (!await FileChecker.CheckAsync(settings, false, logger, cancellationToken))
        {
            logger.Error("File validation failed");
            Status = ServerStatus.Offline;
            return;
        }

        RefreshComponentStatuses(preserveCurrentStatus: true);

        foreach (var comp in Components)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!comp.IsEnabled(settings))
            {
                comp.Status = ServerStatus.Offline;
                continue;
            }

            if (comp.Status is ServerStatus.Online)
            {
                logger.Debug($"{comp.Name} is already running.");
                continue;
            }

            // BuildplateLauncher: wait for shared server ready instead of starting duplicate
            if (comp.Name == "Buildplate Launcher" && comp.Status is ServerStatus.Starting)
            {
                logger.Debug($"{comp.Name} is starting (waiting for shared server)...");
                continue;
            }

            comp.Status = ServerStatus.Starting;
            OnStatusChanged?.Invoke();

            var process = comp.StartAction(settings, logger);
            if (comp.StartupDelayMs > 0)
            {
                await Task.Delay(comp.StartupDelayMs, cancellationToken);
            }

            if (await WaitForProcessStartAsync(comp.ExeName, cancellationToken))
            {
                comp.Status = ServerStatus.Online;
                AnyOnline = true;
            }
            else
            {
                comp.Status = ServerStatus.Offline;
                if (process is not null && process.HasExited)
                {
                    logger.Error($"{comp.Name} process exited immediately after launch.");
                }
                else
                {
                    logger.Error($"{comp.Name} failed to start.");
                }
            }

            OnStatusChanged?.Invoke();
        }

        await WaitForJavaServerReadyAsync(logger, cancellationToken);
        logger.Information("Waiting for programs to stabilize");
        await Task.Delay(7500, cancellationToken);

        bool error = false;
        foreach (var comp in Components)
        {
            if (!comp.IsEnabled(settings))
            {
                continue;
            }

            if (!ProcessUtils.GetProgramProcesses(comp.ExeName).Any())
            {
                logger.Error($"It was detected that {comp.Name} crashed/exited, make sure all options are set correctly, look into logs/{comp.Name}/logxxx for more info");
                comp.Status = ServerStatus.Offline;
                error = true;
            }
            else
            {
                comp.Status = ServerStatus.Online;
            }
        }

        RefreshComponentStatuses();

        if (!error)
        {
            logger.Information("All required programs have (most likely) running successfully");
        }
    }

    private bool IsJavaServerReady()
    {
        // The persistent Fabric server and bridge are plain "java" processes
        // managed by the Buildplate Launcher. A running java process only means
        // the server is booting; it is ready once its "Done ... For help" banner
        // appears in the Java Server logs. Each new capture session ("Capturing
        // Java process output") resets the marker so a restart goes back to
        // not-ready. Mirrors the heuristic on the Server Status page.
        if (!ProcessUtils.GetProgramProcesses("java").Any())
        {
            return false;
        }

        bool ready = false;
        foreach (var logEvent in _logsLogService.GetLogsFor("Java Server"))
        {
            if (logEvent.RenderedMessage?.Contains("Capturing Java process output") == true)
            {
                ready = false;
            }
            else if (logEvent.RenderedMessage?.Contains("For help, type") == true)
            {
                ready = true;
            }
        }

        return ready;
    }

    private async Task WaitForJavaServerReadyAsync(Serilog.ILogger logger, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var lastProgressLog = TimeSpan.Zero;
        bool sawJavaProcess = false;

        while (true)
        {
            if (IsJavaServerReady())
            {
                return;
            }

            if (!sawJavaProcess && ProcessUtils.GetProgramProcesses("java").Any())
            {
                sawJavaProcess = true;
            }

            // If the java process never even spawned within the first 60 s,
            // there is nothing to wait for.
            if (!sawJavaProcess && stopwatch.Elapsed >= TimeSpan.FromSeconds(60))
            {
                logger.Warning("Java server process did not start, not waiting for it to become ready");
                return;
            }

            if (stopwatch.Elapsed >= TimeSpan.FromSeconds(300))
            {
                logger.Warning("Timed out waiting for Java server to become ready");
                return;
            }

            if (stopwatch.Elapsed - lastProgressLog >= TimeSpan.FromSeconds(15))
            {
                logger.Debug("Still waiting for Java server to finish starting");
                lastProgressLog = stopwatch.Elapsed;
            }

            await Task.Delay(1000, cancellationToken);
        }
    }

    /// <summary>
    /// Asks Buildplate Launcher (over the event bus) to stop the Minecraft
    /// Fabric server gracefully. Best effort: on any failure or timeout we just
    /// log and return, so the caller always continues with the normal stop flow
    /// (which force kills the java processes) and Stop() never hangs.
    /// </summary>
    private static async Task TryStopFabricServerGracefully(Serilog.ILogger logger, CancellationToken cancellationToken)
    {
        // 25 s graceful stop on the launcher side + margin for bus round trips.
        // RequestAsync has no timeout parameter (its own internal cap is 90 s),
        // so the hard cap is enforced here with Task.WhenAny.
        var timeout = TimeSpan.FromSeconds(35);

        EventBusClient? eventBus = null;
        try
        {
            eventBus = await EventBusClient.ConnectAsync($"localhost:{Settings.Instance.EventBusPort}");
            var requestSender = await eventBus.AddRequestSenderAsync();
            Task<string?> requestTask = requestSender.RequestAsync("buildplates", "stopFabricServer", "{}");
            string? response = await Task.WhenAny(requestTask, Task.Delay(timeout, cancellationToken)) == requestTask
                ? await requestTask
                : null;
            if (response is null)
            {
                logger.Warning("Graceful Fabric server stop request failed or timed out, falling back to force kill");
                return;
            }

            logger.Information("Fabric server was stopped gracefully");
        }
        catch (OperationCanceledException)
        {
            logger.Warning("Graceful Fabric server stop request cancelled, falling back to force kill");
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Graceful Fabric server stop request failed, falling back to force kill");
        }
        finally
        {
            // DisposeAsync on the client (not on the request sender) is
            // intentional: disposing the sender first would FlushAsync() the
            // still pending response and block forever.
            if (eventBus is not null)
            {
                await eventBus.DisposeAsync();
            }
        }
    }

    private static async Task StopProgram(string name, Serilog.ILogger logger, CancellationToken cancellationToken)
    {
        logger.Information($"Stopping {name}");

        int stoppedCount = 0;
        foreach (var process in ProcessUtils.GetProgramProcesses(name))
        {
            await process.StopGracefullyOrKillAndWaitAsync(3000, cancellationToken);
            stoppedCount++;
        }

        logger.Information(stoppedCount switch
        {
            0 => $"No {name} processes found",
            1 => $"Stopped 1 {name} process",
            _ => $"Stopped {stoppedCount} {name} processes",
        });
    }

    private CancellationToken InitOperation(CancellationToken cancellationToken)
    {
        _operationTokenSource?.Cancel();
        _operationTokenSource = null;

        _operationTokenSource = new CancellationTokenSource();
        var combinedSource = CancellationTokenSource.CreateLinkedTokenSource(_operationTokenSource.Token, cancellationToken);
        return combinedSource.Token;
    }

    private sealed class StartLockHandle : IDisposable
    {
        private readonly ServerManager _manager;
        private int _disposed;

        public StartLockHandle(ServerManager manager)
        {
            _manager = manager;
            if (Interlocked.Increment(ref _manager._startLockCount) == 1)
            {
                _manager.OnStatusChanged?.Invoke();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var remaining = Interlocked.Decrement(ref _manager._startLockCount);
            if (remaining <= 0)
            {
                Interlocked.Exchange(ref _manager._startLockCount, 0);
                _manager.OnStatusChanged?.Invoke();
            }
        }
    }

    private sealed class PatchLockHandle : IDisposable
    {
        private readonly ServerManager _manager;
        private int _disposed;

        public PatchLockHandle(ServerManager manager)
        {
            _manager = manager;
            if (Interlocked.Increment(ref _manager._patchLockCount) == 1)
            {
                _manager.OnStatusChanged?.Invoke();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var remaining = Interlocked.Decrement(ref _manager._patchLockCount);
            if (remaining <= 0)
            {
                Interlocked.Exchange(ref _manager._patchLockCount, 0);
                _manager.OnStatusChanged?.Invoke();
            }
        }
    }
}

public enum ServerStatus
{
    Online = 0,
    Starting,
    Stopping,
    PartiallyOnline,
    Offline,
}
