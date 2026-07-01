using System.Collections.Concurrent;
using System.Globalization;
using Serilog;
using Solace.Common;
using Solace.Common.Utils;

namespace Solace.Buildplate.Launcher;

/// <summary>
/// One persistent Fabric server. Buildplates placed in overworld at far X offsets
/// where server hasn't pre-generated chunks — ensures fresh disk reads.
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
    private readonly Lock _lock = new();
    private bool _started, _disposed, _portReady;
    private readonly ConcurrentDictionary<string, OffsetInfo> _offsets = new();
    private int _nextX = 10240; // Far from spawn — outside spawn chunks (spawn = ~300 blocks, never unloaded)

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
        _logger.Information("Setting up shared Fabric server (overworld offset mode) on port {Port}", ServerPort);
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
        Directory.CreateDirectory(Path.Combine(_serverWorkDir.FullName, "world"));

        // Pre-create 50 void dimensions with datapack (server loads them at startup)
        var dpDir = Path.Combine(_serverWorkDir.FullName, "world", "datapacks", "apace");
        var dimDefDir = Path.Combine(dpDir, "data", "apace", "dimension");
        Directory.CreateDirectory(dimDefDir);
        await File.WriteAllTextAsync(Path.Combine(dpDir, "pack.mcmeta"),
            "{\"pack\":{\"pack_format\":22,\"description\":\"Apace\"}}");
        var dimJson = "{\"type\":\"minecraft:overworld\",\"generator\":{\"type\":\"minecraft:flat\",\"settings\":{\"layers\":[{\"block\":\"minecraft:air\",\"height\":1}],\"biome\":\"minecraft:the_void\"}}}";
        for (int i = 0; i < 50; i++)
        {
            var dd = Path.Combine(_serverWorkDir.FullName, "world", "dimensions", "apace", $"bp_{i}");
            Directory.CreateDirectory(Path.Combine(dd, "region"));
            Directory.CreateDirectory(Path.Combine(dd, "entities"));
            await File.WriteAllTextAsync(Path.Combine(dimDefDir, $"bp_{i}.json"), dimJson);
        }

        _serverProcess = new ConsoleProcess(_javaCmd, useShellExecute: false, redirect: true, openInNewWindow: false);
        _serverProcess.StandartTextReceived += (_, e) =>
        { if (!string.IsNullOrWhiteSpace(e.Data)) _logger.Information("[server] {Line}", e.Data); };
        _serverProcess.ErrorTextReceived += (_, e) =>
        { if (!string.IsNullOrWhiteSpace(e.Data)) _logger.Error("[server-err] {Line}", e.Data); };
        await _serverProcess.ExecuteAsync(_serverWorkDir.FullName, ["-jar", _fabricJarName, "-nogui"]);

        _logger.Information("Waiting for server port {Port}...", ServerPort);
        for (int a = 0; a < 150; a++)
        {
            await Task.Delay(2000);
            if (_serverProcess.Process.HasExited) { _logger.Error("Server crashed"); return; }
            try { using var t = new System.Net.Sockets.TcpClient(); await t.ConnectAsync("127.0.0.1", ServerPort); _portReady = true; break; } catch { }
        }
        if (!_portReady) { _logger.Error("Port unreachable"); return; }
        File.WriteAllText("/tmp/apace-server-ready", DateTime.UtcNow.ToString("O"));

        _rcon = new MinecraftRconClient("127.0.0.1", RconPort, "apace", _logger);
        for (int a = 0; a < 60; a++)
        {
            await Task.Delay(1000);
            if (await _rcon.ConnectAsync()) { _logger.Information("RCON connected"); break; }
            if (a == 0) _logger.Information("Waiting for RCON...");
        }

        // TEST: place diamond block in overworld and 3 dimensions
        if (_rcon is not null)
        {
            await _rcon.SendCommandAsync("setblock 0 5 0 minecraft:diamond_block");
            for (int i = 0; i < 3; i++)
                await _rcon.SendCommandAsync($"execute in apace:bp_{i} run setblock 0 4 0 minecraft:diamond_block");
            _logger.Information("Test blocks placed");
        }

        _logger.Information("Shared server ready — accepting buildplates");
    }

    /// <summary>
    /// Place buildplate world data in overworld at next far offset.
    /// Server hasn't pre-generated chunks here → loads fresh from disk.
    /// </summary>
        public async Task<string?> CreateBuildplateOffsetAsync(string instanceId, string? playerId,
        string buildplateId, byte[] serverData)
    {
        var slotId = $"bp_{_offsets.Count}";
        int offsetX = _nextX;
        _nextX += 256;
        _logger.Information("Buildplate {Slot} at X={Offset}", slotId, offsetX);

        try
        {
            var tmpDir = Path.Combine(Path.GetTempPath(), $"apace-bp-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tmpDir);
            using (var ms = new MemoryStream(serverData))
            using (var zip = new System.IO.Compression.ZipArchive(ms))
                foreach (var entry in zip.Entries)
                {
                    var dest = Path.Combine(tmpDir, entry.FullName);
                    var p = Path.GetDirectoryName(dest);
                    if (p is not null) Directory.CreateDirectory(p);
                    using var es = entry.Open(); using var fs = File.Create(dest); es.CopyTo(fs);
                }

            if (_rcon is not null)
            {
                var rd = Path.Combine(tmpDir, "region");
                if (Directory.Exists(rd))
                {
                    int placed = await McaBlockPlacer.PlaceAsync(rd, _rcon, offsetX, 0);
                    _logger.Information("Placed {Count} blocks via RCON", placed);
                }
            }

            try { Directory.Delete(tmpDir, true); } catch { }
            _offsets[slotId] = new OffsetInfo(instanceId, buildplateId, playerId, slotId, offsetX);
            _logger.Information("Buildplate {Slot} ready", slotId);
            return slotId;
        }
        catch (Exception ex) { _logger.Error(ex, "Failed"); return null; }
    }

public async Task<bool> TeleportPlayerAsync(string playerId, string slotId)
    {
        if (_rcon is null || !_offsets.TryGetValue(slotId, out var info)) return false;
        await Task.Delay(2000);
        // Teleport player directly to dimension with cloned buildplate
        var r = await _rcon.SendCommandAsync($"tp {playerId} {info.OffsetX} 100 0");
        return r is not null;
    }

    public void RemoveOffset(string slotId) => _offsets.TryRemove(slotId, out _);
    public int ActiveCount => _offsets.Count;

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
        CopyIf(Path.Combine(_serverTemplateDir.FullName, _fabricJarName), Path.Combine(_serverWorkDir.FullName, _fabricJarName), true);
        CopyIf(Path.Combine(_serverTemplateDir.FullName, ".fabric"), Path.Combine(_serverWorkDir.FullName, ".fabric"));
        CopyIf(Path.Combine(_serverTemplateDir.FullName, "libraries"), Path.Combine(_serverWorkDir.FullName, "libraries"));
        CopyIf(Path.Combine(_serverTemplateDir.FullName, "versions"), Path.Combine(_serverWorkDir.FullName, "versions"));
        CopyIf(Path.Combine(_serverTemplateDir.FullName, "mods"), Path.Combine(_serverWorkDir.FullName, "mods"));
    }
    private static void CopyIf(string s, string d, bool f = false)
    { if (f) { if (File.Exists(s)) File.Copy(s, d, true); } else { if (Directory.Exists(s)) new DirectoryInfo(s).CopyTo(d); } }

    public sealed record OffsetInfo(string InstanceId, string BuildplateId, string? PlayerId, string SlotId, int OffsetX);
}
