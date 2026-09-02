using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Cyotek.Data.Nbt;
using Cyotek.Data.Nbt.Serialization;
using Serilog;
using Solace.Buildplate.Connector.Model;
using Solace.Common;
using Solace.Common.Utils;

namespace Solace.Buildplate.Launcher;

#pragma warning disable CA1001 // Types that own disposable fields should be disposable
public sealed class PersistentProcessManager
#pragma warning restore CA1001 // Types that own disposable fields should be disposable
{
#pragma warning disable CA1707 // Identifiers should not contain underscores
    public const int PERSISTENT_FABRIC_PORT = 25565;
    public const string PERSISTENT_QUEUE_NAME = "buildplate_persistent";
#pragma warning restore CA1707
    private const int BRIDGE_PORT = 19132;
    private const int CONTROL_CHANNEL_PORT = 25564;
    private const string PERSISTENT_BRIDGE_DIR_NAME = "vienna-buildplate-persistent-bridge";

    private readonly string _javaCmd;
    private readonly string _fabricJarName;
    private readonly DirectoryInfo _serverTemplateDir;
    private readonly string _fabricWorkDir;
    private readonly FileInfo _fountainBridgeJar;
    private readonly FileInfo _connectorPluginJar;
    private readonly string _eventBusConnectionString;
    private readonly string _publicAddress;

    private readonly string _connectorPluginArgString;

    private readonly ILogger _logger;

    private readonly ILogger _javaLogger;

    private ConsoleProcess? _fabricProcess;
    private ConsoleProcess? _bridgeProcess;
    private bool _shuttingDown;
    private readonly ReentrantAsyncLock.ReentrantAsyncLock _subprocessLock = new ReentrantAsyncLock.ReentrantAsyncLock(); // java uses ReentrantLock, Lock cannot be used, because it does not support locking and unlocking on different threads, which happens due to async, SemaphoreSlim does not support multiple locks from the same async context

    public bool IsFabricRunning => _fabricProcess is not null && _fabricProcess.ExitCode is null;
    public bool IsBridgeRunning => _bridgeProcess is not null && _bridgeProcess.ExitCode is null;

    public PersistentProcessManager(
        string javaCmd,
        string fabricJarName,
        DirectoryInfo serverTemplateDir,
        string fabricWorkDir,
        FileInfo fountainBridgeJar,
        FileInfo connectorPluginJar,
        string eventBusConnectionString,
        string publicAddress
    )
    {
        _javaCmd = javaCmd;
        _fabricJarName = fabricJarName;
        _serverTemplateDir = serverTemplateDir;
        _fabricWorkDir = fabricWorkDir;
        _fountainBridgeJar = fountainBridgeJar;
        _connectorPluginJar = connectorPluginJar;
        _eventBusConnectionString = eventBusConnectionString;
        _publicAddress = publicAddress;

        _connectorPluginArgString = Json.Serialize(new ConnectorPluginArg(
            eventBusConnectionString,
            PERSISTENT_QUEUE_NAME,
            InventoryType.SYNCED,
            $"127.0.0.1:{CONTROL_CHANNEL_PORT.ToString(CultureInfo.InvariantCulture)}"
        ));

        _logger = Log.Logger.ForContext("Component", nameof(PersistentProcessManager));

        // Logs the stdout/stderr of the Fabric server and bridge Java processes
        // under a separate component name so they show up as the "Java Server"
        // tab in the web UI live logs. ForContext overrides the global
        // ComponentName enrichment property ("BuildplateLauncher").
        _javaLogger = Log.Logger.ForContext("ComponentName", "Java Server");
    }

    public async Task StartFabricAsync()
    {
        await using (await _subprocessLock.LockAsync(CancellationToken.None))
        {
            if (_shuttingDown)
            {
                _logger.Debug("Already shutting down, not starting persistent Fabric server process");
                return;
            }

            if (_fabricProcess is not null)
            {
                if (_fabricProcess.ExitCode is null)
                {
                    _logger.Debug("Persistent Fabric server process has already been started");
                    return;
                }

                _logger.Warning($"Replacing terminated persistent Fabric server process (exit code {_fabricProcess.ExitCodeText})");
                _fabricProcess.Dispose();
                _fabricProcess = null;
            }

            _logger.Information($"Starting persistent Fabric server, public address {_publicAddress}");

            var workDir = new DirectoryInfo(_fabricWorkDir);
            if (!workDir.TryCreate())
            {
                _logger.Error("Could not create persistent Fabric server working directory");
                return;
            }

            if (!CopyServerFile(new FileInfo(Path.Combine(_serverTemplateDir.FullName, _fabricJarName)), new FileInfo(Path.Combine(workDir.FullName, _fabricJarName)), false))
            {
                _logger.Error("Fabric JAR {} does not exist in server template directory", _fabricJarName);
                return;
            }

            bool warnedMissingServerFiles = false;
            if (!CopyServerFile(new DirectoryInfo(Path.Combine(_serverTemplateDir.FullName, ".fabric", "server")), new DirectoryInfo(Path.Combine(workDir.FullName, ".fabric", "server")), true))
            {
                if (!warnedMissingServerFiles)
                {
                    _logger.Warning("Server files were not pre-downloaded in server template directory, it is recommended to pre-download all server files to improve start-up time and reduce network data usage");
                    warnedMissingServerFiles = true;
                }
            }

            if (!CopyServerFile(new DirectoryInfo(Path.Combine(_serverTemplateDir.FullName, "libraries")), new DirectoryInfo(Path.Combine(workDir.FullName, "libraries")), true))
            {
                if (!warnedMissingServerFiles)
                {
                    _logger.Warning("Server files were not pre-downloaded in server template directory, it is recommended to pre-download all server files to improve start-up time and reduce network data usage");
                    warnedMissingServerFiles = true;
                }
            }

            if (!CopyServerFile(new DirectoryInfo(Path.Combine(_serverTemplateDir.FullName, "versions")), new DirectoryInfo(Path.Combine(workDir.FullName, "versions")), true))
            {
                if (!warnedMissingServerFiles)
                {
                    _logger.Warning("Server files were not pre-downloaded in server template directory, it is recommended to pre-download all server files to improve start-up time and reduce network data usage");
#pragma warning disable IDE0059 // Unnecessary assignment of a value
                    warnedMissingServerFiles = true;
#pragma warning restore IDE0059 // Unnecessary assignment of a value
                }
            }

            if (!CopyServerFile(new DirectoryInfo(Path.Combine(_serverTemplateDir.FullName, "mods")), new DirectoryInfo(Path.Combine(workDir.FullName, "mods")), true))
            {
                _logger.Error("Mods directory was not present in server template directory, the persistent buildplate server instance will not function correctly without the Fountain and Vienna Fabric mods installed");
            }

            await File.WriteAllTextAsync(Path.Combine(workDir.FullName, "eula.txt"), "eula=true");

            string serverProperties = new StringBuilder()
                .Append("online-mode=false\n")
                .Append("enforce-secure-profile=false\n")
                .Append("sync-chunk-writes=false\n")
                .Append("spawn-protection=0\n")
                .Append("enable-command-block=true\n")
                .Append(CultureInfo.InvariantCulture, $"server-port={PERSISTENT_FABRIC_PORT.ToString(CultureInfo.InvariantCulture)}\n")
                .Append("gamemode=creative\n")
                .Append(CultureInfo.InvariantCulture, $"vienna-event-bus-address={_eventBusConnectionString}\n")
                .Append(CultureInfo.InvariantCulture, $"vienna-event-bus-queue-name={PERSISTENT_QUEUE_NAME}\n")
                .ToString();
            await File.WriteAllTextAsync(Path.Combine(workDir.FullName, "server.properties"), serverProperties);

            var worldDir = new DirectoryInfo(Path.Combine(workDir.FullName, "world"));
            if (!worldDir.TryCreate())
            {
                _logger.Error("Could not create persistent server world directory");
                return;
            }

            var worldEntitiesDir = new DirectoryInfo(Path.Combine(worldDir.FullName, "entities"));
            if (!worldEntitiesDir.TryCreate())
            {
                _logger.Error("Could not create persistent server world entities directory");
                return;
            }

            var worldRegionDir = new DirectoryInfo(Path.Combine(worldDir.FullName, "region"));
            if (!worldRegionDir.TryCreate())
            {
                _logger.Error("Could not create persistent server world regions directory");
                return;
            }

            // The default overworld uses the fountain:empty (100% air) generator,
            // so any chunks left over on disk from a previous start (generated with
            // the old noise generator) would keep the old terrain alive forever.
            // Delete the stale overworld chunk data so the empty world takes
            // effect. Instance dimension data (world/dimensions/fountain) and
            // player data are preserved.
            DeleteStaleOverworldChunks(worldDir);

            TagCompound levelDatTag = CreateLevelDat();
            using (var fs = new FileStream(Path.Combine(worldDir.FullName, "level.dat"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
            using (var gzs = new GZipStream(fs, CompressionLevel.Optimal))
            {
                var writer = new BinaryTagWriter(gzs);
                writer.WriteStartDocument();
                writer.WriteStartTag(null, TagType.Compound);
                writer.WriteTag(levelDatTag);
                writer.WriteEndTag();
                writer.WriteEndDocument();
            }

            _logger.Information("Starting persistent Fabric server process");

            try
            {
                _fabricProcess = new ConsoleProcess(_javaCmd, useShellExecute: false, redirect: true, openInNewWindow: false);
                AttachJavaOutputLogging(_fabricProcess, "Fabric");
                await _fabricProcess.ExecuteAsync(workDir.FullName, [$"-Dfountain.control.port={CONTROL_CHANNEL_PORT.ToString(CultureInfo.InvariantCulture)}", "-jar", _fabricJarName, "-nogui"]);

                _logger.Information($"Persistent Fabric server process started, PID {_fabricProcess.Id}");
            }
            catch (IOException exception)
            {
                _logger.Error(exception, "Could not start persistent Fabric server process");
            }
        }
    }

    public async Task StartBridgeAsync()
    {
        await using (await _subprocessLock.LockAsync(CancellationToken.None))
        {
            if (_shuttingDown)
            {
                _logger.Debug("Already shutting down, not starting persistent bridge process");
                return;
            }

            if (_bridgeProcess is not null)
            {
                if (_bridgeProcess.ExitCode is null)
                {
                    _logger.Debug("Persistent bridge process has already been started");
                    return;
                }

                _logger.Warning($"Replacing terminated persistent bridge process (exit code {_bridgeProcess.ExitCodeText})");
                _bridgeProcess.Dispose();
                _bridgeProcess = null;
            }

            _logger.Information("Starting persistent bridge process");

            var bridgeWorkDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), PERSISTENT_BRIDGE_DIR_NAME));
            if (!bridgeWorkDir.TryCreate())
            {
                _logger.Error("Could not create persistent bridge working directory");
                return;
            }

            try
            {
                _bridgeProcess = new ConsoleProcess(_javaCmd, useShellExecute: false, redirect: true, openInNewWindow: false);
                AttachJavaOutputLogging(_bridgeProcess, "Bridge");
                _bridgeProcess.ProcessExited += (sender, e) =>
                {
                    Task.Run(async () =>
                    {
                        await using (await _subprocessLock.LockAsync(CancellationToken.None))
                        {
                            if (!_shuttingDown)
                            {
                                _logger.Warning($"Persistent bridge process has unexpectedly terminated with exit code {_bridgeProcess.ExitCodeText}");
                                _bridgeProcess.Dispose();
                                _bridgeProcess = null;
                                BeginShutdown();
                            }
                        }
                    }).Forget();
                };

                await _bridgeProcess.ExecuteAsync(bridgeWorkDir.FullName,
                [
                    "-jar", _fountainBridgeJar.FullName,
                    "-port", BRIDGE_PORT.ToString(CultureInfo.InvariantCulture),
                    "-serverAddress", "127.0.0.1",
                    "-serverPort", PERSISTENT_FABRIC_PORT.ToString(CultureInfo.InvariantCulture),
                    "-connectorPluginJar", _connectorPluginJar.FullName,
                    "-connectorPluginClass", "micheal65536.vienna.buildplate.connector.plugin.ViennaConnectorPlugin",
                    "-connectorPluginArg", _connectorPluginArgString,
                    "-useUUIDAsUsername",
                ]);

                _logger.Information($"Persistent bridge process started, PID {_bridgeProcess.Id}");
            }
            catch (IOException exception)
            {
                _logger.Error(exception, "Could not start persistent bridge process");
            }
        }
    }

    /// <summary>
    /// Redirects the stdout/stderr of a Java subprocess into the "Java Server"
    /// log component so it is visible in the web UI live logs.
    /// </summary>
    private void AttachJavaOutputLogging(ConsoleProcess process, string prefix)
    {
        _javaLogger.Information("[{Prefix}] Capturing Java process output", prefix);
        process.StandartTextReceived += (sender, e) => LogJavaLine(prefix, e.Data);
        process.ErrorTextReceived += (sender, e) => LogJavaLine(prefix, e.Data);
    }

    private void LogJavaLine(string prefix, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        // Java log lines are already formatted (timestamp, level, logger
        // name), so they are passed through as-is at information level.
        _javaLogger.Information("[{Prefix}] {Line}", prefix, line.TrimEnd());
    }

    /// <summary>
    /// Stops the persistent Fabric server gracefully: writes <c>stop</c> to its
    /// console (stdin) so it saves all worlds, then waits up to 25 seconds for
    /// it to exit by itself. If it is still running after that, it is killed.
    /// </summary>
    public async Task StopFabricAsync(CancellationToken cancellationToken = default)
    {
        // Deliberately no _subprocessLock here: StartFabricAsync holds it while
        // copying the whole server template (can take a long time), which would
        // eat into the 25 second grace period. Capturing the field reference is
        // enough - writing "stop" to a replaced process's stdin is harmless.
        ConsoleProcess? fabricProcess = _fabricProcess;
        if (fabricProcess is null || fabricProcess.ExitCode is not null)
        {
            _logger.Information("Persistent Fabric server is not running, nothing to stop");
            return;
        }

        _logger.Information("Sending 'stop' to the Fabric server console");

        try
        {
            fabricProcess.WriteLine("stop");
        }
        catch (Exception exception)
        {
            // stdin may already be broken (pipe closed etc.) - the wait/kill
            // path below still applies.
            _logger.Warning(exception, "Could not write 'stop' to the Fabric server console, falling back to the grace period wait");
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(25));
            await fabricProcess.WaitForExitAsync(timeoutCts.Token);

            _logger.Information($"Persistent Fabric server stopped gracefully with exit code {fabricProcess.ExitCodeText}");
        }
        catch (OperationCanceledException)
        {
            _logger.Warning("Persistent Fabric server did not exit within 25 s of the 'stop' console command, killing it");
            // A short kill timeout keeps the whole operation well below the
            // caller's overall budget (grace 25 s + kill 5 s < 35 s).
            await fabricProcess.StopNoWaitAsync(5 * 1000, cancellationToken);
        }
    }

    public async Task StopAllAsync()
    {
        var @lock = await _subprocessLock.LockAsync(CancellationToken.None);

        if (_shuttingDown)
        {
            _logger.Debug("Already shutting down, not stopping persistent processes again");
            await @lock.DisposeAsync();
            return;
        }

        _shuttingDown = true;

        _logger.Information("Beginning shutdown of persistent processes");

        if (_bridgeProcess is not null)
        {
            _logger.Information("Waiting for persistent bridge to shut down");
            await @lock.DisposeAsync();
            await _bridgeProcess.StopAndWaitAsync();
            var bridgeExitCode = _bridgeProcess.ExitCodeText;
            @lock = await _subprocessLock.LockAsync(CancellationToken.None);
            _bridgeProcess.Dispose();
            _bridgeProcess = null;
            _logger.Information($"Persistent bridge has finished with exit code {bridgeExitCode}");
        }

        if (_fabricProcess is not null)
        {
            _logger.Information("Asking persistent Fabric server to shut down");
            ConsoleProcess fabricProcess = _fabricProcess;
            fabricProcess.ProcessExited += (sender, e) =>
            {
                _logger.Information($"Persistent Fabric server has finished with exit code {fabricProcess.ExitCodeText}");
                fabricProcess.Dispose();
            };
            await fabricProcess.StopNoWaitAsync();
            if (fabricProcess.ExitCode is not null)
            {
                _logger.Information($"Persistent Fabric server has already exited with exit code {fabricProcess.ExitCodeText}");
                fabricProcess.Dispose();
            }

            _fabricProcess = null;
        }

        await @lock.DisposeAsync();
    }

    private void BeginShutdown()
        => StopAllAsync().Forget();

    private static bool CopyServerFile(FileSystemInfo src, FileSystemInfo dst, bool directory)
    {
        if (!src.Exists)
        {
            return false;
        }

        if (directory)
        {
            ((DirectoryInfo)src).CopyTo(dst.FullName);
        }
        else
        {
            ((FileInfo)src).CopyTo(dst.FullName, true);
        }

        return true;
    }

    /// <summary>
    /// Deletes stale chunk data of the default overworld (generated by the
    /// previous noise-based generator) so the 100% air <c>fountain:empty</c>
    /// world takes effect on an existing persistent server. Also removes the
    /// data directories of the removed vanilla dimensions (the Nether / the
    /// End). Instance dimensions (<c>world/dimensions/fountain</c>) and player
    /// data are preserved.
    /// </summary>
    private void DeleteStaleOverworldChunks(DirectoryInfo worldDir)
    {
        foreach (string chunkDirName in new[] { "region", "entities" })
        {
            var chunkDir = new DirectoryInfo(Path.Combine(worldDir.FullName, chunkDirName));
            if (!chunkDir.Exists)
            {
                continue;
            }

            foreach (var chunkFile in chunkDir.EnumerateFiles("*.mca"))
            {
                try
                {
                    chunkFile.Delete();
                    _logger.Debug("Deleted stale overworld chunk file {ChunkFile}", chunkFile.FullName);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _logger.Warning(exception, "Could not delete stale overworld chunk file {ChunkFile}", chunkFile.FullName);
                }
            }
        }

        // Chunk data of the removed vanilla dimensions lives under
        // world/dimensions/minecraft (only secondary vanilla dimensions are
        // stored there, the overworld itself lives at the world root). Instance
        // dimensions live under world/dimensions/fountain and are preserved.
        var vanillaDimensionsDir = new DirectoryInfo(Path.Combine(worldDir.FullName, "dimensions", "minecraft"));
        if (vanillaDimensionsDir.Exists)
        {
            try
            {
                vanillaDimensionsDir.Delete(recursive: true);
                _logger.Debug("Deleted removed vanilla dimension data {VanillaDimensionsDir}", vanillaDimensionsDir.FullName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.Warning(exception, "Could not delete removed vanilla dimension data {VanillaDimensionsDir}", vanillaDimensionsDir.FullName);
            }
        }
    }

    private static TagCompound CreateLevelDat()
    {
        TagCompound dataTag = new NbtBuilder.Compound()
            .Add("GameType", 1)
            .Add("Difficulty", 1)
            .Add("DayTime", 6000)
            .Add("GameRules", new NbtBuilder.Compound()
                .Add("doDaylightCycle", "false")
                .Add("doWeatherCycle", "false")
                .Add("doMobSpawning", "false")
                .Add("fountain:doMobDespawn", "false")
                .Add("keepInventory", "true")
            )
            .Add("WorldGenSettings", new NbtBuilder.Compound()
                .Add("seed", (long)0)
                .Add("generate_features", (byte)0)
                .Add("dimensions", new NbtBuilder.Compound()
                    // The persistent server's default overworld is only a staging
                    // area (instances get their own dimensions via createInstance),
                    // so it uses the fountain:empty generator: 100% air chunks,
                    // no noise terrain to generate at startup. The Nether and the
                    // End are intentionally NOT listed here, so those dimensions
                    // are never created.
                    .Add("minecraft:overworld", new NbtBuilder.Compound()
                        .Add("type", "minecraft:overworld")
                        .Add("generator", new NbtBuilder.Compound()
                            .Add("type", "fountain:empty")
                            .Add("size", 32)
                            .Add("ground_level", 63)
                            .Add("underground_height", 16)
                            // minecraft:plains is present in the bridge's Java->Bedrock
                            // biome mapping (registry/biomes_bedrock.json)
                            .Add("biome", "minecraft:plains")
                        )
                    )
                )
            )
            .Add("DataVersion", 3700)
            .Add("version", 19133)
            .Add("Version", new NbtBuilder.Compound()
                .Add("Id", 3700)
                .Add("Name", "1.20.4")
                .Add("Series", "main")
                .Add("Snapshot", (byte)0)
            )
            .Add("initialized", (byte)1)
            .Build("Data");

        return dataTag;
    }
}
