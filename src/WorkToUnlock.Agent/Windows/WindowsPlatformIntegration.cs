using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WorkToUnlock.Agent.Services;

namespace WorkToUnlock.Agent.Windows;

[SupportedOSPlatform("windows")]
internal sealed class WindowsPlatformIntegration(
    AgentWindow window,
    WindowsSessionStateProvider session,
    TrayController tray,
    IHostApplicationLifetime lifetime,
    ILogger<WindowsPlatformIntegration> logger) : IPlatformIntegration, IDisposable
{
    private bool _started;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        window.Start();
        session.Attach(window);
        tray.Initialize();
        window.CloseRequested += () =>
        {
            logger.LogInformation("Windows asked the agent to close (sign-out, shutdown or installer)");
            lifetime.StopApplication();
        };
        CrashRecovery.Register(logger);
    }

    public void ApplyStartupRegistration(bool startWithWindows)
    {
        if (Infrastructure.AppPaths.InstanceName is { } instance)
        {
            logger.LogInformation("Isolated instance '{Instance}': startup registration skipped", instance);
            return;
        }

        StartupRegistration.Apply(startWithWindows, Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "WorkToUnlock.Agent.exe"), logger);
    }

    public void Dispose()
    {
        tray.Dispose();
        window.Dispose();
    }
}
