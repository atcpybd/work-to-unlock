using Microsoft.Extensions.Logging;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Persistence;
using WorkToUnlock.Core.Time;

namespace WorkToUnlock.Agent.Services;

/// <summary>
/// Today's progress towards the daily goal and the goal streak. Computed from stored work sessions (so it lags live
/// tracking by up to the persist interval) and cached briefly. Raises a notification when the goal is crossed.
/// </summary>
public sealed class ProgressService(
    IStatisticsStore statistics,
    SettingsService settings,
    NotificationService notifications,
    IClock clock,
    ILogger<ProgressService> logger)
{
    /// <summary>History used for streaks; limited in practice by the data-retention setting.</summary>
    internal const int HistoryDays = 400;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private (long Mono, DailyProgress Progress)? _cache;
    private (DateOnly Day, bool Met)? _lastSeen;

    internal TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;

    /// <summary>Raised after every fresh calculation (achievements are evaluated from it).</summary>
    public event EventHandler<DailyProgress>? Updated;

    public async Task<DailyProgress> GetAsync(CancellationToken cancellationToken, bool refresh = false)
    {
        DailyProgress progress;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var mono = clock.MonotonicMilliseconds;
            if (!refresh && _cache is { } cached && mono - cached.Mono < (long)CacheLifetime.TotalMilliseconds)
            {
                return cached.Progress;
            }

            var now = clock.UtcNow;
            var days = await statistics.GetDailyTotalsAsync(now, TimeZone, HistoryDays, cancellationToken).ConfigureAwait(false);
            progress = DailyProgressCalculator.Compute(days, settings.Current.DailyGoal);
            _cache = (mono, progress);

            // Notify only when the goal is crossed while running, not when the agent starts on a day already done.
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZone).DateTime);
            if (_lastSeen is { } seen && seen.Day == today && !seen.Met && progress.GoalMet)
            {
                logger.LogInformation("Daily goal reached ({Streak}-day streak)", progress.CurrentStreak);
                notifications.NotifyDailyGoalReached(progress);
            }

            _lastSeen = (today, progress.GoalMet);
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            Updated?.Invoke(this, progress);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "A progress listener failed");
        }

        return progress;
    }
}
