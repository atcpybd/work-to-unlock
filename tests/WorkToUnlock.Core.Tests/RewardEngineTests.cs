using Microsoft.Extensions.Logging.Abstractions;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Persistence;
using WorkToUnlock.Core.Rewards;

namespace WorkToUnlock.Core.Tests;

public sealed class RewardEngineTests : IDisposable
{
    private static readonly RewardSettings Defaults = new(TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(20));
    private readonly FakeClock _clock = new();
    private readonly InMemoryRewardStateStore _store = new();
    private readonly List<RewardEngine> _engines = [];

    public void Dispose()
    {
        foreach (var engine in _engines)
        {
            engine.Dispose();
        }
    }

    private async Task<RewardEngine> CreateAsync(RewardSettings? settings = null)
    {
        var engine = new RewardEngine(_store, _clock, NullLogger<RewardEngine>.Instance);
        _engines.Add(engine);
        await engine.InitializeAsync(settings ?? Defaults, CancellationToken.None);
        return engine;
    }

    private async Task<RewardEngine> CreateReadyAsync()
    {
        var engine = await CreateAsync();
        await engine.AddWorkAsync(TimeSpan.FromMinutes(60), CancellationToken.None);
        Assert.Equal(RewardState.RewardReady, engine.GetStatus().State);
        return engine;
    }

    [Fact]
    public async Task New_engine_starts_in_working_state_with_no_progress()
    {
        var engine = await CreateAsync();
        var status = engine.GetStatus();

        Assert.Equal(RewardState.Working, status.State);
        Assert.Equal(TimeSpan.Zero, status.AccumulatedWork);
        Assert.Equal(TimeSpan.FromMinutes(60), status.RemainingWork);
        Assert.True(status.RestrictionsActive);
        Assert.False(status.CanActivate);
    }

    [Fact]
    public async Task Work_accumulates_across_sessions_until_target_is_reached()
    {
        var engine = await CreateAsync();

        await engine.AddWorkAsync(TimeSpan.FromMinutes(20), CancellationToken.None);
        await engine.AddWorkAsync(TimeSpan.FromMinutes(15), CancellationToken.None);
        Assert.Equal(RewardState.Working, engine.GetStatus().State);
        Assert.Equal(TimeSpan.FromMinutes(35), engine.GetStatus().AccumulatedWork);

        await engine.AddWorkAsync(TimeSpan.FromMinutes(25), CancellationToken.None);

        var status = engine.GetStatus();
        Assert.Equal(RewardState.RewardReady, status.State);
        Assert.True(status.CanActivate);
        Assert.True(status.RestrictionsActive);
        Assert.Equal(_clock.UtcNow, status.RewardEarnedUtc);
        Assert.Equal(RewardTransitionKind.Earned, Assert.Single(_store.Transitions).Kind);
    }

    [Fact]
    public async Task Additional_work_after_reward_is_ready_does_not_stack()
    {
        var engine = await CreateReadyAsync();

        await engine.AddWorkAsync(TimeSpan.FromMinutes(90), CancellationToken.None);

        var status = engine.GetStatus();
        Assert.Equal(RewardState.RewardReady, status.State);
        Assert.Equal(TimeSpan.FromMinutes(60), status.AccumulatedWork);
        Assert.Single(_store.Transitions, t => t.Kind == RewardTransitionKind.Earned);
    }

    [Fact]
    public async Task Activation_before_target_is_rejected()
    {
        var engine = await CreateAsync();
        await engine.AddWorkAsync(TimeSpan.FromMinutes(59), CancellationToken.None);

        var result = await engine.ActivateRewardAsync(CancellationToken.None);

        Assert.Equal(ActivationOutcome.NotReady, result.Outcome);
        Assert.Equal(RewardState.Working, engine.GetStatus().State);
    }

    [Fact]
    public async Task Activation_unlocks_for_the_configured_duration()
    {
        var engine = await CreateReadyAsync();

        var result = await engine.ActivateRewardAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        var status = engine.GetStatus();
        Assert.Equal(RewardState.Unlocked, status.State);
        Assert.False(status.RestrictionsActive);
        Assert.Equal(TimeSpan.FromMinutes(20), status.RemainingReward);
        Assert.Equal(_clock.UtcNow.AddMinutes(20), status.RewardExpiresUtc);
        Assert.Equal(RewardState.Unlocked, _store.Stored!.State);
        Assert.Equal(_clock.BootId, _store.Stored.BootId);
    }

    [Fact]
    public async Task Work_does_not_accumulate_during_a_reward()
    {
        var engine = await CreateReadyAsync();
        await engine.ActivateRewardAsync(CancellationToken.None);

        await engine.AddWorkAsync(TimeSpan.FromMinutes(10), CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(20));
        await engine.TickAsync(CancellationToken.None);

        Assert.Equal(RewardState.Working, engine.GetStatus().State);
        Assert.Equal(TimeSpan.Zero, engine.GetStatus().AccumulatedWork);
    }

