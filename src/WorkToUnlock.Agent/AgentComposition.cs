using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WorkToUnlock.Agent.Services;
using WorkToUnlock.Agent.Windows;
using WorkToUnlock.Core.Activity;
using WorkToUnlock.Core.Enforcement;
using WorkToUnlock.Core.Persistence;
using WorkToUnlock.Core.Rewards;
using WorkToUnlock.Core.Rules;
using WorkToUnlock.Core.Time;
using WorkToUnlock.Core.Tracking;
using WorkToUnlock.Enforcement.Discovery;
using WorkToUnlock.Enforcement.Processes;
using WorkToUnlock.Enforcement.Strict;
using WorkToUnlock.Infrastructure;
using WorkToUnlock.Infrastructure.Persistence;
using WorkToUnlock.Infrastructure.Windows;

namespace WorkToUnlock.Agent;

/// <summary>Dependency injection wiring for the production agent.</summary>
[SupportedOSPlatform("windows")]
internal static class AgentComposition
{
    public static void Register(IServiceCollection services, AppPaths paths)
    {
        services.AddSingleton(paths);
        services.AddSingleton<AgentInfo>();
        services.AddSingleton<IClock>(_ => new SystemClock(WindowsBootId.Read()));
        services.AddSingleton(_ => WindowsSystemFolders.Create(AppContext.BaseDirectory));

        // Persistence
        services.AddSingleton(sp => new SqliteDatabase(paths.DatabasePath, paths.BackupPath, sp.GetRequiredService<ILogger<SqliteDatabase>>()));
        services.AddSingleton<IRewardStateStore, SqliteRewardStateStore>();
        services.AddSingleton<IApplicationRuleStore, SqliteApplicationRuleStore>();
        services.AddSingleton<IWorkSessionStore, SqliteWorkSessionStore>();
        services.AddSingleton<ISettingsStore, SqliteSettingsStore>();
        services.AddSingleton<IAuditLog, SqliteAuditLog>();
        services.AddSingleton<IStatisticsStore, SqliteStatisticsStore>();

        // Domain
        services.AddSingleton<IRuleCatalog, RuleCatalog>();
        services.AddSingleton<IApplicationRuleMatcher, ApplicationRuleMatcher>();
        services.AddSingleton<IRewardEngine, RewardEngine>();
        services.AddSingleton<IWorkTracker, WorkTracker>();
        services.AddSingleton<ActivitySampler>();

        // Windows activity monitoring
        services.AddSingleton<IExecutableSignatureReader, AuthenticodeSignatureReader>();
        services.AddSingleton<IForegroundAppMonitor, Win32ForegroundAppMonitor>();
        services.AddSingleton<IIdleDetector, Win32IdleDetector>();
        services.AddSingleton<WindowsSessionStateProvider>();
        services.AddSingleton<ISessionStateProvider>(sp => sp.GetRequiredService<WindowsSessionStateProvider>());

        // Enforcement
        services.AddSingleton<IProcessInspector, Win32ProcessInspector>();
        services.AddSingleton<IAppEnforcer, ProcessEnforcer>();
        services.AddSingleton<IStrictEnforcementClient, StrictEnforcementClient>();
        services.AddSingleton<IApplicationDiscovery, ApplicationDiscovery>();
        services.AddSingleton<EnforcementCoordinator>();

        // Agent services
        services.AddSingleton<SettingsService>();
        services.AddSingleton<NotificationService>();
        services.AddSingleton<IEnforcementNotifier>(sp => sp.GetRequiredService<NotificationService>());
        services.AddSingleton<AgentStatusService>();
        services.AddSingleton<AgentEventHub>();
        services.AddSingleton<IAgentEventHub>(sp => sp.GetRequiredService<AgentEventHub>());
        services.AddSingleton<DashboardBridge>();
        services.AddSingleton<AgentRequestHandler>();
        services.AddSingleton<DefaultRulesSeeder>();
        services.AddSingleton<MaintenanceService>();

        // Tray and OS integration
        services.AddSingleton<AgentWindow>();
        services.AddSingleton<TrayController>();
        services.AddSingleton<ITrayPresenter>(sp => sp.GetRequiredService<TrayController>());
        services.AddSingleton<IPlatformIntegration, WindowsPlatformIntegration>();

        services.AddHostedService<AgentService>();
    }
}
