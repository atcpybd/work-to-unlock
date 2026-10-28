using Microsoft.Extensions.Logging.Abstractions;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Rewards;

namespace WorkToUnlock.Core.Tests;

public sealed class BalanceModeTests : IDisposable
{
    // 60 minutes of work earn 20 minutes of break: one minute of break per three minutes of work.
    private static readonly RewardSettings Goal = new(TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(20));
    private static readonly RewardSettings Balance = Goal with { Mode = RewardMode.Balance, BalanceCap = TimeSpan.FromMinutes(30) };
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

    private async Task<RewardEngine> CreateAsync(RewardSettings settings)
    {
        var engine = new RewardEngine(_store, _clock, NullLogger<RewardEngine>.Instance);
        _engines.Add(engine);
        await engine.InitializeAsync(settings, CancellationToken.None);
        return engine;
    }

    [Fact]
    public async Task Work_saves_break_time_in_proportion()
    {
        var engine = await CreateAsync(Balance);

        await engine.AddWorkAsync(TimeSpan.FromMinutes(15), CancellationToken.None);

        var status = engine.GetStatus();
        Assert.Equal(RewardState.Working, status.State);
        Assert.Equal(TimeSpan.FromMinutes(5), status.Balance);
        Assert.True(status.CanActivate);
    }

    [Fact]
    public async Task Saved_time_survives_a_restart_once_flushed()
    {
        var engine = await CreateAsync(Balance);
        await engine.AddWorkAsync(TimeSpan.FromMinutes(30), CancellationToken.None);
        await engine.FlushAsync(CancellationToken.None);

        var restarted = await CreateAsync(Balance);

        Assert.Equal(TimeSpan.FromMinutes(10), restarted.GetStatus().Balance);
        Assert.Equal(0, _store.Stored!.AccumulatedWorkMilliseconds);
    }

