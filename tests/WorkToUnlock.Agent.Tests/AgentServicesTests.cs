using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WorkToUnlock.Agent.Services;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Enforcement;
using WorkToUnlock.Core.Ipc;
using WorkToUnlock.Core.Rewards;
using WorkToUnlock.Core.Rules;
using WorkToUnlock.Core.Persistence;
using WorkToUnlock.Infrastructure.Ipc;
using WorkToUnlock.Enforcement.Strict;

namespace WorkToUnlock.Agent.Tests;

/// <summary>Wires the agent services with in-memory fakes.</summary>
internal sealed class AgentHarness : IDisposable
{
    public FakeClock Clock { get; } = new();

    public InMemoryRewardStateStore RewardStore { get; } = new();

    public InMemoryRuleStore RuleStore { get; } = new(
        TestRules.Work(1, "Code", TestRules.VsCodePath),
        TestRules.Restricted(2, "Steam", TestRules.SteamPath));

    public InMemorySettingsStore SettingsStore { get; } = new();

    public InMemoryAuditLog Audit { get; } = new();

    public FakeEnforcer Enforcer { get; } = new();

    public FakeStrictClient Strict { get; } = new();

    public FakeDiscovery Discovery { get; } = new();

    public NullTrayPresenter Tray { get; } = new();

    public RewardEngine Engine { get; }

    public RuleCatalog Rules { get; }

    public SettingsService Settings { get; }

    public EnforcementCoordinator Coordinator { get; }

    public NotificationService Notifications { get; }

    public AgentHarness()
    {
        Engine = new RewardEngine(RewardStore, Clock, NullLogger<RewardEngine>.Instance);
        Rules = new RuleCatalog(RuleStore, Clock);
        Settings = new SettingsService(SettingsStore, Audit, NullLogger<SettingsService>.Instance);
        Coordinator = new EnforcementCoordinator(Enforcer, Strict, Engine, Rules, Settings, NullLogger<EnforcementCoordinator>.Instance);
        Notifications = new NotificationService(Tray, Settings, Engine, Clock);
        Protection = new ProtectionMonitor(SettingsStore, Downtime, Settings, Audit, Clock, NullLogger<ProtectionMonitor>.Instance);
        Progress = new ProgressService(Statistics, Settings, Notifications, Clock, NullLogger<ProgressService>.Instance) { TimeZone = TimeZoneInfo.Utc };
    }

    public ProgressService Progress { get; }

    public InMemoryAchievementStore Achievements { get; } = new();

    public InMemoryDowntimeStore Downtime { get; } = new();

    public ProtectionMonitor Protection { get; }

    public async Task StartAsync()
    {
        await Settings.LoadAsync(CancellationToken.None);
        await Rules.LoadAsync(CancellationToken.None);
        await Engine.InitializeAsync(RewardSettings.From(Settings.Current), CancellationToken.None);
        Notifications.Start();
        await Coordinator.StartAsync(CancellationToken.None);
    }

    public AgentRequestHandler CreateHandler(AgentEventHub? hub = null)
    {
        var tracker = new WorkToUnlock.Core.Tracking.WorkTracker(Engine, Rules, new ApplicationRuleMatcher(new FakeSignatureReader()), new InMemoryWorkSessionStore(), NullLogger<WorkToUnlock.Core.Tracking.WorkTracker>.Instance);
        var info = new AgentInfo();
        var status = new AgentStatusService(Engine, tracker, Coordinator, Settings, info, Clock);
        var bridge = new DashboardBridge(hub ?? new AgentEventHub(), info, NullLogger<DashboardBridge>.Instance);
        var achievements = new AchievementService(Achievements, Statistics, Progress, Protection, Notifications, Clock, NullLogger<AchievementService>.Instance);
        return new AgentRequestHandler(status, Engine, Rules, Settings, Discovery, Statistics, Audit, Strict, bridge, Progress, Protection, achievements, TestRules.Folders, Clock, NullLogger<AgentRequestHandler>.Instance);
    }

    /// <summary>Waits for event-triggered reconciliations (they run on the thread pool).</summary>
    public async Task SettleAsync() => await Coordinator.ReconcileAsync("test", CancellationToken.None);

    public void Dispose()
    {
        Engine.Dispose();
        Coordinator.Dispose();
    }

    public FakeStatistics Statistics { get; } = new();

    internal sealed class FakeStatistics : IStatisticsStore
    {
        /// <summary>Daily totals returned to callers, oldest first, ending today.</summary>
        public List<DailyActivity> Days { get; set; } = [];

