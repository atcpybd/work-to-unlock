using Microsoft.Extensions.Logging;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Persistence;
using WorkToUnlock.Core.Time;

namespace WorkToUnlock.Agent.Services;

/// <summary>
/// Unlocks achievements whenever daily progress is recalculated and announces the new ones. Unlocked achievements
/// are kept forever, even after the history that earned them is removed by data retention.
/// </summary>
public sealed class AchievementService(
    IAchievementStore store,
    IStatisticsStore statistics,
    ProgressService progress,
    ProtectionMonitor protection,
    NotificationService notifications,
    IClock clock,
    ILogger<AchievementService> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _queueLock = new();
    private HashSet<string>? _unlocked;

    /// <summary>The most recent evaluation (tests await it).</summary>
    internal Task LastEvaluation { get; private set; } = Task.CompletedTask;

    public void Start() => progress.Updated += (_, current) => Queue(current);

    public async Task<IReadOnlyList<AchievementInfo>> GetAllAsync(CancellationToken cancellationToken) =>
        AchievementCatalog.Describe(await store.GetUnlockedAsync(cancellationToken).ConfigureAwait(false));

    internal async Task EvaluateAsync(DailyProgress current, CancellationToken cancellationToken)
    {
        List<AchievementDefinition> announced = [];
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _unlocked ??= (await store.GetUnlockedAsync(cancellationToken).ConfigureAwait(false)).Keys.ToHashSet(StringComparer.Ordinal);
            if (_unlocked.Count == AchievementCatalog.All.Count)
            {
                return;
            }

            var now = clock.UtcNow;
            var report = await statistics.GetReportAsync(now, TimeZoneInfo.Local, 1, cancellationToken).ConfigureAwait(false);
            var honesty = await protection.GetReportAsync(cancellationToken).ConfigureAwait(false);
            var facts = new AchievementFacts
            {
                TotalWork = current.TotalWork,
                BestDay = current.BestDay,
                BestStreak = current.BestStreak,
                RewardsEarned = report.RewardsEarned,
                DaysTracked = current.DaysTracked,
                CleanLastWeek = honesty.IsClean,
            };

            foreach (var achievement in AchievementCatalog.NewlyEarned(facts, _unlocked))
            {
                if (await store.UnlockAsync(achievement.Id, now, cancellationToken).ConfigureAwait(false))
                {
                    announced.Add(achievement);
                }

                _unlocked.Add(achievement.Id);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (announced.Count > 0)
        {
            logger.LogInformation("Achievements unlocked: {Achievements}", string.Join(", ", announced.Select(a => a.Id)));
            notifications.NotifyAchievementsUnlocked(announced);
        }
    }

    private void Queue(DailyProgress current)
    {
        // Progress is also recalculated for dashboard requests; never make those wait on this.
        lock (_queueLock)
        {
            var previous = LastEvaluation;
            LastEvaluation = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                try
                {
                    await EvaluateAsync(current, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Checking achievements failed");
                }
            });
        }
    }
}
