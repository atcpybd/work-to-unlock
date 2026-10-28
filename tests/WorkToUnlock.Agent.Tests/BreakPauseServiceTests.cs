using Microsoft.Extensions.Logging.Abstractions;
using WorkToUnlock.Agent.Services;
using WorkToUnlock.Core.Activity;
using WorkToUnlock.Core.Domain;

namespace WorkToUnlock.Agent.Tests;

public sealed class BreakPauseServiceTests : IDisposable
{
    private readonly AgentHarness _h = new();
    private readonly FakeSession _session = new();
    private readonly BreakPauseService _service;

    public BreakPauseServiceTests()
    {
        _service = new BreakPauseService(_session, _h.Engine, _h.Settings, NullLogger<BreakPauseService>.Instance);
    }

    public void Dispose() => _h.Dispose();

    private async Task StartOnBreakAsync(bool pauseWhileAway = true)
    {
        _h.SettingsStore.Settings = AppSettings.Default with { PauseBreakWhileLocked = pauseWhileAway };
        await _h.StartAsync();
        _service.Start();
        await _h.Engine.AddWorkAsync(TimeSpan.FromHours(1), CancellationToken.None);
        await _h.Engine.ActivateRewardAsync(CancellationToken.None);
        await _service.LastEvaluation;
    }

    private async Task SetSessionAsync(SessionState state)
    {
        _session.Set(state);
        await _service.LastEvaluation;
    }

    [Fact]
    public async Task Locking_pauses_and_unlocking_resumes_the_break()
    {
        await StartOnBreakAsync();
        _h.Clock.Advance(TimeSpan.FromMinutes(5));

        await SetSessionAsync(SessionState.Locked);
        Assert.True(_h.Engine.GetStatus().IsPaused);

        _h.Clock.Advance(TimeSpan.FromHours(1));
        await SetSessionAsync(SessionState.Active);

        var status = _h.Engine.GetStatus();
        Assert.False(status.IsPaused);
        Assert.Equal(TimeSpan.FromMinutes(15), status.RemainingReward);
    }

    [Fact]
    public async Task Sleep_pauses_the_break_too()
    {
        await StartOnBreakAsync();

        await SetSessionAsync(SessionState.Suspended);

        Assert.True(_h.Engine.GetStatus().IsPaused);
    }

    [Fact]
    public async Task Nothing_pauses_when_the_setting_is_off()
    {
        await StartOnBreakAsync(pauseWhileAway: false);

        await SetSessionAsync(SessionState.Locked);

        Assert.False(_h.Engine.GetStatus().IsPaused);
    }

    [Fact]
    public async Task Turning_the_setting_off_resumes_a_paused_break()
    {
        await StartOnBreakAsync();
        await SetSessionAsync(SessionState.Locked);

        await _h.Settings.UpdateAsync(_h.Settings.Current with { PauseBreakWhileLocked = false }, CancellationToken.None);
        await _service.LastEvaluation;

        Assert.False(_h.Engine.GetStatus().IsPaused);
    }

    [Fact]
    public async Task Locking_without_a_break_does_nothing()
    {
        _h.SettingsStore.Settings = AppSettings.Default with { PauseBreakWhileLocked = true };
        await _h.StartAsync();
        _service.Start();

        await SetSessionAsync(SessionState.Locked);

        Assert.Equal(RewardState.Working, _h.Engine.GetStatus().State);
        Assert.False(_h.Engine.GetStatus().IsPaused);
    }

    private sealed class FakeSession : ISessionStateProvider
    {
        public SessionState Current { get; private set; } = SessionState.Active;

        public event EventHandler<SessionState>? Changed;

        public void Set(SessionState state)
        {
            Current = state;
            Changed?.Invoke(this, state);
        }
    }
}
