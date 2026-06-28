using System.Collections.Concurrent;
using System.Globalization;
using Serilog;
using Solace.Common;
using Solace.Common.Utils;

namespace Solace.Buildplate.Launcher;

/// <summary>
/// One persistent Fabric server hosting all buildplates as dimensions.
/// Uses RCON to manage dimensions — no Java plugin changes needed.
/// </summary>
public sealed class SharedFabricServer : IDisposable
{
    private readonly string _javaCmd;
    private readonly DirectoryInfo _serverTemplateDir;
    private readonly string _fabricJarName;
    private readonly string _eventBusAddress;
    private readonly ILogger _logger;

    private readonly DirectoryInfo _serverWorkDir;
    private ConsoleProcess? _serverProcess;
    private MinecraftRconClient? _rcon;
    private bool _rconReady;
    private readonly Lock _lock = new();
    private bool _started;
    private bool _disposed;

    private readonly ConcurrentDictionary<string, DimensionInfo> _dimensions = new();

    public int ServerPort { get; }
    public int RconPort { get; }
    public DirectoryInfo ServerWorkDir => _serverWorkDir;
    public bool IsRunning => _serverProcess is not null && !_serverProcess.Process.HasExited;
    public bool IsReady => _serverProcess is not null && !_serverProcess.Process.HasExited && _rconReady;

