using Microsoft.Extensions.Logging.Abstractions;
using WorkToUnlock.Core.Activity;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Rewards;
using WorkToUnlock.Core.Rules;
using WorkToUnlock.Core.Tracking;

namespace WorkToUnlock.Core.Tests;

public sealed class WorkTrackerTests : IAsyncLifetime, IDisposable
{
    private readonly FakeClock _clock = new();
    private readonly InMemoryRewardStateStore _rewardStore = new();
    private readonly InMemoryWorkSessionStore _sessions = new();
    private readonly InMemoryRuleStore _ruleStore = new(
        TestRules.Work(1, "Visual Studio Code", TestRules.VsCodePath),
        TestRules.Work(2, "Blockbench", TestRules.BlockbenchPath),
        TestRules.Work(3, "Notes", @"C:\Tools\notes.exe", countsTowardGoal: false),
        TestRules.Work(4, "Old editor", @"C:\Tools\old.exe", enabled: false),
        TestRules.Restricted(10, "Steam", TestRules.SteamPath));

    private readonly RewardEngine _engine;
    private readonly RuleCatalog _catalog;
    private readonly WorkTracker _tracker;

    public WorkTrackerTests()
    {
        _engine = new RewardEngine(_rewardStore, _clock, NullLogger<RewardEngine>.Instance);
        _catalog = new RuleCatalog(_ruleStore, _clock);
        _tracker = new WorkTracker(_engine, _catalog, new ApplicationRuleMatcher(new FakeSignatureReader()), _sessions, NullLogger<WorkTracker>.Instance);
    }

    public async Task InitializeAsync()
    {
        await _catalog.LoadAsync(CancellationToken.None);
        await _engine.InitializeAsync(new RewardSettings(TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(20)), CancellationToken.None);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _tracker.Dispose();
        _engine.Dispose();
    }

    private Task Sample(string? path, double idleSeconds = 0, SessionState session = SessionState.Active, bool minimized = false) =>
        _tracker.ProcessActivityAsync(
            new ActivitySample(
                _clock.MonotonicMilliseconds,
                _clock.UtcNow,
                path is null ? null : new ForegroundAppInfo(1234, path, minimized),
                TimeSpan.FromSeconds(idleSeconds),
                session),
            CancellationToken.None);

    /// <summary>Samples every 500 ms for the given duration, starting with a sample at the current time.</summary>
    private async Task RunAsync(string? path, TimeSpan duration, double idleSeconds = 0, SessionState session = SessionState.Active, bool minimized = false)
    {
        var steps = (int)(duration.TotalMilliseconds / 500);
        await Sample(path, idleSeconds, session, minimized);
        for (var i = 0; i < steps; i++)
        {
            _clock.AdvanceMilliseconds(500);
            await Sample(path, idleSeconds, session, minimized);
        }
    }

    private TimeSpan Accumulated => _engine.GetStatus().AccumulatedWork;

    [Fact]
    public async Task Foreground_time_in_an_approved_app_accumulates()
    {
        await RunAsync(TestRules.VsCodePath, TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.FromSeconds(10), Accumulated);
        Assert.Equal(ActivityStatus.Working, _tracker.Current.Status);
        Assert.Equal("Visual Studio Code", _tracker.Current.CurrentApplicationName);
    }

    [Fact]
    public async Task Process_names_alone_do_not_match()
    {
        await RunAsync(@"C:\Users\dev\Downloads\Code.exe", TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.Zero, Accumulated);
        Assert.Equal(ActivityStatus.NotWorkApp, _tracker.Current.Status);
    }

    [Fact]
    public async Task Switching_between_approved_apps_keeps_accumulating()
    {
        await RunAsync(TestRules.VsCodePath, TimeSpan.FromSeconds(10));
        _clock.AdvanceMilliseconds(500);
        await RunAsync(TestRules.BlockbenchPath, TimeSpan.FromSeconds(10));

        // The 500 ms interval spanning the switch is credited to VS Code: both endpoints qualify.
        Assert.Equal(TimeSpan.FromSeconds(20.5), Accumulated);
    }

