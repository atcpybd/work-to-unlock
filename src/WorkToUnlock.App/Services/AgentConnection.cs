using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Serilog;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Ipc;
using WorkToUnlock.Infrastructure.Ipc;

namespace WorkToUnlock.App.Services;

/// <summary>
/// The dashboard's only link to the background agent. Connects (starting the agent if it is not running),
/// subscribes to status events, reconnects automatically, and raises every event on the UI thread.
/// The dashboard never tracks activity or enforces anything itself.
/// </summary>
public sealed class AgentConnection : IAsyncDisposable
{
    public const string AgentPathVariable = "WORKTOUNLOCK_AGENT_PATH";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    private readonly DispatcherQueue _dispatcher;
    private readonly CancellationTokenSource _shutdown = new();
    private IpcClient? _client;
    private Task? _loop;
    private bool _launchAttempted;

    public AgentConnection(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    public event EventHandler<AgentStatus>? StatusChanged;

    public event EventHandler<IReadOnlyList<ApplicationRule>>? RulesChanged;

    public event EventHandler<AppSettings>? SettingsChanged;

    public event EventHandler<bool>? ConnectionChanged;

    public event EventHandler<string?>? ShowRequested;

    public event EventHandler? ExitRequested;

    public bool IsConnected => _client?.IsConnected == true;

    public AgentStatus? LastStatus { get; private set; }

    public static string AgentPath =>
        Environment.GetEnvironmentVariable(AgentPathVariable) is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(AppContext.BaseDirectory, "WorkToUnlock.Agent.exe");

    public void Start() => _loop ??= Task.Run(() => RunAsync(_shutdown.Token));

    /// <summary>Starts the agent process (used by the "Start agent" button and on first connection failure).</summary>
    public bool LaunchAgent()
    {
        var path = AgentPath;
        if (!File.Exists(path))
        {
            Log.Warning("Agent executable not found at {Path}", path);
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error(ex, "Could not start the agent");
            return false;
        }
    }

    public async Task<T?> RequestAsync<T>(string method, object? payload = null, TimeSpan? timeout = null)
    {
        var client = _client;
        if (client is not { IsConnected: true })
        {
            throw new InvalidOperationException("The Work to Unlock agent is not running.");
        }

        return await client.RequestAsync<T>(method, payload, _shutdown.Token, timeout).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            IpcClient? client = null;
            try
            {
                client = await IpcClient.ConnectAsync(PipeSecurityFactory.AgentPipeNameForCurrentUser(), ConnectTimeout, requireSameUserServer: true, cancellationToken).ConfigureAwait(false);
                var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                client.Disconnected += (_, _) => disconnected.TrySetResult();
                client.EventReceived += OnEvent;
                await client.SendAsync(AgentMethods.Subscribe, null, cancellationToken).ConfigureAwait(false);
                _client = client;

                var status = await client.RequestAsync<AgentStatus>(AgentMethods.GetStatus, null, cancellationToken).ConfigureAwait(false);
                Post(() =>
                {
                    ConnectionChanged?.Invoke(this, true);
                    if (status is not null)
                    {
                        LastStatus = status;
                        StatusChanged?.Invoke(this, status);
                    }
                });

                Log.Information("Connected to the agent");
                await disconnected.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                Log.Warning("Lost the connection to the agent");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or IpcException or UnauthorizedAccessException)
            {
                if (!_launchAttempted)
                {
                    _launchAttempted = true;
                    Log.Information("Agent not reachable; starting it");
                    LaunchAgent();
                }
            }
            finally
            {
                _client = null;
                if (client is not null)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
            }

            Post(() => ConnectionChanged?.Invoke(this, false));
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void OnEvent(object? sender, IpcEventArgs e)
    {
        switch (e.Name)
        {
            case AgentEvents.Status when e.GetPayload<AgentStatus>() is { } status:
                Post(() =>
                {
                    LastStatus = status;
                    StatusChanged?.Invoke(this, status);
                });
                break;
            case AgentEvents.RulesChanged when e.GetPayload<List<ApplicationRule>>() is { } rules:
                Post(() => RulesChanged?.Invoke(this, rules));
                break;
            case AgentEvents.SettingsChanged when e.GetPayload<AppSettings>() is { } settings:
                Post(() => SettingsChanged?.Invoke(this, settings));
                break;
            case AgentEvents.ShowDashboard:
                var page = e.GetPayload<ShowDashboardRequest>()?.Page;
                Post(() => ShowRequested?.Invoke(this, page));
                break;
            case AgentEvents.ExitDashboard:
                Post(() => ExitRequested?.Invoke(this, EventArgs.Empty));
                break;
        }
    }

    private void Post(Action action) => _dispatcher.TryEnqueue(() => action());
}
