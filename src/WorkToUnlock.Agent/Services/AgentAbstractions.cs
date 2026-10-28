using WorkToUnlock.Core.Ipc;
using WorkToUnlock.Infrastructure.Ipc;

namespace WorkToUnlock.Agent.Services;

/// <summary>Static facts about the running agent.</summary>
public sealed class AgentInfo
{
    public string Version { get; } = typeof(AgentInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    public string InstallDirectory { get; } = AppContext.BaseDirectory;
}

/// <summary>Pushes events to connected dashboards.</summary>
public interface IAgentEventHub
{
    int DashboardCount { get; }

    Task PublishAsync(string eventName, object? payload);
}

/// <summary>Event hub backed by the pipe server (attached once the server has started).</summary>
public sealed class AgentEventHub : IAgentEventHub
{
    private volatile PipeServer? _server;

    public int DashboardCount => _server?.SubscriberCount ?? 0;

    public void Attach(PipeServer server) => _server = server;

    public Task PublishAsync(string eventName, object? payload) =>
        _server is { } server ? server.BroadcastAsync(eventName, payload) : Task.CompletedTask;
}

/// <summary>The tray icon (Windows) or nothing (tests, other platforms).</summary>
public interface ITrayPresenter
{
    void Update(AgentStatus status);

    void ShowNotification(string title, string message);
}

public sealed class NullTrayPresenter : ITrayPresenter
{
    public List<(string Title, string Message)> Notifications { get; } = [];

    public void Update(AgentStatus status)
    {
    }

    public void ShowNotification(string title, string message) => Notifications.Add((title, message));
}