        public StatisticsReport Report { get; set; } = new();

        public Task<StatisticsReport> GetReportAsync(DateTimeOffset now, TimeZoneInfo timeZone, int historyDays, CancellationToken cancellationToken) => Task.FromResult(Report);

        public Task<IReadOnlyList<DailyActivity>> GetDailyTotalsAsync(DateTimeOffset now, TimeZoneInfo timeZone, int days, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DailyActivity>>(Days.ToList());

        public Task<int> PruneAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<string> ExportJsonAsync(CancellationToken cancellationToken) => Task.FromResult("{}");
    }
}

public sealed class EnforcementCoordinatorTests : IDisposable
{
    private readonly AgentHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Restrictions_are_applied_on_start_while_working()
    {
        await _h.StartAsync();

        Assert.True(_h.Enforcer.Active);
        Assert.Equal("Steam", Assert.Single(_h.Enforcer.Rules).Name);
    }

    [Fact]
    public async Task Restrictions_follow_the_reward_lifecycle()
    {
        await _h.StartAsync();
        await _h.Engine.AddWorkAsync(TimeSpan.FromHours(1), CancellationToken.None);
        await _h.SettleAsync();
        Assert.True(_h.Enforcer.Active); // REWARD_READY keeps restrictions

        await _h.Engine.ActivateRewardAsync(CancellationToken.None);
        await _h.SettleAsync();
        Assert.False(_h.Enforcer.Active);

        _h.Clock.Advance(TimeSpan.FromMinutes(20));
        await _h.Engine.TickAsync(CancellationToken.None);
        await _h.SettleAsync();
        Assert.True(_h.Enforcer.Active);
    }

