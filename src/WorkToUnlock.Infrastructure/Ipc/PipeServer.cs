using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using WorkToUnlock.Core.Ipc;
using WorkToUnlock.Infrastructure.Windows;

namespace WorkToUnlock.Infrastructure.Ipc;

/// <summary>Error reported back to the caller as a failed response.</summary>
public sealed class IpcException(string message) : Exception(message);

/// <summary>Information about a connected client.</summary>
public sealed class IpcClientContext
{
    internal IpcClientContext(long connectionId, int processId)
    {
        ConnectionId = connectionId;
        ProcessId = processId;
    }

    public long ConnectionId { get; }

    /// <summary>Client process id (0 when unknown).</summary>
    public int ProcessId { get; }

    /// <summary>Client executable path, when it could be determined.</summary>
    public string? ExecutablePath { get; internal set; }

    /// <summary>Client user SID, when the server could determine it.</summary>
    public string? UserSid { get; internal set; }

    public bool Subscribed { get; internal set; }
}

public interface IIpcRequestHandler
{
    /// <summary>Handles a request and returns the response payload (serialized to JSON), or null.</summary>
    /// <exception cref="IpcException">The request was rejected; the message is sent to the client.</exception>
    Task<object?> HandleAsync(IpcClientContext client, string method, JsonElement? payload, CancellationToken cancellationToken);
}

public sealed class PipeServerOptions
{
    public required string PipeName { get; init; }

    /// <summary>Creates the ACL for each pipe instance (Windows). Null uses the platform default.</summary>
    public Func<PipeSecurity>? SecurityFactory { get; init; }

    public int MaxInstances { get; init; } = 16;

    /// <summary>Resolve the client's user SID by impersonation (used by the elevated service).</summary>
    public bool ResolveClientUser { get; init; }

    /// <summary>Optional gate run after connection; returning false disconnects the client.</summary>
    public Func<IpcClientContext, bool>? ClientValidator { get; init; }

    public int MaxMessageBytes { get; init; } = 4 * 1024 * 1024;
}

/// <summary>
/// Newline-delimited JSON request/response server over a named pipe, with server-push events for subscribers.
/// Requests on one connection are handled concurrently; responses are matched by id.
/// </summary>
public sealed class PipeServer(PipeServerOptions options, IIpcRequestHandler handler, ILogger<PipeServer> logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<long, Connection> _connections = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _acceptLoop;
    private long _nextConnectionId;

    public int SubscriberCount => _connections.Values.Count(c => c.Context.Subscribed);

    public void Start()
    {
        if (_acceptLoop is not null)
        {
            throw new InvalidOperationException("The pipe server is already running.");
        }

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
        logger.LogInformation("Pipe server listening on {Pipe}", options.PipeName);
    }

    /// <summary>Sends an event to every subscribed client. Clients that fail are disconnected.</summary>
    public async Task BroadcastAsync(string eventName, object? payload)
    {
        var message = new IpcMessage
        {
            Type = IpcMessage.EventType,
            Name = eventName,
            Payload = payload is null ? null : IpcJson.ToElement(payload),
        };

        foreach (var connection in _connections.Values.Where(c => c.Context.Subscribed))
        {
            try
            {
                await connection.WriteAsync(message, _shutdown.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                connection.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        foreach (var connection in _connections.Values)
        {
            connection.Dispose();
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _shutdown.Dispose();
    }

    private NamedPipeServerStream CreateStream()
    {
        if (OperatingSystem.IsWindows() && options.SecurityFactory is not null)
        {
            return NamedPipeServerStreamAcl.Create(
                options.PipeName,
                PipeDirection.InOut,
                options.MaxInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: 0,
                outBufferSize: 0,
                options.SecurityFactory());
        }

        return new NamedPipeServerStream(options.PipeName, PipeDirection.InOut, options.MaxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? stream = null;
            try
            {
                stream = CreateStream();
                await stream.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                var context = new IpcClientContext(Interlocked.Increment(ref _nextConnectionId), GetClientProcessId(stream));
                if (OperatingSystem.IsWindows() && context.ProcessId != 0)
                {
                    context.ExecutablePath = ProcessImage.TryGetImagePath(context.ProcessId);
                }

                if (options.ResolveClientUser && OperatingSystem.IsWindows())
                {
                    context.UserSid = ResolveClientUser(stream);
                }

                if (options.ClientValidator is { } validator && !validator(context))
                {
                    logger.LogWarning("Rejected pipe client pid {Pid} ({Path})", context.ProcessId, context.ExecutablePath);
                    await stream.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                var connection = new Connection(stream, context);
                _connections[context.ConnectionId] = connection;
                stream = null;
                _ = Task.Run(() => ServeAsync(connection, cancellationToken), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Pipe accept failed");
                try
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                if (stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task ServeAsync(Connection connection, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new StreamReader(connection.Stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (line.Length > options.MaxMessageBytes)
                {
                    logger.LogWarning("Dropping oversized message from pid {Pid}", connection.Context.ProcessId);
                    break;
                }

                IpcMessage? request;
                try
                {
                    request = JsonSerializer.Deserialize<IpcMessage>(line, IpcJson.Options);
                }
                catch (JsonException)
                {
                    logger.LogWarning("Malformed message from pid {Pid}", connection.Context.ProcessId);
                    break;
                }

                if (request is not { Type: IpcMessage.RequestType, Name: not null })
                {
                    continue;
                }

                _ = Task.Run(() => HandleRequestAsync(connection, request, cancellationToken), CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            _connections.TryRemove(connection.Context.ConnectionId, out _);
            connection.Dispose();
        }
    }

    private async Task HandleRequestAsync(Connection connection, IpcMessage request, CancellationToken cancellationToken)
    {
        var response = new IpcMessage { Type = IpcMessage.ResponseType, Id = request.Id };
        try
        {
            if (request.Name == AgentMethods.Subscribe)
            {
                connection.Context.Subscribed = true;
            }
            else
            {
                var result = await handler.HandleAsync(connection.Context, request.Name!, request.Payload, cancellationToken).ConfigureAwait(false);
                response.Payload = result is null ? null : IpcJson.ToElement(result);
            }
        }
        catch (IpcException ex)
        {
            response.Error = ex.Message;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Request {Method} failed", request.Name);
            response.Error = "The request failed. See the agent log for details.";
        }

        try
        {
            await connection.WriteAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            connection.Dispose();
        }
    }

    private static int GetClientProcessId(NamedPipeServerStream stream)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        return PipeNative.GetNamedPipeClientProcessId(stream.SafePipeHandle, out var pid) ? (int)pid : 0;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? ResolveClientUser(NamedPipeServerStream stream)
    {
        string? sid = null;
        try
        {
            stream.RunAsClient(() => sid = System.Security.Principal.WindowsIdentity.GetCurrent(ifImpersonating: true)?.User?.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return sid;
    }

    private sealed class Connection(NamedPipeServerStream stream, IpcClientContext context) : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private int _disposed;

        public NamedPipeServerStream Stream { get; } = stream;

        public IpcClientContext Context { get; } = context;

        public async Task WriteAsync(IpcMessage message, CancellationToken cancellationToken)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message, IpcJson.Options);
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await Stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Stream.Dispose();
            }
        }
    }
}

internal static partial class PipeNative
{
    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
