using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WorkToUnlock.Core.Ipc;

namespace WorkToUnlock.Agent.Services;

/// <summary>Opens, focuses and closes the WinUI dashboard. Closing the dashboard never stops the agent.</summary>
public sealed class DashboardBridge(IAgentEventHub hub, AgentInfo info, ILogger<DashboardBridge> logger)
{
    public const string DashboardPathVariable = "WORKTOUNLOCK_APP_PATH";

    public bool IsDashboardRunning => hub.DashboardCount > 0;

    public string DashboardPath =>
        Environment.GetEnvironmentVariable(DashboardPathVariable) is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(info.InstallDirectory, "WorkToUnlock.App.exe");

    public async Task ShowAsync(string? page)
    {
        if (hub.DashboardCount > 0)
        {
            await hub.PublishAsync(AgentEvents.ShowDashboard, new ShowDashboardRequest(page)).ConfigureAwait(false);
            return;
        }

        var path = DashboardPath;
        if (!File.Exists(path))
        {
            logger.LogWarning("Dashboard executable not found at {Path}", path);
            return;
        }

        try
        {
            var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! };
            if (!string.IsNullOrEmpty(page))
            {
                start.ArgumentList.Add("--page");
                start.ArgumentList.Add(page);
            }

            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogError(ex, "Could not start the dashboard");
        }
    }

    public Task ExitDashboardsAsync() => hub.PublishAsync(AgentEvents.ExitDashboard, null);
}
