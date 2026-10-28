using Microsoft.Extensions.Logging.Abstractions;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Rewards;

namespace WorkToUnlock.Core.Tests;

public sealed class BreakPauseTests : IDisposable
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

    private async Task<RewardEngine> CreateAsync()
    {
        var engine = new RewardEngine(_store, _clock, NullLogger<RewardEngine>.Instance);
        _engines.Add(engine);
        await engine.InitializeAsync(Defaults, CancellationToken.None);
        return engine;
    }

    private async Task<RewardEngine> CreateOnBreakAsync()
    {
        var engine = await CreateAsync();
        await engine.AddWorkAsync(TimeSpan.FromMinutes(60), CancellationToken.None);
        await engine.ActivateRewardAsync(CancellationToken.None);
        return engine;
    }

    [Fact]
    public async Task A_paused_break_does_not_count_down()
    {
        var engine = await CreateOnBreakAsync();
        _clock.Advance(TimeSpan.FromMinutes(5));

        Assert.True(await engine.PauseAsync("locked", CancellationToken.None));
        _clock.Advance(TimeSpan.FromHours(2));
        Assert.False(await engine.TickAsync(CancellationToken.None));

        var status = engine.GetStatus();
        Assert.True(status.IsPaused);
        Assert.Equal(RewardState.Unlocked, status.State);
        Assert.Equal(TimeSpan.FromMinutes(15), status.RemainingReward);
        Assert.Equal(RewardTransitionKind.Paused, _store.Transitions[^1].Kind);
    }

    [Fact]
    public async Task Resuming_gives_back_exactly_the_time_that_was_left()
    {
        var engine = await CreateOnBreakAsync();
        _clock.Advance(TimeSpan.FromMinutes(5));
        await engine.PauseAsync("locked", CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(40));

        Assert.True(await engine.ResumeAsync(CancellationToken.None));

        var status = engine.GetStatus();
        Assert.False(status.IsPaused);
        Assert.Equal(TimeSpan.FromMinutes(15), status.RemainingReward);
        Assert.Equal(_clock.UtcNow.AddMinutes(15), status.RewardExpiresUtc);

        _clock.Advance(TimeSpan.FromMinutes(15));
        Assert.True(await engine.TickAsync(CancellationToken.None));
        Assert.Equal(RewardState.Working, engine.GetStatus().State);
    }

    [Fact]
    public async Task Moving_the_clock_while_paused_does_not_lengthen_the_break()
    {
        var engine = await CreateOnBreakAsync();
        _clock.Advance(TimeSpan.FromMinutes(10));
        await engine.PauseAsync("locked", CancellationToken.None);

        _clock.UtcNow -= TimeSpan.FromDays(1);
        await engine.ResumeAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(10), engine.GetStatus().RemainingReward);
    }

    [Fact]
    public async Task A_paused_break_survives_a_restart_and_a_reboot()
    {
        var engine = await CreateOnBreakAsync();
        _clock.Advance(TimeSpan.FromMinutes(8));
        await engine.PauseAsync("locked", CancellationToken.None);

        _clock.Reboot(TimeSpan.FromHours(10));
        var restarted = await CreateAsync();

        Assert.True(restarted.GetStatus().IsPaused);
        Assert.Equal(TimeSpan.FromMinutes(12), restarted.GetStatus().RemainingReward);
        await restarted.ResumeAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(11));
        Assert.False(await restarted.TickAsync(CancellationToken.None));
        Assert.Equal(TimeSpan.FromMinutes(1), restarted.GetStatus().RemainingReward);
    }

    [Fact]
    public async Task Pause_and_resume_do_nothing_without_a_running_break()
    {
        var engine = await CreateAsync();

        Assert.False(await engine.PauseAsync("locked", CancellationToken.None));
        Assert.False(await engine.ResumeAsync(CancellationToken.None));
        Assert.False(engine.GetStatus().IsPaused);
    }

    [Fact]
    public async Task Pausing_twice_keeps_the_first_remaining_time()
    {
        var engine = await CreateOnBreakAsync();
        await engine.PauseAsync("locked", CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));

        Assert.False(await engine.PauseAsync("suspended", CancellationToken.None));
        Assert.Equal(TimeSpan.FromMinutes(20), engine.GetStatus().RemainingReward);
    }

    [Fact]
    public async Task Resetting_progress_ends_a_paused_break()
    {
        var engine = await CreateOnBreakAsync();
        await engine.PauseAsync("locked", CancellationToken.None);

        await engine.ResetProgressAsync("test", CancellationToken.None);

        Assert.Equal(RewardState.Working, engine.GetStatus().State);
        Assert.False(engine.GetStatus().IsPaused);
        Assert.Null(_store.Stored!.PausedRemainingMilliseconds);
    }
}
