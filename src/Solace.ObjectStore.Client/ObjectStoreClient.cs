using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using Serilog;

namespace Solace.ObjectStore.Client;

public sealed class ObjectStoreClient : IAsyncDisposable
{
    public sealed class ConnectException : ObjectStoreClientException
    {
        public ConnectException(string? message)
            : base(message)
        {
        }

        public ConnectException(string? message, Exception? cause)
            : base(message, cause)
        {
        }
    }

    private const int MaxConcurrentCommands = 256;

    private readonly string _host;
    private readonly int _port;
    private readonly SemaphoreSlim _commandSlots;
    private readonly CancellationTokenSource _cts = new();

    public static async Task<ObjectStoreClient> ConnectAsync(string connectionString)
    {
        string[] parts = connectionString.Split(':', 2);
        string host = parts[0];
        if (!int.TryParse(parts.Length > 1 ? parts[1] : "5396", out int port) || port is <= 0 or > 65535)
        {
            throw new ArgumentException($"Invalid port number in connection string.");
        }

        Socket socket = new(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(host, port);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new ConnectException($"Could not create socket: {ex.Message}", ex);
        }

        // The probe connection only verifies the server is reachable; every command runs on its own connection.
        socket.Dispose();

        return new ObjectStoreClient(host, port);
    }

    private ObjectStoreClient(string host, int port)
    {
        _host = host;
        _port = port;
        _commandSlots = new SemaphoreSlim(MaxConcurrentCommands, MaxConcurrentCommands);
    }

    public async Task<string?> StoreAsync(ReadOnlyMemory<byte> data)
    {
        var result = await EnqueueCommand(CommandType.Store, data);
        return (string?)result;
    }

    public async Task<byte[]?> GetAsync(string id)
    {
        var result = await EnqueueCommand(CommandType.Get, id);
        return (byte[]?)result;
    }

    public async Task<bool> DeleteAsync(string id)
    {
        var result = await EnqueueCommand(CommandType.Delete, id);
        return result is true;
    }

    private async Task<object?> EnqueueCommand(CommandType type, object data)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new CommandState();

        // Armed at enqueue time, before the command even queues for a slot, and kept in sync with the
        // caller's WaitAsync deadline below: when the caller gives up this token fires too, so the
        // semaphore wait is cancelled and an abandoned command can never wake up later and grab a slot.
        var queueCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        queueCts.CancelAfter(TimeSpan.FromSeconds(60));

        _ = Task.Run(() => ExecuteCommandAsync(type, data, tcs, queueCts, state));

