using Microsoft.Extensions.Logging;
using WorkToUnlock.Core.Activity;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Rewards;

namespace WorkToUnlock.Agent.Services;

/// <summary>
/// When <see cref="AppSettings.PauseBreakWhileLocked"/> is on, a running break stops counting down while the PC is
/// locked, asleep or disconnected, and continues with the same time left once the user is back.
/// </summary>
public sealed class BreakPauseService(
    ISessionStateProvider session,
    IRewardEngine engine,
    SettingsService settings,
    ILogger<BreakPauseService> logger)
{
    private readonly Lock _queueLock = new();

    /// <summary>The most recent evaluation (tests await it).</summary>
    internal Task LastEvaluation { get; private set; } = Task.CompletedTask;

    public void Start()
    {
        session.Changed += (_, _) => Queue();
        settings.Changed += (_, _) => Queue();

        // A break paused before a restart resumes if the user is back (or the setting was turned off).
        Queue();
    }

    private async Task EvaluateAsync()
    {
        var current = session.Current;
        var away = current != SessionState.Active;
        var enabled = settings.Current.PauseBreakWhileLocked;
        var status = engine.GetStatus();

        if (enabled && away && status.State == RewardState.Unlocked && !status.IsPaused)
        {
            await engine.PauseAsync(current switch
            {
                SessionState.Suspended => "PC asleep",
                SessionState.Disconnected => "session disconnected",
                _ => "PC locked",
            }, CancellationToken.None).ConfigureAwait(false);
        }
        else if (status.IsPaused && (!away || !enabled))
        {
            await engine.ResumeAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void Queue()
    {
        // Session notifications arrive on the window thread; never block it on the database.
        lock (_queueLock)
        {
            var previous = LastEvaluation;
            LastEvaluation = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                try
                {
                    await EvaluateAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Pausing or resuming the break failed");
                }
            });
        }
    }
}
