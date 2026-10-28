using System.Text.Json;
using Microsoft.Extensions.Logging;
using WorkToUnlock.Core.Persistence;
using WorkToUnlock.Core.Time;
using WorkToUnlock.Core.Updates;
using WorkToUnlock.Infrastructure.Updates;

namespace WorkToUnlock.Agent.Services;

/// <summary>
/// Checks the project's GitHub releases once a day (when <c>CheckForUpdates</c> is on) and notifies once per new
/// version. Installing is left to the dashboard, which downloads, verifies and runs the installer on request.
/// </summary>
public sealed class UpdateService(
    IReleaseClient releases,
    SettingsService settings,
    ISettingsStore values,
    NotificationService notifications,
    AgentInfo info,
    IClock clock,
    ILogger<UpdateService> logger)
{
    internal const string NotifiedKey = "Internal.UpdateNotifiedVersion";
    internal static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _nextCheck = clock.MonotonicMilliseconds + (long)FirstCheckDelay.TotalMilliseconds;
    private UpdateInfo? _last;

    /// <summary>Called on the agent's cadence; checks when a check is due and allowed.</summary>
    public async Task CheckIfDueAsync(CancellationToken cancellationToken)
    {
        if (settings.Current.CheckForUpdates && clock.MonotonicMilliseconds >= Volatile.Read(ref _nextCheck))
        {
            await CheckAsync(notify: true, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The last result, or a fresh check when asked (also when automatic checks are off).</summary>
    public async Task<UpdateInfo> GetAsync(bool checkNow, CancellationToken cancellationToken) =>
        checkNow ? await CheckAsync(notify: false, cancellationToken).ConfigureAwait(false) : _last ?? new UpdateInfo { CurrentVersion = info.Version };

    private async Task<UpdateInfo> CheckAsync(bool notify, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = clock.UtcNow;
            UpdateInfo result;
            try
            {
                var json = await releases.GetLatestReleaseJsonAsync(cancellationToken).ConfigureAwait(false);
                result = json is null ? new UpdateInfo { CurrentVersion = info.Version, CheckedUtc = now } : ReleaseParser.Parse(json, info.Version, now);
                Volatile.Write(ref _nextCheck, clock.MonotonicMilliseconds + (long)CheckInterval.TotalMilliseconds);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException
                                       || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                logger.LogInformation("Update check failed: {Message}", ex.Message);
                Volatile.Write(ref _nextCheck, clock.MonotonicMilliseconds + (long)RetryInterval.TotalMilliseconds);
                result = (_last ?? new UpdateInfo { CurrentVersion = info.Version }) with
                {
                    CheckedUtc = now,
                    Error = "Couldn't reach GitHub to check for updates.",
                };
            }

            _last = result;
            if (result.IsUpdateAvailable)
            {
                logger.LogInformation("Update available: {Latest} (running {Current})", result.LatestVersion, result.CurrentVersion);
                if (notify && await values.GetValueAsync(NotifiedKey, cancellationToken).ConfigureAwait(false) != result.LatestVersion)
                {
                    notifications.NotifyUpdateAvailable(result);
                    await values.SetValueAsync(NotifiedKey, result.LatestVersion!, cancellationToken).ConfigureAwait(false);
                }
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }
}
