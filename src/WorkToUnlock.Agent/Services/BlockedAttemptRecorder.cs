using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Enforcement;
using WorkToUnlock.Core.Persistence;
using WorkToUnlock.Core.Time;

namespace WorkToUnlock.Agent.Services;

/// <summary>
/// Counts attempts to start a restricted app while it is locked. One launch usually starts several processes
/// (Steam plus its web helpers, Discord's updater), so detections of the same app within
/// <see cref="SameLaunchWindow"/> of a recorded attempt belong to that attempt.
/// </summary>
public sealed class BlockedAttemptRecorder(IBlockedAttemptStore store, IClock clock, ILogger<BlockedAttemptRecorder> logger) : IEnforcementNotifier
{
    internal static readonly TimeSpan SameLaunchWindow = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<long, long> _lastRecorded = new();
    private readonly Lock _writeLock = new();

    /// <summary>The most recent database write (tests await it).</summary>
    internal Task LastWrite { get; private set; } = Task.CompletedTask;

    public void OnLaunchBlocked(ApplicationRule rule)
    {
        var now = clock.MonotonicMilliseconds;
        if (_lastRecorded.TryGetValue(rule.Id, out var last) && now - last < (long)SameLaunchWindow.TotalMilliseconds)
        {
            return;
        }

        _lastRecorded[rule.Id] = now;
        var when = clock.UtcNow;

        // Called from the enforcement scan, so never block it on the database. Writes are chained to keep their order.
        lock (_writeLock)
        {
            var previous = LastWrite;
            LastWrite = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                try
                {
                    await store.RecordAsync(rule.Id, when, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not record a blocked attempt for {Name}", rule.Name);
                }
            });
        }
    }

    public void OnClosingRestrictedApp(ApplicationRule rule, TimeSpan gracePeriod)
    {
    }
}

/// <summary>Forwards enforcement events to several listeners (notifications, attempt counting).</summary>
public sealed class CompositeEnforcementNotifier(IEnumerable<IEnforcementNotifier> notifiers) : IEnforcementNotifier
{
    private readonly IEnforcementNotifier[] _notifiers = notifiers.ToArray();

    public void OnLaunchBlocked(ApplicationRule rule)
    {
        foreach (var notifier in _notifiers)
        {
            notifier.OnLaunchBlocked(rule);
        }
    }

    public void OnClosingRestrictedApp(ApplicationRule rule, TimeSpan gracePeriod)
    {
        foreach (var notifier in _notifiers)
        {
            notifier.OnClosingRestrictedApp(rule, gracePeriod);
        }
    }
}
