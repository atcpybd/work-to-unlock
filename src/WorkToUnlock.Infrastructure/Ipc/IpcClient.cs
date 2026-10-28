using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using WorkToUnlock.Core.Ipc;

namespace WorkToUnlock.Infrastructure.Ipc;

public sealed class IpcEventArgs(string name, JsonElement? payload) : EventArgs
{
    public string Name { get; } = name;

    public JsonElement? Payload { get; } = payload;

    public T? GetPayload<T>() => IpcJson.FromElement<T>(Payload);
}

/// <summary>Client side of <see cref="PipeServer"/>.</summary>
public sealed class IpcClient : IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly NamedPipeClientStream _stream;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<IpcMessage>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _readLoop;
    private long _nextId;
    private int _disconnected;

    private IpcClient(NamedPipeClientStream stream) => _stream = stream;

    public event EventHandler<IpcEventArgs>? EventReceived;

    public event EventHandler? Disconnected;

    public bool IsConnected => _disconnected == 0 && _stream.IsConnected;

    /// <param name="requireSameUserServer">
    /// Verify the server process runs as the current user (agent pipe). Must be false for the SYSTEM service pipe.
    /// </param>
    public static async Task<IpcClient> ConnectAsync(string pipeName, TimeSpan timeout, bool requireSameUserServer, CancellationToken cancellationToken)
    {
        var options = PipeOptions.Asynchronous;
        if (requireSameUserServer && OperatingSystem.IsWindows())
        {
            options |= PipeOptions.CurrentUserOnly;
        }

        var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, options);
        try
        {
            await stream.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var client = new IpcClient(stream);
        client._readLoop = Task.Run(client.ReadLoopAsync);
        return client;
    }

    public async Task<JsonElement?> SendAsync(string method, object? payload, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        if (!IsConnected)
        {
            throw new IOException("Not connected.");
        }

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            var message = new IpcMessage
            {
                Type = IpcMessage.RequestType,
                Id = id,
                Name = method,
                Payload = payload is null ? null : IpcJson.ToElement(payload),
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message, IpcJson.Options);
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await _stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            var response = await completion.Task.WaitAsync(timeout ?? DefaultTimeout, cancellationToken).ConfigureAwait(false);
            if (response.Error is not null)
            {
                throw new IpcException(response.Error);
            }

            return response.Payload;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async Task<T?> RequestAsync<T>(string method, object? payload, CancellationToken cancellationToken, TimeSpan? timeout = null) =>
        IpcJson.FromElement<T>(await SendAsync(method, payload, cancellationToken, timeout).ConfigureAwait(false));

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        }

        _shutdown.Dispose();
        _writeLock.Dispose();
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            using var reader = new StreamReader(_stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            while (!_shutdown.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_shutdown.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                IpcMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<IpcMessage>(line, IpcJson.Options);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (message is null)
                {
                    continue;
                }

                if (message.Type == IpcMessage.ResponseType && _pending.TryGetValue(message.Id, out var completion))
                {
                    completion.TrySetResult(message);
                }
                else if (message.Type == IpcMessage.EventType && message.Name is not null)
                {
                    EventReceived?.Invoke(this, new IpcEventArgs(message.Name, message.Payload));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            if (Interlocked.Exchange(ref _disconnected, 1) == 0)
            {
                foreach (var pending in _pending.Values)
                {
                    pending.TrySetException(new IOException("The connection was closed."));
                }

                Disconnected?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