    [Fact]
    public async Task Disabling_enforcement_removes_restrictions_and_is_audited()
    {
        await _h.StartAsync();

        var result = await _h.Settings.SetEnforcementEnabledAsync(false, "Exam week", CancellationToken.None);
        await _h.SettleAsync();

        Assert.True(result.Succeeded);
        Assert.False(_h.Enforcer.Active);
        Assert.Contains(_h.Audit.Events, e => e.EventType == "EnforcementDisabled" && e.Description.Contains("Exam week", StringComparison.Ordinal));
        Assert.Contains("disabled", _h.Coordinator.Health.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task No_restricted_apps_means_nothing_to_enforce()
    {
        await _h.RuleStore.DeleteAsync(2, CancellationToken.None);
        await _h.StartAsync();

        Assert.False(_h.Enforcer.Active);
    }

    [Fact]
    public async Task Strict_mode_without_the_service_degrades_but_keeps_process_enforcement()
    {
        _h.SettingsStore.Settings = AppSettings.Default with { EnforcementMode = EnforcementMode.Strict };
        await _h.StartAsync();

        Assert.True(_h.Enforcer.Active);
        Assert.Equal(EnforcementHealthState.Degraded, _h.Coordinator.Health.State);
        Assert.False(_h.Coordinator.Health.LaunchPreventionActive);
        Assert.Contains(_h.Coordinator.Health.Problems, p => p.Contains("Strict mode", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Health_is_read_live_from_the_enforcer_not_from_a_cached_copy()
    {
        await _h.StartAsync();
        Assert.Equal(EnforcementHealthState.Healthy, _h.Coordinator.Health.State);
        var scanned = new DateTimeOffset(2026, 1, 5, 9, 30, 0, TimeSpan.Zero);

        _h.Enforcer.SetHealthSilently(_h.Enforcer.Health with
        {
            State = EnforcementHealthState.Failed,
            Message = "stopped",
            LastScanUtc = scanned,
            Problems = ["The enforcement check has stopped running."],
        });

        Assert.Equal(EnforcementHealthState.Failed, _h.Coordinator.Health.State);
        Assert.Equal(scanned, _h.Coordinator.Health.LastScanUtc);
        Assert.Contains("The enforcement check has stopped running.", _h.Coordinator.Health.Problems);
    }

    [Fact]
    public async Task Strict_mode_with_the_service_reports_launch_prevention()
    {
        _h.Strict.Available = true;
        _h.SettingsStore.Settings = AppSettings.Default with { EnforcementMode = EnforcementMode.Strict };
        await _h.StartAsync();

        Assert.Equal(1, _h.Strict.ApplyCalls);
        Assert.True(_h.Coordinator.Health.LaunchPreventionActive);
        Assert.Equal(EnforcementHealthState.Healthy, _h.Coordinator.Health.State);
    }
}

public sealed class SettingsServiceTests : IDisposable
{
    private readonly AgentHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Invalid_settings_are_rejected_and_not_saved()
    {
        await _h.Settings.LoadAsync(CancellationToken.None);

        var result = await _h.Settings.UpdateAsync(AppSettings.Default with { IdleThresholdSeconds = 1 }, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(60, _h.SettingsStore.Settings.IdleThresholdSeconds);
    }

    [Fact]
    public async Task General_updates_cannot_disable_enforcement()
    {
        await _h.Settings.LoadAsync(CancellationToken.None);

        await _h.Settings.UpdateAsync(AppSettings.Default with { EnforcementEnabled = false, RewardDurationMinutes = 30 }, CancellationToken.None);

        Assert.True(_h.Settings.Current.EnforcementEnabled);
        Assert.Equal(30, _h.Settings.Current.RewardDurationMinutes);
        Assert.Contains(_h.Audit.Events, e => e.EventType == "SettingsChanged" && e.Description.Contains("RewardDurationMinutes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disabling_enforcement_requires_a_reason()
    {
        await _h.Settings.LoadAsync(CancellationToken.None);

        var result = await _h.Settings.SetEnforcementEnabledAsync(false, "  ", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(_h.Settings.Current.EnforcementEnabled);
    }
}

public sealed class AgentRequestHandlerTests : IDisposable
{
    private readonly AgentHarness _h = new();
    private readonly IpcClientContext _client = new(1, 4242);

    public void Dispose() => _h.Dispose();

    private static JsonElement Payload<T>(T value) => IpcJson.ToElement(value);

    [Fact]
    public async Task New_rules_are_inspected_and_saved()
    {
        await _h.StartAsync();
        var handler = _h.CreateHandler();

        var result = (SaveRuleResult)(await handler.HandleAsync(_client, AgentMethods.SaveRule,
            Payload(new ApplicationRule { Name = "Blockbench", ExecutablePath = TestRules.BlockbenchPath, RuleType = RuleType.Work }), CancellationToken.None))!;

        Assert.True(result.Succeeded);
        Assert.Equal(1, _h.Discovery.InspectCalls);
        Assert.Contains(_h.Rules.Rules, r => r.Name == "Blockbench" && r.Identity.FileName == "Blockbench.exe");
    }

    [Fact]
    public async Task An_app_cannot_be_both_work_and_restricted()
    {
        await _h.StartAsync();
        var handler = _h.CreateHandler();

        var result = (SaveRuleResult)(await handler.HandleAsync(_client, AgentMethods.SaveRule,
            Payload(new ApplicationRule { Name = "Steam", ExecutablePath = TestRules.SteamPath, RuleType = RuleType.Work }), CancellationToken.None))!;

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Message.Contains("cannot be both", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Protected_executables_cannot_be_restricted()
    {
        await _h.StartAsync();
        var handler = _h.CreateHandler();

        var result = (SaveRuleResult)(await handler.HandleAsync(_client, AgentMethods.SaveRule,
            Payload(new ApplicationRule { Name = "Explorer", ExecutablePath = @"C:\Windows\explorer.exe", RuleType = RuleType.Restricted }), CancellationToken.None))!;

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Activation_goes_through_the_engine()
    {
        await _h.StartAsync();
        var handler = _h.CreateHandler();

        var early = (ActivationResult)(await handler.HandleAsync(_client, AgentMethods.ActivateReward, null, CancellationToken.None))!;
        await _h.Engine.AddWorkAsync(TimeSpan.FromHours(1), CancellationToken.None);
        var ready = (ActivationResult)(await handler.HandleAsync(_client, AgentMethods.ActivateReward, null, CancellationToken.None))!;

        Assert.Equal(ActivationOutcome.NotReady, early.Outcome);
        Assert.Equal(ActivationOutcome.Activated, ready.Outcome);
    }

    [Fact]
    public async Task Strict_mode_cannot_be_selected_without_the_service()
    {
        await _h.StartAsync();
        var handler = _h.CreateHandler();

        var result = (OperationResult)(await handler.HandleAsync(_client, AgentMethods.UpdateSettings,
            Payload(AppSettings.Default with { EnforcementMode = EnforcementMode.Strict }), CancellationToken.None))!;

        Assert.False(result.Succeeded);
        Assert.Equal(EnforcementMode.CloseThenTerminate, _h.Settings.Current.EnforcementMode);
    }

    [Fact]
    public async Task Unknown_methods_are_rejected()
    {
        await _h.StartAsync();

        await Assert.ThrowsAsync<IpcException>(() => _h.CreateHandler().HandleAsync(_client, "dropTables", null, CancellationToken.None));
    }

    [Fact]
    public async Task Status_round_trips_through_json()
    {
        await _h.StartAsync();
        var status = await _h.CreateHandler().HandleAsync(_client, AgentMethods.GetStatus, null, CancellationToken.None);

        var parsed = IpcJson.FromElement<AgentStatus>(IpcJson.ToElement(status));

        Assert.Equal(RewardState.Working, parsed!.Reward.State);
        Assert.Equal(TimeSpan.FromMinutes(60), parsed.Reward.RequiredWork);
    }
}

public sealed class ProgressServiceTests : IDisposable
{
    private readonly AgentHarness _h = new();

    public void Dispose() => _h.Dispose();

    private ProgressService Create() =>
        new(_h.Statistics, _h.Settings, _h.Notifications, _h.Clock, NullLogger<ProgressService>.Instance) { TimeZone = TimeZoneInfo.Utc };

    private void WorkedToday(int minutes) =>
        _h.Statistics.Days = [new DailyActivity(DateOnly.FromDateTime(_h.Clock.UtcNow.UtcDateTime), TimeSpan.FromMinutes(minutes))];

    [Fact]
    public async Task Notifies_once_when_the_daily_goal_is_crossed()
    {
        await _h.StartAsync();
        var progress = Create();

        WorkedToday(100);
        await progress.GetAsync(CancellationToken.None, refresh: true);
        WorkedToday(125);
        var reached = await progress.GetAsync(CancellationToken.None, refresh: true);
        await progress.GetAsync(CancellationToken.None, refresh: true);

        Assert.True(reached.GoalMet);
        Assert.Single(_h.Tray.Notifications, n => n.Title == "Daily goal reached!");
    }

    [Fact]
    public async Task Starting_on_a_day_already_done_does_not_notify()
    {
        await _h.StartAsync();
        var progress = Create();

        WorkedToday(150);
        await progress.GetAsync(CancellationToken.None, refresh: true);
        await progress.GetAsync(CancellationToken.None, refresh: true);

        Assert.DoesNotContain(_h.Tray.Notifications, n => n.Title == "Daily goal reached!");
    }

    [Fact]
    public async Task Uses_the_daily_goal_setting()
    {
        _h.SettingsStore.Settings = AppSettings.Default with { DailyGoalMinutes = 30 };
        await _h.StartAsync();
        WorkedToday(45);

        var progress = await Create().GetAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(30), progress.Goal);
        Assert.True(progress.GoalMet);
    }
}

public sealed class NotificationServiceTests : IDisposable
{
    private readonly AgentHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Blocked_launch_notifications_are_rate_limited_per_app()
    {
        await _h.StartAsync();
        var steam = _h.Rules.Rules.Single(r => r.Name == "Steam");

        _h.Notifications.OnLaunchBlocked(steam);
        _h.Notifications.OnLaunchBlocked(steam);
        _h.Clock.Advance(NotificationService.BlockedCooldown);
        _h.Notifications.OnLaunchBlocked(steam);

        Assert.Equal(2, _h.Tray.Notifications.Count(n => n.Title == "Steam is locked"));
    }

    [Fact]
    public async Task Reward_events_produce_notifications()
    {
        await _h.StartAsync();

        await _h.Engine.AddWorkAsync(TimeSpan.FromHours(1), CancellationToken.None);
        await _h.Engine.ActivateRewardAsync(CancellationToken.None);
        _h.Notifications.CheckRewardCountdown(_h.Engine.GetStatus());
        _h.Clock.Advance(TimeSpan.FromMinutes(16));
        _h.Notifications.CheckRewardCountdown(_h.Engine.GetStatus());
        _h.Clock.Advance(TimeSpan.FromMinutes(4));
        await _h.Engine.TickAsync(CancellationToken.None);

        Assert.Equal(["Reward earned!", "Apps unlocked", "Reward ending soon", "Reward ended"], _h.Tray.Notifications.Select(n => n.Title));
    }

    [Fact]
    public async Task Notifications_respect_the_setting()
    {
        _h.SettingsStore.Settings = AppSettings.Default with { NotificationsEnabled = false };
        await _h.StartAsync();

        await _h.Engine.AddWorkAsync(TimeSpan.FromHours(1), CancellationToken.None);

        Assert.Empty(_h.Tray.Notifications);
    }
}
