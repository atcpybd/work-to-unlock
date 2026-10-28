using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WorkToUnlock.Agent.Services;
using WorkToUnlock.Core.Activity;
using WorkToUnlock.Core.Ipc;
using WorkToUnlock.Core.Persistence;
using WorkToUnlock.Core.Rewards;
using WorkToUnlock.Core.Rules;
using WorkToUnlock.Core.Tracking;
using WorkToUnlock.Infrastructure.Ipc;
using WorkToUnlock.Infrastructure.Persistence;

namespace WorkToUnlock.Agent;

/// <summary>
/// The agent's main loop. Bootstraps persistence and state, applies restrictions before anything else (fail-safe),
/// then samples activity every 500 ms, expires rewards, and publishes status to the tray and dashboards.
/// </summary>
public sealed class AgentService(IServiceProvider services, ILogger<AgentService> logger) : BackgroundService
{
    internal static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(30);

    private PipeServer? _pipeServer;

    /// <summary>Completes once bootstrap finished (used by integration tests).</summary>
    internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await BootstrapAsync(stoppingToken).ConfigureAwait(false);
            Ready.TrySetResult();
            await RunLoopAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Ready.TrySetException(ex);
            logger.LogCritical(ex, "The agent failed");
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await services.GetRequiredService<IWorkTracker>().FlushAsync(CancellationToken.None).ConfigureAwait(false);
            await services.GetRequiredService<ProtectionMonitor>().StopAsync(CancellationToken.None).ConfigureAwait(false);
            await services.GetRequiredService<IAuditLog>().WriteAsync("AgentStopped", "The agent stopped.", "Success", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Final flush failed");
        }

        if (_pipeServer is not null)
        {
            await _pipeServer.DisposeAsync().ConfigureAwait(false);
        }