    public SharedFabricServer(
        string javaCmd,
        DirectoryInfo serverTemplateDir,
        string fabricJarName,
        string eventBusAddress,
        ILogger logger,
        int serverPort = 25566,
        int rconPort = 25575)
    {
        _javaCmd = javaCmd;
        _serverTemplateDir = serverTemplateDir;
        _fabricJarName = fabricJarName;
        _eventBusAddress = eventBusAddress;
        _logger = logger;
        ServerPort = serverPort;
        RconPort = rconPort;

        _serverWorkDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "apace-shared-server"));
    }

    public async Task StartAsync()
    {
        lock (_lock)
        {
            if (_started || _disposed) return;
            _started = true;
        }

        _logger.Information("Setting up shared Fabric server on port {Port} (RCON: {Rcon})", ServerPort, RconPort);

        _serverWorkDir.TryCreate();

        // Copy server files from template
        CopyServerFiles();

        // Create server.properties
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("online-mode=false");
        sb.AppendLine("enforce-secure-profile=false");
        sb.AppendLine("sync-chunk-writes=false");
        sb.AppendLine("spawn-protection=0");
        sb.AppendLine("enable-command-block=true");
        sb.AppendLine(CultureInfo.InvariantCulture, $"server-port={ServerPort}");
        sb.AppendLine("gamemode=creative");
        // Flat world for instant startup (no overworld generation)
        sb.AppendLine("level-type=minecraft:flat");
        sb.AppendLine("level-name=world");
        sb.AppendLine(CultureInfo.InvariantCulture, $"enable-rcon=true");
        sb.AppendLine(CultureInfo.InvariantCulture, $"rcon.port={RconPort}");
        sb.AppendLine("rcon.password=apace");
        sb.AppendLine(CultureInfo.InvariantCulture, $"vienna-event-bus-address={_eventBusAddress}");
        sb.AppendLine("vienna-event-bus-queue-name=buildplate_server");
        await File.WriteAllTextAsync(Path.Combine(_serverWorkDir.FullName, "server.properties"), sb.ToString());
        await File.WriteAllTextAsync(Path.Combine(_serverWorkDir.FullName, "eula.txt"), "eula=true");

        // Pre-create dimension directories
        var dimsDir = Path.Combine(_serverWorkDir.FullName, "world", "dimensions", "apace");
        Directory.CreateDirectory(dimsDir);

        // Start server with redirect to capture Minecraft output for panel logs
        _serverProcess = new ConsoleProcess(_javaCmd, useShellExecute: false, redirect: true, openInNewWindow: false);
        _serverProcess.StandartTextReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                _logger.Information("[server] {Line}", e.Data);
        };
        _serverProcess.ErrorTextReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                _logger.Error("[server-err] {Line}", e.Data);
        };
        await _serverProcess.ExecuteAsync(_serverWorkDir.FullName, ["-jar", _fabricJarName, "-nogui"]);

        // Wait for server to be ready — poll the game port instead of RCON
        // Fabric takes 1-5 min on first start (world gen), 20-30s on subsequent
        _logger.Information("Waiting for Fabric server on port {Port}...", ServerPort);

        bool portReady = false;
        for (int attempt = 0; attempt < 150; attempt++) // up to 5 minutes
        {
            await Task.Delay(2000);
            if (_serverProcess.Process.HasExited)
            {
                _logger.Error("Fabric server exited during startup");
                return;
            }
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                await tcp.ConnectAsync("127.0.0.1", ServerPort);
                portReady = true;
                break;
            }
            catch
            {
                // Server not listening yet
            }
        }

        _portReady = portReady;

        if (!portReady)
        {
            _logger.Error("Fabric server port {Port} not reachable after 5 minutes", ServerPort);
            return;
        }

        // Now try RCON for dimension management (non-critical)
        _rcon = new MinecraftRconClient("127.0.0.1", RconPort, "apace", _logger);
        for (int attempt = 0; attempt < 15; attempt++)
        {
            await Task.Delay(1000);
            if (await _rcon.ConnectAsync())
            {
                _rconReady = true;
                break;
            }
        }

        if (!_rconReady)
            _logger.Warning("RCON not available — dimension teleport will use files only");
        else
            _logger.Information("RCON connected — full dimension management available");

        _logger.Information("Shared Fabric server ready — accepting buildplate dimensions");
    }

    /// <summary>
    /// Register a new buildplate dimension. Sets up world files and notifies server via RCON.
    /// </summary>
    public async Task<string?> CreateBuildplateDimensionAsync(
        string instanceId, string? playerId, string buildplateId,
        byte[] serverData, bool survival, bool night)
    {
        var dimId = $"bp_{instanceId.Replace("-", "")[..8]}";
        _logger.Information("CreateBuildplateDimensionAsync: dimId={DimId}, serverData={DataLen} bytes", dimId, serverData?.Length ?? 0);
        var dimDir = Path.Combine(_serverWorkDir.FullName, "world", "dimensions", "apace", dimId);

        try
        {
            Directory.CreateDirectory(dimDir);
            Directory.CreateDirectory(Path.Combine(dimDir, "region"));
            Directory.CreateDirectory(Path.Combine(dimDir, "entities"));

            // Extract buildplate world data into dimension directory
            using (var ms = new MemoryStream(serverData))
            using (var zip = new System.IO.Compression.ZipArchive(ms))
            {
                foreach (var entry in zip.Entries)
                {
                    var destPath = Path.Combine(dimDir, entry.FullName);
                    var destParent = Path.GetDirectoryName(destPath);
                    if (destParent is not null)
                        Directory.CreateDirectory(destParent);
                    using (var entryStream = entry.Open())
                    using (var fileStream = File.Create(destPath))
                        entryStream.CopyTo(fileStream);
                }
            }

            _dimensions[dimId] = new DimensionInfo(instanceId, buildplateId, playerId, dimId);
            _logger.Information("Buildplate dimension {DimId} created for instance {InstanceId}", dimId, instanceId);

            return dimId;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to create dimension {DimId}", dimId);
            return null;
        }
    }

    /// <summary>
    /// Teleport a player to their buildplate dimension.
    /// </summary>
    public async Task<bool> SendPlayerToDimensionAsync(string playerName, string dimensionId)
    {
        if (_rcon is null) return false;
        var result = await _rcon.SendCommandAsync($"execute in apace:{dimensionId} run tp {playerName} 0.5 65 0.5");
        return result is not null;
    }

    public void RemoveDimension(string dimensionId)
    {
        _dimensions.TryRemove(dimensionId, out _);
    }

    public int DimensionCount => _dimensions.Count;

    public async Task StopAsync()
    {
        lock (_lock) { if (_disposed) return; _disposed = true; }
        _rcon?.Dispose();
        if (_serverProcess is not null && !_serverProcess.Process.HasExited)
        {
            await _serverProcess.StopAndWaitAsync();
            _serverProcess.Dispose();
            _serverProcess = null;
        }
    }

    public void Dispose() => _rcon?.Dispose();

    private void CopyServerFiles()
    {
        CopyIfExists(Path.Combine(_serverTemplateDir.FullName, _fabricJarName),
            Path.Combine(_serverWorkDir.FullName, _fabricJarName), true);
        CopyIfExists(Path.Combine(_serverTemplateDir.FullName, ".fabric"),
            Path.Combine(_serverWorkDir.FullName, ".fabric"));
        CopyIfExists(Path.Combine(_serverTemplateDir.FullName, "libraries"),
            Path.Combine(_serverWorkDir.FullName, "libraries"));
        CopyIfExists(Path.Combine(_serverTemplateDir.FullName, "versions"),
            Path.Combine(_serverWorkDir.FullName, "versions"));
        CopyIfExists(Path.Combine(_serverTemplateDir.FullName, "mods"),
            Path.Combine(_serverWorkDir.FullName, "mods"));
    }

    private static void CopyIfExists(string src, string dst, bool isFile = false)
    {
        if (isFile) { if (File.Exists(src)) File.Copy(src, dst, true); }
        else { if (Directory.Exists(src)) new DirectoryInfo(src).CopyTo(dst); }
    }

    public sealed record DimensionInfo(string InstanceId, string BuildplateId, string? PlayerId, string DimensionId);
}