    [Fact]
    public async Task Switching_to_an_unapproved_app_pauses_tracking()
    {
        await RunAsync(TestRules.VsCodePath, TimeSpan.FromSeconds(10));
        _clock.AdvanceMilliseconds(500);
        await RunAsync(TestRules.ChromePath, TimeSpan.FromSeconds(30));
        _clock.AdvanceMilliseconds(500);
        await RunAsync(TestRules.VsCodePath, TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromSeconds(15), Accumulated);
    }

    [Fact]
    public async Task Restricted_apps_never_count_as_work()
    {
        await RunAsync(TestRules.SteamPath, TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.Zero, Accumulated);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(59.5, true)]
    [InlineData(60, false)]
    [InlineData(600, false)]
    public async Task Idle_threshold_boundary(double idleSeconds, bool counts)
    {
        await RunAsync(TestRules.VsCodePath, TimeSpan.FromSeconds(10), idleSeconds);

        Assert.Equal(counts ? TimeSpan.FromSeconds(10) : TimeSpan.Zero, Accumulated);
        Assert.Equal(counts ? ActivityStatus.Working : ActivityStatus.Idle, _tracker.Current.Status);
    }

    [Fact]
    public async Task Idle_threshold_follows_updated_options()
    {
        _tracker.UpdateOptions(new TrackingOptions { IdleThreshold = TimeSpan.FromSeconds(30) });

        await RunAsync(TestRules.VsCodePath, TimeSpan.FromSeconds(10), idleSeconds: 45);

        Assert.Equal(TimeSpan.Zero, Accumulated);
    }

    [Theory]
    [InlineData(SessionState.Locked, ActivityStatus.Locked)]
    [InlineData(SessionState.Disconnected, ActivityStatus.Disconnected)]
    [InlineData(SessionState.Suspended, ActivityStatus.Suspended)]
    public async Task Inactive_sessions_do_not_count(SessionState session, ActivityStatus expected)
    {
        await RunAsync(TestRules.VsCodePath, TimeSpan.FromSeconds(10), session: session);

        Assert.Equal(TimeSpan.Zero, Accumulated);
        Assert.Equal(expected, _tracker.Current.Status);
    }

    [Fact]
    public async Task Lock_and_unlock_only_counts_unlocked_time()
    {
        await RunAsync(TestRules.VsCodePath, TimeSpan.FromSeconds(5));
        _clock.AdvanceMilliseconds(500);
        await RunAsync(TestRules.VsCodePath, TimeSpan.FromSeconds(60), session: SessionState.Locked);
        _clock.AdvanceMilliseconds(500);
        await RunAsync(TestRules.VsCodePath, TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromSeconds(10), Accumulated);
    }

    [Fact]
    public async Task Minimized_apps_do_not_count()
    {
        await RunAsync(TestRules.VsCodePath, TimeSpan.FromSeconds(10), minimized: true);

        Assert.Equal(TimeSpan.Zero, Accumulated);
        Assert.Equal(ActivityStatus.NoForeground, _tracker.Current.Status);
    }

    [Fact]
    public async Task No_foreground_window_does_not_count()
    {
        await RunAsync(null, TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.Zero, Accumulated);
    }

    [Fact]
    public async Task Disabled_rules_do_not_count()
    {
        await RunAsync(@"C:\Tools\old.exe", TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.Zero, Accumulated);
    }

    [Fact]
    public async Task Gaps_longer_than_the_maximum_are_not_credited()
    {
        await Sample(TestRules.VsCodePath);
        _clock.Advance(TimeSpan.FromMinutes(30)); // suspended, or the agent was starved
        await Sample(TestRules.VsCodePath);
        _clock.AdvanceMilliseconds(500);
        await Sample(TestRules.VsCodePath);

        Assert.Equal(TimeSpan.FromMilliseconds(500), Accumulated);
    }

    [Fact]
    public async Task Irregular_polling_credits_actual_elapsed_time()
    {
        long[] intervals = [300, 900, 1700, 450, 4999];
        await Sample(TestRules.VsCodePath);
        foreach (var interval in intervals)
        {
            _clock.AdvanceMilliseconds(interval);
            await Sample(TestRules.VsCodePath);
        }

        Assert.Equal(TimeSpan.FromMilliseconds(intervals.Sum()), Accumulated);
    }