    [Fact]
    public async Task Reward_does_not_expire_before_its_duration()
    {
        var engine = await CreateReadyAsync();
        await engine.ActivateRewardAsync(CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(19) + TimeSpan.FromSeconds(59));
        var changed = await engine.TickAsync(CancellationToken.None);

        Assert.False(changed);
        Assert.Equal(RewardState.Unlocked, engine.GetStatus().State);
        Assert.Equal(TimeSpan.FromSeconds(1), engine.GetStatus().RemainingReward);
    }

    [Fact]
    public async Task Reward_expires_after_its_duration_and_progress_resets()
    {
        var engine = await CreateReadyAsync();
        await engine.ActivateRewardAsync(CancellationToken.None);
        RewardStatus? published = null;
        engine.StateChanged += (_, s) => published = s;

        _clock.Advance(TimeSpan.FromMinutes(20));
        var changed = await engine.TickAsync(CancellationToken.None);

        Assert.True(changed);
        var status = engine.GetStatus();
        Assert.Equal(RewardState.Working, status.State);
        Assert.True(status.RestrictionsActive);
        Assert.Equal(TimeSpan.Zero, status.AccumulatedWork);
        Assert.Null(status.RewardExpiresUtc);
        Assert.Equal(RewardState.Working, published!.State);
        Assert.Equal(RewardTransitionKind.Expired, _store.Transitions[^1].Kind);
    }

    [Fact]
    public async Task Reward_time_keeps_elapsing_while_the_pc_sleeps()
    {
        var engine = await CreateReadyAsync();
        await engine.ActivateRewardAsync(CancellationToken.None);

        // GetTickCount64 keeps counting during sleep, and so does UTC.
        _clock.Advance(TimeSpan.FromMinutes(45));

        Assert.True(await engine.TickAsync(CancellationToken.None));
        Assert.Equal(RewardState.Working, engine.GetStatus().State);
    }

