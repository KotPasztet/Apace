using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Serilog;

namespace Solace.Buildplate.Launcher;

/// <summary>
/// Lightweight RCON client for sending commands to a Minecraft/Fabric server.
/// Used to create dimensions and teleport players without modifying Java plugins.
/// </summary>
public sealed class MinecraftRconClient : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _password;
    private readonly ILogger _logger;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private int _requestId;

    public MinecraftRconClient(string host, int port, string password, ILogger logger)
    {
        _host = host;
        _port = port;
        _password = password;
        _logger = logger;
    }

    public async Task<bool> ConnectAsync()
    {
        try
        {
            _client = new TcpClient();
            await _client.ConnectAsync(_host, _port);
            _stream = _client.GetStream();

            // RCON handshake: type 3 = login
            if (!await AuthenticateAsync())
            {
                _logger.Debug("RCON authentication failed — server may still be starting");
                return false;
            }

            _logger.Information("RCON connected to {Host}:{Port}", _host, _port);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "RCON not ready");
            return false;
        }
    }

    private async Task<bool> AuthenticateAsync()
    {
        var request = BuildPacket(3, _password);
        await _stream!.WriteAsync(request);
        var response = await ReadPacketAsync();
        // RCON login: server echoes requestId on success, sends -1 on failure
        return response is not null && response.RequestId == _requestId - 1;
    }

    /// <summary>
    /// Send a Minecraft command and return the response.
    /// </summary>
    public async Task<string?> SendCommandAsync(string command)
    {
        if (_stream is null || _client is null || !_client.Connected)
        {
            if (!await ConnectAsync())
                return null;
        }

        try
        {
            var request = BuildPacket(2, command);
            await _stream!.WriteAsync(request);
            var response = await ReadPacketAsync();
            return response?.Body ?? "";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "RCON command failed: {Command}", command);
            return null;
        }
    }

    /// <summary>
    /// Create a Fabric dimension using /execute and datapack commands.
    /// </summary>
    public async Task<bool> CreateDimensionAsync(string dimensionId, string? playerId)
    {
        // Tell the server to execute a dimension change command
        // The Fountain-fabric mod handles the actual teleport
        var cmd = $"execute in apace:{dimensionId} run say Dimension {dimensionId} activated";
        var result = await SendCommandAsync(cmd);
        _logger.Information("Dimension command result: {Result}", result);
        return result is not null;
    }

    /// <summary>
    /// Teleport a player to a specific dimension.
    /// </summary>
    public async Task<bool> TeleportPlayerAsync(string playerId, string dimensionId)
    {
        var cmd = $"execute in apace:{dimensionId} run tp {playerId} 0 64 0";
        var result = await SendCommandAsync(cmd);
        return result is not null;
    }

    private byte[] BuildPacket(int type, string body)
    {
        var id = Interlocked.Increment(ref _requestId);
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var packet = new byte[14 + bodyBytes.Length];

        // Length (4 bytes, little-endian) — excludes length field itself
        var length = 10 + bodyBytes.Length;
        BitConverter.TryWriteBytes(packet, length);

        // Request ID (4 bytes, little-endian)
        BitConverter.TryWriteBytes(packet.AsSpan(4), id);

        // Type (4 bytes, little-endian)
        BitConverter.TryWriteBytes(packet.AsSpan(8), type);

        // Body (UTF-8)
        Buffer.BlockCopy(bodyBytes, 0, packet, 12, bodyBytes.Length);

        // Two null bytes terminator
        packet[12 + bodyBytes.Length] = 0;
        packet[13 + bodyBytes.Length] = 0;

        return packet;
    }

    private async Task<RconPacket?> ReadPacketAsync()
    {
        if (_stream is null) return null;

        var lengthBuf = new byte[4];
        if (await _stream.ReadAsync(lengthBuf) < 4) return null;
        var length = BitConverter.ToInt32(lengthBuf);

        var data = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await _stream.ReadAsync(data.AsMemory(read, length - read));
            if (n == 0) return null;
            read += n;
        }

        var requestId = BitConverter.ToInt32(data, 0);
        var type = BitConverter.ToInt32(data, 4);
        var body = Encoding.UTF8.GetString(data, 8, length - 10);

        return new RconPacket(requestId, type, body);
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _client?.Dispose();
    }

    private sealed record RconPacket(int RequestId, int Type, string Body);
}
