using System.Collections.Concurrent;
using System.Globalization;
using Serilog;
using Solace.Common;
using Solace.Common.Utils;

namespace Solace.Buildplate.Launcher;

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
    private readonly Lock _lock = new();
    private bool _started, _disposed, _portReady;
    private readonly ConcurrentDictionary<string, DimensionInfo> _dimensions = new();
    private int _nextDimIndex;

    public int ServerPort { get; }
    public int RconPort { get; }
    public DirectoryInfo ServerWorkDir => _serverWorkDir;
    public bool IsRunning => _serverProcess is not null && !_serverProcess.Process.HasExited;
    public bool IsReady => _portReady;

    public SharedFabricServer(string javaCmd, DirectoryInfo serverTemplateDir, string fabricJarName,
        string eventBusAddress, ILogger logger, int serverPort = 25566, int rconPort = 25575)
    {
        _javaCmd = javaCmd; _serverTemplateDir = serverTemplateDir; _fabricJarName = fabricJarName;
        _eventBusAddress = eventBusAddress; _logger = logger; ServerPort = serverPort; RconPort = rconPort;
        _serverWorkDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "apace-shared-server"));
    }

    public async Task StartAsync()
    {
        lock (_lock) { if (_started || _disposed) return; _started = true; }
        _logger.Information("Setting up shared Fabric server on port {Port} ({Count} dimensions)", ServerPort, 50);
        _serverWorkDir.TryCreate();
        CopyServerFiles();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("online-mode=false"); sb.AppendLine("enforce-secure-profile=false");
        sb.AppendLine("sync-chunk-writes=false"); sb.AppendLine("spawn-protection=0");
        sb.AppendLine("enable-command-block=true");
        sb.AppendLine(CultureInfo.InvariantCulture, $"server-port={ServerPort}");
        sb.AppendLine("gamemode=creative"); sb.AppendLine("level-type=minecraft:flat");
        sb.AppendLine("level-name=world");
        sb.AppendLine("enable-rcon=true"); sb.AppendLine(CultureInfo.InvariantCulture, $"rcon.port={RconPort}");
        sb.AppendLine("rcon.password=apace");
        sb.AppendLine(CultureInfo.InvariantCulture, $"vienna-event-bus-address={_eventBusAddress}");
        sb.AppendLine("vienna-event-bus-queue-name=buildplate_server");
        await File.WriteAllTextAsync(Path.Combine(_serverWorkDir.FullName, "server.properties"), sb.ToString());
        await File.WriteAllTextAsync(Path.Combine(_serverWorkDir.FullName, "eula.txt"), "eula=true");

        // Pre-create 50 void dimensions (registered via datapack at server start)
        var datapackDir = Path.Combine(_serverWorkDir.FullName, "world", "datapacks", "apace");
        var dimDefDir = Path.Combine(datapackDir, "data", "apace", "dimension");
        Directory.CreateDirectory(dimDefDir);
        await File.WriteAllTextAsync(Path.Combine(datapackDir, "pack.mcmeta"),
            "{\"pack\":{\"pack_format\":22,\"description\":\"Apace buildplate dimensions\"}}");

        const string dimJson = "{\"type\":\"minecraft:overworld\",\"generator\":{\"type\":\"minecraft:flat\",\"settings\":{\"layers\":[{\"block\":\"minecraft:air\",\"height\":1}],\"biome\":\"minecraft:the_void\"}}}";
        for (int i = 0; i < 50; i++)
        {
            var dimDir = Path.Combine(_serverWorkDir.FullName, "world", "dimensions", "apace", $"bp_{i}");
            Directory.CreateDirectory(Path.Combine(dimDir, "region"));
            Directory.CreateDirectory(Path.Combine(dimDir, "entities"));
            await File.WriteAllTextAsync(Path.Combine(dimDefDir, $"bp_{i}.json"), dimJson);
        }
        _logger.Information("Pre-created 50 void buildplate dimensions");

        _serverProcess = new ConsoleProcess(_javaCmd, useShellExecute: false, redirect: true, openInNewWindow: false);
        _serverProcess.StandartTextReceived += (_, e) =>
        { if (!string.IsNullOrWhiteSpace(e.Data)) _logger.Information("[server] {Line}", e.Data); };
        _serverProcess.ErrorTextReceived += (_, e) =>
        { if (!string.IsNullOrWhiteSpace(e.Data)) _logger.Error("[server-err] {Line}", e.Data); };
        await _serverProcess.ExecuteAsync(_serverWorkDir.FullName, ["-jar", _fabricJarName, "-nogui"]);

        _logger.Information("Waiting for Fabric server on port {Port}...", ServerPort);
        for (int a = 0; a < 150; a++)
        {
            await Task.Delay(2000);
            if (_serverProcess.Process.HasExited) { _logger.Error("Fabric server crashed during startup"); return; }
            try { using var t = new System.Net.Sockets.TcpClient(); await t.ConnectAsync("127.0.0.1", ServerPort); _portReady = true; break; } catch { }
        }
        if (!_portReady) { _logger.Error("Port {Port} not reachable", ServerPort); return; }
        File.WriteAllText("/tmp/apace-server-ready", DateTime.UtcNow.ToString("O"));

        _rcon = new MinecraftRconClient("127.0.0.1", RconPort, "apace", _logger);
        for (int a = 0; a < 60; a++)
        {
            await Task.Delay(1000);
            if (await _rcon.ConnectAsync()) { _logger.Information("RCON connected"); break; }
            if (a == 0) _logger.Information("Waiting for RCON...");
        }
        _logger.Information("Shared Fabric server ready — accepting buildplate dimensions");
    }

    public async Task<string?> CreateBuildplateDimensionAsync(string instanceId, string? playerId,
        string buildplateId, byte[] serverData, bool survival, bool night)
    {
        var dimId = $"bp_{_dimensions.Count}";
        if (_dimensions.Count >= 50) { _logger.Error("All 50 buildplate dimensions in use"); return null; }
        _logger.Information("Buildplate {DimId} for instance {InstanceId} ({DataLen} bytes)", dimId, instanceId, serverData?.Length ?? 0);

        var dimDir = Path.Combine(_serverWorkDir.FullName, "world", "dimensions", "apace", dimId);
        try
        {
            using (var ms = new MemoryStream(serverData))
            using (var zip = new System.IO.Compression.ZipArchive(ms))
            {
                foreach (var entry in zip.Entries)
                {
                    var destPath = Path.Combine(dimDir, entry.FullName);
                    var parent = Path.GetDirectoryName(destPath);
                    if (parent is not null) Directory.CreateDirectory(parent);
                    using var es = entry.Open(); using var fs = File.Create(destPath); es.CopyTo(fs);
                }
            }
            _dimensions[dimId] = new DimensionInfo(instanceId, buildplateId, playerId, dimId);
            _logger.Information("Buildplate {DimId} ready", dimId);
            return dimId;
        }
        catch (Exception ex) { _logger.Error(ex, "Failed to create buildplate {DimId}", dimId); return null; }
    }

    public async Task<bool> SendPlayerToDimensionAsync(string playerId, string dimensionId)
    {
        if (_rcon is null) return false;
        var r = await _rcon.SendCommandAsync($"execute in apace:{dimensionId} run tp {playerId} 0 65 0");
        return r is not null;
    }

    public void RemoveDimension(string id) => _dimensions.TryRemove(id, out _);
    public int DimensionCount => _dimensions.Count;

    public async Task StopAsync()
    {
        lock (_lock) { if (_disposed) return; _disposed = true; }
        _rcon?.Dispose();
        if (_serverProcess is not null && !_serverProcess.Process.HasExited)
        { await _serverProcess.StopAndWaitAsync(); _serverProcess.Dispose(); _serverProcess = null; }
    }

    public void Dispose() { _rcon?.Dispose(); _serverProcess?.Dispose(); }

    private void CopyServerFiles()
    {
        Copy(Path.Combine(_serverTemplateDir.FullName, _fabricJarName), Path.Combine(_serverWorkDir.FullName, _fabricJarName), true);
        Copy(Path.Combine(_serverTemplateDir.FullName, ".fabric"), Path.Combine(_serverWorkDir.FullName, ".fabric"));
        Copy(Path.Combine(_serverTemplateDir.FullName, "libraries"), Path.Combine(_serverWorkDir.FullName, "libraries"));
        Copy(Path.Combine(_serverTemplateDir.FullName, "versions"), Path.Combine(_serverWorkDir.FullName, "versions"));
        Copy(Path.Combine(_serverTemplateDir.FullName, "mods"), Path.Combine(_serverWorkDir.FullName, "mods"));
    }
    private static void Copy(string s, string d, bool f = false)
    { if (f) { if (File.Exists(s)) File.Copy(s, d, true); } else { if (Directory.Exists(s)) new DirectoryInfo(s).CopyTo(d); } }

    public sealed record DimensionInfo(string InstanceId, string BuildplateId, string? PlayerId, string DimensionId);
}