        logger.LogInformation("Agent stopped");
    }

    private async Task BootstrapAsync(CancellationToken cancellationToken)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        var tray = services.GetRequiredService<ITrayPresenter>();
        var status = services.GetRequiredService<AgentStatusService>();
        var audit = services.GetRequiredService<IAuditLog>();
        var platform = services.GetRequiredService<IPlatformIntegration>();

        // 0. Tray icon, session notifications and crash-restart registration, so problems below can be reported.
        platform.Start();

        // 1. Persistence. Keep retrying: without the database there are no rules to enforce.
        DatabaseOpenResult? opened = null;
        for (var attempt = 1; opened is null; attempt++)
        {
            try
            {
                opened = await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                status.SetDatabaseHealthy(false);
                logger.LogError(ex, "Database unavailable (attempt {Attempt})", attempt);
                if (attempt == 1)
                {
                    tray.ShowNotification("Work to Unlock cannot open its data", "Tracking and enforcement are paused until the database is available. See the log for details.");
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 5 * attempt)), cancellationToken).ConfigureAwait(false);
            }
        }

        status.SetDatabaseHealthy(true);
        if (opened.Recovered)
        {
            await audit.WriteAsync("DatabaseRecovered", opened.Message ?? "Database recovered.", "Warning", cancellationToken).ConfigureAwait(false);
            tray.ShowNotification("Work to Unlock repaired its data", opened.Message ?? "The database was damaged and has been repaired.");
        }

        // 2. Settings, rules and reward state.
        var settings = services.GetRequiredService<SettingsService>();
        await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        await services.GetRequiredService<ProtectionMonitor>().StartAsync(cancellationToken).ConfigureAwait(false);
        var rules = services.GetRequiredService<IRuleCatalog>();
        await rules.LoadAsync(cancellationToken).ConfigureAwait(false);
        await services.GetRequiredService<DefaultRulesSeeder>().SeedIfFirstRunAsync(cancellationToken).ConfigureAwait(false);

        var engine = services.GetRequiredService<IRewardEngine>();
        await engine.InitializeAsync(RewardSettings.From(settings.Current), cancellationToken).ConfigureAwait(false);
        var tracker = services.GetRequiredService<IWorkTracker>();
        tracker.UpdateOptions(new TrackingOptions { IdleThreshold = settings.Current.IdleThreshold });

        settings.Changed += (_, current) =>
        {
            tracker.UpdateOptions(new TrackingOptions { IdleThreshold = current.IdleThreshold });
            _ = Task.Run(async () =>
            {
                try
                {
                    await engine.UpdateSettingsAsync(RewardSettings.From(current), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Applying reward settings failed");
                }
            });
            platform.ApplyStartupRegistration(current.StartWithWindows);
            _ = PublishAsync(AgentEvents.SettingsChanged, current);
        };
        rules.Changed += (_, _) => _ = PublishAsync(AgentEvents.RulesChanged, rules.Rules);
        engine.StateChanged += (_, _) => _ = PublishStatusAsync();

        // 3. Enforcement before anything else that could take time (fail-safe).
        var coordinator = services.GetRequiredService<EnforcementCoordinator>();
        coordinator.HealthChanged += (_, _) => _ = PublishStatusAsync();
        await coordinator.StartAsync(cancellationToken).ConfigureAwait(false);
        services.GetRequiredService<NotificationService>().Start();
        services.GetRequiredService<AchievementService>().Start();

        // 4. Startup registration and IPC.
        platform.ApplyStartupRegistration(settings.Current.StartWithWindows);
        _pipeServer = ActivatorUtilities.CreateInstance<PipeServer>(services, new PipeServerOptions
        {
            PipeName = PipeSecurityFactory.AgentPipeNameForCurrentUser(),
            SecurityFactory = OperatingSystem.IsWindows() ? PipeSecurityFactory.CurrentUserOnly : null,
        }, services.GetRequiredService<AgentRequestHandler>());
        _pipeServer.Start();
        services.GetRequiredService<AgentEventHub>().Attach(_pipeServer);

        await audit.WriteAsync("AgentStarted", $"Agent {services.GetRequiredService<AgentInfo>().Version} started in state {engine.GetStatus().State}.", "Success", cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Agent ready: state {State}, {Rules} rule(s)", engine.GetStatus().State, rules.Rules.Count);
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        var sampler = services.GetRequiredService<ActivitySampler>();
        var tracker = services.GetRequiredService<IWorkTracker>();
        var engine = services.GetRequiredService<IRewardEngine>();
        var notifications = services.GetRequiredService<NotificationService>();
        var maintenance = services.GetRequiredService<MaintenanceService>();

        var progress = services.GetRequiredService<ProgressService>();
        var protection = services.GetRequiredService<ProtectionMonitor>();
        var statusCadence = new Cadence(StatusInterval);
        var maintenanceCadence = new Cadence(MaintenanceInterval);
        var progressCadence = new Cadence(ProgressInterval);
        var heartbeatCadence = new Cadence(ProtectionMonitor.HeartbeatInterval);
        using var timer = new PeriodicTimer(SampleInterval);
        do
        {
            var now = Environment.TickCount64;
            try
            {
                await tracker.ProcessActivityAsync(sampler.Capture(), cancellationToken).ConfigureAwait(false);
                await engine.TickAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Tracking iteration failed");
            }

            if (statusCadence.IsDue(now))
            {
                notifications.CheckRewardCountdown(engine.GetStatus());
                await PublishStatusAsync().ConfigureAwait(false);
            }

            if (maintenanceCadence.IsDue(now))
            {
                _ = Task.Run(() => maintenance.RunIfDueAsync(cancellationToken), cancellationToken);
            }

            if (progressCadence.IsDue(now))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await progress.GetAsync(cancellationToken, refresh: true).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Daily progress check failed");
                    }
                }, cancellationToken);
            }

            if (heartbeatCadence.IsDue(now))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await protection.BeatAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Heartbeat failed");
                    }
                }, cancellationToken);
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task PublishStatusAsync()
    {
        try
        {
            var status = services.GetRequiredService<AgentStatusService>().Build();
            services.GetRequiredService<ITrayPresenter>().Update(status);
            await PublishAsync(AgentEvents.Status, status).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Status publish failed");
        }
    }

    private Task PublishAsync(string eventName, object? payload) =>
        services.GetRequiredService<AgentEventHub>().PublishAsync(eventName, payload);
}