    [Fact]
    public async Task Concurrent_activation_requests_activate_exactly_once()
    {
        var engine = await CreateReadyAsync();
        _store.SaveDelay = TimeSpan.FromMilliseconds(30);

        var results = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => Task.Run(() => engine.ActivateRewardAsync(CancellationToken.None))));

        Assert.Single(results, r => r.Outcome == ActivationOutcome.Activated);
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Equal(ActivationOutcome.AlreadyActive, r.Outcome));
        Assert.Single(_store.Transitions, t => t.Kind == RewardTransitionKind.Activated);
    }

    [Fact]
    public async Task State_is_durable_before_listeners_are_notified()
    {
        var engine = await CreateReadyAsync();
        RewardState? storedWhenNotified = null;
        engine.StateChanged += (_, _) => storedWhenNotified = _store.Stored?.State;

        await engine.ActivateRewardAsync(CancellationToken.None);

        Assert.Equal(RewardState.Unlocked, storedWhenNotified);
    }

    [Fact]
    public async Task Flushed_progress_survives_a_restart()
    {
        var engine = await CreateAsync();
        await engine.AddWorkAsync(TimeSpan.FromMinutes(42), CancellationToken.None);
        await engine.FlushAsync(CancellationToken.None);

        var restarted = await CreateAsync();

        Assert.Equal(TimeSpan.FromMinutes(42), restarted.GetStatus().AccumulatedWork);
        Assert.Equal(RewardState.Working, restarted.GetStatus().State);
    }

    [Fact]
    public async Task Reward_ready_survives_a_restart()
    {
        await CreateReadyAsync();

        var restarted = await CreateAsync();

        Assert.Equal(RewardState.RewardReady, restarted.GetStatus().State);
        Assert.True(restarted.GetStatus().CanActivate);
    }

    [Fact]
    public async Task Active_reward_restored_in_the_same_boot_keeps_its_monotonic_deadline()
    {
        var engine = await CreateReadyAsync();
        await engine.ActivateRewardAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var restarted = await CreateAsync();

        Assert.Equal(RewardState.Unlocked, restarted.GetStatus().State);
        Assert.Equal(TimeSpan.FromMinutes(15), restarted.GetStatus().RemainingReward);
    }

    [Fact]
    public async Task Active_reward_restored_after_a_reboot_uses_utc_time()
    {
        var engine = await CreateReadyAsync();
        await engine.ActivateRewardAsync(CancellationToken.None);

        _clock.Reboot(TimeSpan.FromMinutes(8));
        var restarted = await CreateAsync();

        Assert.Equal(RewardState.Unlocked, restarted.GetStatus().State);
        Assert.Equal(TimeSpan.FromMinutes(12), restarted.GetStatus().RemainingReward);
    }

    [Fact]
    public async Task Reward_that_elapsed_while_the_agent_was_stopped_expires_on_start()
    {
        var engine = await CreateReadyAsync();
        await engine.ActivateRewardAsync(CancellationToken.None);

        _clock.Reboot(TimeSpan.FromHours(3));
        var restarted = await CreateAsync();

        Assert.Equal(RewardState.Working, restarted.GetStatus().State);
        Assert.Equal(RewardTransitionKind.Expired, _store.Transitions[^1].Kind);
    }

    [Fact]
    public async Task Moving_the_clock_backwards_does_not_extend_a_running_reward()
    {
        var engine = await CreateReadyAsync();
        await engine.ActivateRewardAsync(CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(10));
        _clock.UtcNow -= TimeSpan.FromHours(2);

        Assert.Equal(TimeSpan.FromMinutes(10), engine.GetStatus().RemainingReward);
        _clock.MonotonicMilliseconds += (long)TimeSpan.FromMinutes(10).TotalMilliseconds;
        Assert.True(await engine.TickAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Moving_the_clock_forwards_ends_the_reward_conservatively()
    {
        var engine = await CreateReadyAsync();
        await engine.ActivateRewardAsync(CancellationToken.None);

        _clock.UtcNow += TimeSpan.FromHours(1);

        Assert.True(await engine.TickAsync(CancellationToken.None));
        Assert.Equal(RewardState.Working, engine.GetStatus().State);
    }

    [Fact]
    public async Task Clock_set_before_activation_after_reboot_ends_the_reward()
    {
        var engine = await CreateReadyAsync();
        await engine.ActivateRewardAsync(CancellationToken.None);

        _clock.Reboot(TimeSpan.Zero);
        _clock.UtcNow -= TimeSpan.FromDays(1);
        var restarted = await CreateAsync();

        Assert.Equal(RewardState.Working, restarted.GetStatus().State);
    }

    [Fact]
    public async Task Lowering_the_requirement_can_complete_the_goal()
    {
        var engine = await CreateAsync();
        await engine.AddWorkAsync(TimeSpan.FromMinutes(40), CancellationToken.None);

        await engine.UpdateSettingsAsync(new RewardSettings(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(20)), CancellationToken.None);

        Assert.Equal(RewardState.RewardReady, engine.GetStatus().State);
    }

    [Fact]
    public async Task Changing_the_reward_duration_does_not_change_an_active_reward()
    {
        var engine = await CreateReadyAsync();
        await engine.ActivateRewardAsync(CancellationToken.None);

        await engine.UpdateSettingsAsync(new RewardSettings(TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(90)), CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(20), engine.GetStatus().RemainingReward);
        Assert.Equal(TimeSpan.FromMinutes(20), engine.GetStatus().RewardDuration);
    }

    [Fact]
    public async Task Reset_clears_progress_and_ends_an_active_reward()
    {
        var engine = await CreateReadyAsync();
        await engine.ActivateRewardAsync(CancellationToken.None);

        await engine.ResetProgressAsync("test", CancellationToken.None);

        Assert.Equal(RewardState.Working, engine.GetStatus().State);
        Assert.Equal(TimeSpan.Zero, engine.GetStatus().AccumulatedWork);
        Assert.Equal(RewardTransitionKind.Reset, _store.Transitions[^1].Kind);
    }

    [Fact]
    public async Task Invalid_persisted_state_falls_back_to_working()
    {
        _store.Stored = new RewardStateRecord { State = (RewardState)42, AccumulatedWorkMilliseconds = 5000, Version = 3 };

        var engine = await CreateAsync();

        Assert.Equal(RewardState.Working, engine.GetStatus().State);
        Assert.True(engine.GetStatus().RestrictionsActive);
    }

    [Fact]
    public async Task Unlocked_state_without_timestamps_is_ended()
    {
        _store.Stored = new RewardStateRecord { State = RewardState.Unlocked, Version = 1 };

        var engine = await CreateAsync();

        Assert.Equal(RewardState.Working, engine.GetStatus().State);
    }

    [Fact]
    public async Task Failed_save_keeps_the_credit_and_retries_the_transition()
    {
        var engine = await CreateAsync();
        _store.FailNextSaves = 1;

        await Assert.ThrowsAsync<PersistenceException>(() => engine.AddWorkAsync(TimeSpan.FromMinutes(60), CancellationToken.None));
        Assert.Equal(RewardState.Working, engine.GetStatus().State);

        await engine.AddWorkAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal(RewardState.RewardReady, engine.GetStatus().State);
    }

    [Fact]
    public async Task Flush_writes_pending_progress_once()
    {
        var engine = await CreateAsync();
        await engine.AddWorkAsync(TimeSpan.FromMinutes(1), CancellationToken.None);
        var before = _store.SaveCount;

        await engine.FlushAsync(CancellationToken.None);
        await engine.FlushAsync(CancellationToken.None);

        Assert.Equal(before + 1, _store.SaveCount);
        Assert.Equal(60_000, _store.Stored!.AccumulatedWorkMilliseconds);
    }
}