    [Fact]
    public async Task Repeated_samples_at_the_same_instant_are_not_double_counted()
    {
        await Sample(TestRules.VsCodePath);
        _clock.AdvanceMilliseconds(500);
        await Sample(TestRules.VsCodePath);
        await Sample(TestRules.VsCodePath);
        await Sample(TestRules.VsCodePath);

        Assert.Equal(TimeSpan.FromMilliseconds(500), Accumulated);
    }

    [Fact]
    public async Task Apps_not_counting_toward_the_goal_are_recorded_but_not_credited()
    {
        await RunAsync(@"C:\Tools\notes.exe", TimeSpan.FromSeconds(10));
        await _tracker.FlushAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.Zero, Accumulated);
        var session = Assert.Single(_sessions.Sessions.Values);
        Assert.Equal(3, session.ApplicationRuleId);
        Assert.Equal(10_000, session.QualifyingMilliseconds);
    }

    [Fact]
    public async Task Work_sessions_are_recorded_per_application()
    {
        await RunAsync(TestRules.VsCodePath, TimeSpan.FromSeconds(10));
        _clock.AdvanceMilliseconds(500);
        await RunAsync(TestRules.BlockbenchPath, TimeSpan.FromSeconds(4));
        _clock.AdvanceMilliseconds(500);
        await Sample(TestRules.ChromePath);

        var sessions = _sessions.Sessions.Values.OrderBy(s => s.Id).ToList();
        Assert.Equal(2, sessions.Count);
        Assert.Equal((1L, 10_500L), (sessions[0].ApplicationRuleId, sessions[0].QualifyingMilliseconds));
        Assert.Equal((2L, 4_000L), (sessions[1].ApplicationRuleId, sessions[1].QualifyingMilliseconds));
    }

    [Fact]
    public async Task Progress_is_persisted_periodically()
    {
        await RunAsync(TestRules.VsCodePath, TimeSpan.FromSeconds(16));

        Assert.NotNull(_rewardStore.Stored);
        Assert.True(_rewardStore.Stored!.AccumulatedWorkMilliseconds >= 15_000);
        Assert.Single(_sessions.Sessions);
    }

    [Fact]
    public async Task Persistence_failures_are_reported_and_recovered()
    {
        _sessions.FailNextWrites = 1;
        await RunAsync(TestRules.VsCodePath, TimeSpan.FromSeconds(16));
        Assert.False(_tracker.PersistenceHealthy);

        await RunAsync(TestRules.VsCodePath, TimeSpan.FromSeconds(16));

        Assert.True(_tracker.PersistenceHealthy);
        Assert.Equal(TimeSpan.FromSeconds(32), Accumulated);
    }

    [Fact]
    public async Task Failed_reward_transition_does_not_double_count()
    {
        await _engine.AddWorkAsync(TimeSpan.FromMinutes(60) - TimeSpan.FromSeconds(1), CancellationToken.None);
        _rewardStore.FailNextSaves = 1;

        await RunAsync(TestRules.VsCodePath, TimeSpan.FromSeconds(3));

        Assert.Equal(RewardState.RewardReady, _engine.GetStatus().State);
        Assert.Equal(1, _rewardStore.Transitions.Count(t => t.Kind == RewardTransitionKind.Earned));
    }

    [Fact]
    public async Task Sixty_minutes_of_mixed_work_unlocks_a_reward()
    {
        await RunAsync(TestRules.VsCodePath, TimeSpan.FromMinutes(20));
        _clock.Advance(TimeSpan.FromMinutes(5));
        await RunAsync(TestRules.BlockbenchPath, TimeSpan.FromMinutes(15));
        _clock.Advance(TimeSpan.FromMinutes(5));
        await RunAsync(TestRules.VsCodePath, TimeSpan.FromMinutes(25));

        Assert.Equal(RewardState.RewardReady, _engine.GetStatus().State);
    }
}
