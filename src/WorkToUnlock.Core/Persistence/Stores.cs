using WorkToUnlock.Core.Domain;

namespace WorkToUnlock.Core.Persistence;

/// <summary>Thrown when a reward-state save loses an optimistic-concurrency race.</summary>
public sealed class ConcurrencyConflictException(string message) : Exception(message);

/// <summary>Thrown when persistent storage is unavailable or corrupt.</summary>
public sealed class PersistenceException(string message, Exception? inner = null) : Exception(message, inner);

public interface IRewardStateStore
{
    Task<RewardStateRecord?> LoadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Saves <paramref name="record"/> if the stored version still equals <paramref name="expectedVersion"/>.
    /// The transition (if any) is written to the audit log and reward history in the same transaction.
    /// Returns the saved record with its new version.
    /// </summary>
    /// <exception cref="ConcurrencyConflictException">The stored version changed.</exception>
    Task<RewardStateRecord> SaveAsync(
        RewardStateRecord record,
        long expectedVersion,
        RewardTransition? transition,
        CancellationToken cancellationToken);
}

public interface IApplicationRuleStore
{
    Task<IReadOnlyList<ApplicationRule>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Inserts when <see cref="ApplicationRule.Id"/> is 0, otherwise updates. Returns the stored rule.</summary>
    Task<ApplicationRule> SaveAsync(ApplicationRule rule, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken);
}

public sealed record WorkSessionRecord
{
    public long Id { get; init; }

    public long ApplicationRuleId { get; init; }

    public DateTimeOffset StartUtc { get; init; }

    public DateTimeOffset EndUtc { get; init; }

    public long QualifyingMilliseconds { get; init; }
}

public interface IWorkSessionStore
{
    Task<long> InsertAsync(WorkSessionRecord session, CancellationToken cancellationToken);

    Task UpdateAsync(WorkSessionRecord session, CancellationToken cancellationToken);
}

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);

    Task<string?> GetValueAsync(string key, CancellationToken cancellationToken);

    Task SetValueAsync(string key, string value, CancellationToken cancellationToken);
}

public sealed record AuditEvent(long Id, DateTimeOffset TimestampUtc, string EventType, string Description, string Result);

public interface IAuditLog
{
    Task WriteAsync(string eventType, string description, string result, CancellationToken cancellationToken);

    Task<IReadOnlyList<AuditEvent>> GetRecentAsync(int count, CancellationToken cancellationToken);
}

public sealed record ApplicationTime(long RuleId, string Name, TimeSpan Time);

public sealed record DailyActivity(DateOnly Date, TimeSpan QualifyingWork);

public sealed record StatisticsReport
{
    public TimeSpan WorkToday { get; init; }

    public TimeSpan WorkThisWeek { get; init; }

    public int RewardsEarned { get; init; }

    public int RewardsActivated { get; init; }

    public TimeSpan RewardTimeActivated { get; init; }

    public IReadOnlyList<ApplicationTime> WorkByApplicationToday { get; init; } = [];

    public IReadOnlyList<ApplicationTime> WorkByApplicationThisWeek { get; init; } = [];

    public IReadOnlyList<DailyActivity> DailyHistory { get; init; } = [];
}

public interface IStatisticsStore
{
    Task<StatisticsReport> GetReportAsync(DateTimeOffset now, TimeZoneInfo timeZone, int historyDays, CancellationToken cancellationToken);

    /// <summary>Deletes activity data older than the cutoff. Returns the number of rows removed.</summary>
    Task<int> PruneAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken);

    /// <summary>Exports all local data as an indented JSON document.</summary>
    Task<string> ExportJsonAsync(CancellationToken cancellationToken);
}