    [Fact]
    public async Task Saving_stops_at_the_limit()
    {
        var engine = await CreateAsync(Balance);

        await engine.AddWorkAsync(TimeSpan.FromHours(5), CancellationToken.None);
        await engine.FlushAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(30), engine.GetStatus().Balance);
        Assert.Equal(RewardState.Working, engine.GetStatus().State);
    }

    [Fact]
    public async Task Less_than_a_minute_cannot_be_spent()
    {
        var engine = await CreateAsync(Balance);
        await engine.AddWorkAsync(TimeSpan.FromMinutes(2), CancellationToken.None);

        var result = await engine.ActivateRewardAsync(CancellationToken.None);

        Assert.Equal(ActivationOutcome.NotReady, result.Outcome);
    }

    [Fact]
    public async Task Starting_a_break_spends_the_whole_balance()
    {
        var engine = await CreateAsync(Balance);
        await engine.AddWorkAsync(TimeSpan.FromMinutes(36), CancellationToken.None);

        var result = await engine.ActivateRewardAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        var status = engine.GetStatus();
        Assert.Equal(RewardState.Unlocked, status.State);
        Assert.Equal(TimeSpan.FromMinutes(12), status.RemainingReward);
        Assert.Equal(TimeSpan.Zero, status.Balance);
        Assert.True(status.CanEndEarly);
    }

    [Fact]
    public async Task Ending_early_saves_the_time_left()
    {
        var engine = await CreateAsync(Balance);
        await engine.AddWorkAsync(TimeSpan.FromMinutes(36), CancellationToken.None);
        await engine.ActivateRewardAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));

        Assert.True(await engine.EndBreakEarlyAsync(CancellationToken.None));

        var status = engine.GetStatus();
        Assert.Equal(RewardState.Working, status.State);
        Assert.True(status.RestrictionsActive);
        Assert.Equal(TimeSpan.FromMinutes(7), status.Balance);
        Assert.Equal(RewardTransitionKind.EndedEarly, _store.Transitions[^1].Kind);
    }

    [Fact]
    public async Task A_finished_break_leaves_nothing_saved()
    {
        var engine = await CreateAsync(Balance);
        await engine.AddWorkAsync(TimeSpan.FromMinutes(30), CancellationToken.None);
        await engine.ActivateRewardAsync(CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(await engine.TickAsync(CancellationToken.None));

        Assert.Equal(RewardState.Working, engine.GetStatus().State);
        Assert.Equal(TimeSpan.Zero, engine.GetStatus().Balance);
    }

    [Fact]
    public async Task Ending_early_is_only_for_balance_mode()
    {
        var engine = await CreateAsync(Goal);
        await engine.AddWorkAsync(TimeSpan.FromMinutes(60), CancellationToken.None);
        await engine.ActivateRewardAsync(CancellationToken.None);

        Assert.False(await engine.EndBreakEarlyAsync(CancellationToken.None));
        Assert.Equal(RewardState.Unlocked, engine.GetStatus().State);
    }

    [Fact]
    public async Task Switching_to_balance_saves_partial_progress()
    {
        var engine = await CreateAsync(Goal);
        await engine.AddWorkAsync(TimeSpan.FromMinutes(45), CancellationToken.None);
        await engine.FlushAsync(CancellationToken.None);

        await engine.UpdateSettingsAsync(Balance, CancellationToken.None);

        var status = engine.GetStatus();
        Assert.Equal(RewardMode.Balance, status.Mode);
        Assert.Equal(TimeSpan.FromMinutes(15), status.Balance);
        Assert.Equal(RewardTransitionKind.ModeChanged, _store.Transitions[^1].Kind);
    }

    [Fact]
    public async Task Switching_to_balance_with_a_reward_ready_saves_the_whole_reward()
    {
        var engine = await CreateAsync(Goal);
        await engine.AddWorkAsync(TimeSpan.FromMinutes(60), CancellationToken.None);

        await engine.UpdateSettingsAsync(Balance, CancellationToken.None);

        var status = engine.GetStatus();
        Assert.Equal(RewardState.Working, status.State);
        Assert.Equal(TimeSpan.FromMinutes(20), status.Balance);
    }

    [Fact]
    public async Task Switching_to_goal_counts_saved_time_as_work()
    {
        var engine = await CreateAsync(Balance);
        await engine.AddWorkAsync(TimeSpan.FromMinutes(30), CancellationToken.None);
        await engine.FlushAsync(CancellationToken.None);

        await engine.UpdateSettingsAsync(Goal, CancellationToken.None);

        var status = engine.GetStatus();
        Assert.Equal(RewardMode.Goal, status.Mode);
        Assert.Equal(TimeSpan.FromMinutes(30), status.AccumulatedWork);
        Assert.Equal(0, _store.Stored!.BalanceMilliseconds);
    }

    [Fact]
    public async Task Switching_to_goal_with_enough_saved_time_makes_the_reward_ready()
    {
        var engine = await CreateAsync(Balance);
        await engine.AddWorkAsync(TimeSpan.FromMinutes(70), CancellationToken.None);
        await engine.FlushAsync(CancellationToken.None);

        await engine.UpdateSettingsAsync(Goal, CancellationToken.None);

        Assert.Equal(RewardState.RewardReady, engine.GetStatus().State);
    }

    [Fact]
    public async Task Unsaved_work_carries_over_when_switching_modes()
    {
        var engine = await CreateAsync(Balance);
        await engine.AddWorkAsync(TimeSpan.FromMinutes(9), CancellationToken.None);

        await engine.UpdateSettingsAsync(Goal, CancellationToken.None);
        Assert.Equal(TimeSpan.FromMinutes(9), engine.GetStatus().AccumulatedWork);

        await engine.UpdateSettingsAsync(Balance, CancellationToken.None);
        Assert.Equal(TimeSpan.FromMinutes(3), engine.GetStatus().Balance);
    }

    [Fact]
    public async Task Changing_other_settings_in_balance_mode_is_not_a_mode_change()
    {
        var engine = await CreateAsync(Balance);
        await engine.AddWorkAsync(TimeSpan.FromMinutes(9), CancellationToken.None);

        await engine.UpdateSettingsAsync(Balance with { BalanceCap = TimeSpan.FromMinutes(45) }, CancellationToken.None);

        Assert.DoesNotContain(_store.Transitions, t => t.Kind == RewardTransitionKind.ModeChanged);
        Assert.Equal(TimeSpan.FromMinutes(3), engine.GetStatus().Balance);
    }

    [Fact]
    public async Task A_running_break_continues_when_the_mode_changes()
    {
        var engine = await CreateAsync(Goal);
        await engine.AddWorkAsync(TimeSpan.FromMinutes(60), CancellationToken.None);
        await engine.ActivateRewardAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));

        await engine.UpdateSettingsAsync(Balance, CancellationToken.None);

        var status = engine.GetStatus();
        Assert.Equal(RewardState.Unlocked, status.State);
        Assert.Equal(TimeSpan.FromMinutes(15), status.RemainingReward);
        Assert.True(await engine.EndBreakEarlyAsync(CancellationToken.None));
        Assert.Equal(TimeSpan.FromMinutes(15), engine.GetStatus().Balance);
    }

    [Fact]
    public async Task Resetting_clears_the_balance()
    {
        var engine = await CreateAsync(Balance);
        await engine.AddWorkAsync(TimeSpan.FromMinutes(30), CancellationToken.None);
        await engine.FlushAsync(CancellationToken.None);

        await engine.ResetProgressAsync("test", CancellationToken.None);

        Assert.Equal(TimeSpan.Zero, engine.GetStatus().Balance);
    }
}