        try
        {
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException)
        {
            // Nobody observes the TCS from here on; complete it so the abandoned command's late
            // completion cannot surface as an unobserved task exception.
            tcs.TrySetCanceled();

            if (state.Started)
            {
                Log.Error($"ObjectStore command {type} (data {data}) timed out after 60s waiting for response");
            }
            else
            {
                Log.Error($"ObjectStore command {type} (data {data}) timed out after 60s while queued");
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, $"ObjectStore command {type} (data {data}) failed");
            return null;
        }
    }

    private async Task ExecuteCommandAsync(CommandType type, object data, TaskCompletionSource<object?> tcs, CancellationTokenSource queueCts, CommandState state)
    {
        try
        {
            await _commandSlots.WaitAsync(queueCts.Token);
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
            return;
        }
        finally
        {
            queueCts.Dispose();
        }

        state.Started = true;

        try
        {
            await RunCommandAsync(type, data, tcs);
        }
        finally
        {
            try
            {
                _commandSlots.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private sealed class CommandState
    {
        // Set as soon as the command owns a semaphore slot, so the caller can tell
        // "never ran" (queued) timeouts from "ran but no reply" timeouts.
        public volatile bool Started;
    }

    private async Task RunCommandAsync(CommandType type, object data, TaskCompletionSource<object?> tcs)
    {
        try
        {
            using var commandCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            commandCts.CancelAfter(TimeSpan.FromSeconds(60));

            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(_host, _port, commandCts.Token);
            }
            catch (SocketException ex)
            {
                throw new ObjectStoreClientException($"Could not create socket: {ex.Message}", ex);
            }

            await using var stream = new NetworkStream(socket, ownsSocket: false);
            var reader = PipeReader.Create(stream);
            var writer = PipeWriter.Create(stream);
            try
            {
                await WriteCommandAsync(writer, type, data, commandCts.Token);
                await ReadResponseAsync(reader, type, tcs, commandCts.Token);
            }
            finally
            {
                await reader.CompleteAsync();
                await writer.CompleteAsync();
            }
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
    }

    private static async Task WriteCommandAsync(PipeWriter writer, CommandType type, object data, CancellationToken cancellationToken)
    {
        switch (type)
        {
            case CommandType.Store:
                var memory = (ReadOnlyMemory<byte>)data;
                var header = Encoding.ASCII.GetBytes($"STORE {memory.Length}\n");

                writer.Write(header);
                writer.Write(memory.Span);

                await writer.FlushAsync(cancellationToken);
                break;
            case CommandType.Get:
                await writer.WriteAsync(Encoding.ASCII.GetBytes($"GET {(string)data}\n"), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                break;
            case CommandType.Delete:
                await writer.WriteAsync(Encoding.ASCII.GetBytes($"DEL {(string)data}\n"), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                break;
        }
    }

    private static async Task ReadResponseAsync(PipeReader reader, CommandType type, TaskCompletionSource<object?> tcs, CancellationToken cancellationToken)
    {
        Range[] partsArray = ArrayPool<Range>.Shared.Rent(2);
        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync(cancellationToken);
                ReadOnlySequence<byte> buffer = result.Buffer;

                if (TryReadMessage(ref buffer, out ReadOnlySequence<byte> line))
                {
                    var message = Encoding.ASCII.GetString(line).AsSpan().Trim('\r');
                    var parts = partsArray.AsSpan(0, 2);
                    var partsLength = message.Split(parts, ' ');
                    var partsLocal = parts[..partsLength];

                    reader.AdvanceTo(buffer.Start, result.Buffer.End);

                    if (message[partsLocal[0]] is "ERR")
                    {
                        tcs.TrySetResult(type is CommandType.Delete ? false : null);
                        return;
                    }

                    if (message[partsLocal[0]] is "OK")
                    {
                        if (type is CommandType.Delete)
                        {
                            tcs.TrySetResult(true);
                            return;
                        }

                        if (type is CommandType.Store)
                        {
                            tcs.TrySetResult(partsLocal.Length > 1 ? message[partsLocal[1]].ToString() : null);
                            return;
                        }

                        if (type is CommandType.Get && partsLocal.Length is 2 && int.TryParse(message[partsLocal[1]], out int length))
                        {
                            await ReadBinaryPayloadAsync(reader, length, tcs, cancellationToken);
                            return;
                        }
                    }

                    throw new InvalidOperationException("Invalid server response format.");
                }

                reader.AdvanceTo(buffer.Start, result.Buffer.End);

                if (result.IsCompleted)
                {
                    throw new EndOfStreamException("Server closed the connection.");
                }
            }
        }
        finally
        {
            ArrayPool<Range>.Shared.Return(partsArray);
        }
    }

    private static async Task ReadBinaryPayloadAsync(PipeReader reader, int length, TaskCompletionSource<object?> tcs, CancellationToken cancellationToken)
    {
        if (length is 0)
        {
            tcs.TrySetResult(Array.Empty<byte>());
            return;
        }

        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (buffer.Length >= length)
            {
                byte[] data = buffer.Slice(0, length).ToArray();
                tcs.TrySetResult(data);

                reader.AdvanceTo(buffer.GetPosition(length));
                return;
            }

            reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
            {
                throw new EndOfStreamException("Incomplete binary payload received.");
            }
        }
    }

    private static bool TryReadMessage(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line)
    {
        SequencePosition? position = buffer.PositionOf((byte)'\n');
        if (position is null)
        {
            line = default;
            return false;
        }

        line = buffer.Slice(0, position.Value);
        buffer = buffer.Slice(buffer.GetPosition(1, position.Value));
        return true;
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _commandSlots.Dispose();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }

    private enum CommandType
    {
        Store,
        Get,
        Delete,
    }
}
