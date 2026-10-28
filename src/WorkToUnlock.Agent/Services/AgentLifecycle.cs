using Microsoft.Extensions.Logging;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Persistence;
using WorkToUnlock.Core.Rules;
using WorkToUnlock.Core.Time;
using WorkToUnlock.Enforcement.Discovery;
using WorkToUnlock.Infrastructure.Persistence;

namespace WorkToUnlock.Agent.Services;

/// <summary>First-run setup: adds VS Code and Blockbench as work apps and Steam and Discord as restricted apps when installed.</summary>
public sealed class DefaultRulesSeeder(IApplicationDiscovery discovery, IRuleCatalog rules, ISettingsStore settings, SystemFolders folders, ILogger<DefaultRulesSeeder> logger)
{
    internal const string SeededKey = "Internal.DefaultsSeeded";

    public async Task SeedIfFirstRunAsync(CancellationToken cancellationToken)
    {
        if (await settings.GetValueAsync(SeededKey, cancellationToken).ConfigureAwait(false) is not null)
        {
            return;
        }

        // Isolated test instances must never start restricting the real apps on the machine.
        if (Infrastructure.AppPaths.InstanceName is not null)
        {
            await settings.SetValueAsync(SeededKey, "skipped (isolated instance)", cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var app in discovery.DetectDefaults())
        {
            if (rules.Rules.Any(r => WindowsPath.PathEquals(r.ExecutablePath, app.ExecutablePath)))
            {
                continue;
            }

            var inspection = discovery.Inspect(app.ExecutablePath, app.RuleType);
            var rule = new ApplicationRule
            {
                Name = app.Name,
                ExecutablePath = inspection.ExecutablePath,
                RuleType = app.RuleType,
                Identity = inspection.Identity,
            };

            if (!inspection.Exists || RuleSafetyValidator.Validate(rule, folders).Count > 0)
            {
                continue;
            }

            await rules.SaveAsync(rule, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Added default {Type} app {Name} ({Path})", app.RuleType, app.Name, app.ExecutablePath);
        }

        await settings.SetValueAsync(SeededKey, "true", cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Daily database backup and data-retention pruning.</summary>
public sealed class MaintenanceService(SqliteDatabase database, IStatisticsStore statistics, ISettingsStore store, SettingsService settings, IClock clock, ILogger<MaintenanceService> logger)
{
    private const string LastBackupKey = "Internal.LastBackupUtc";
    private const string LastPruneKey = "Internal.LastPruneUtc";
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public async Task RunIfDueAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = clock.UtcNow;
            if (await IsDueAsync(LastPruneKey, now, cancellationToken).ConfigureAwait(false))
            {
                var cutoff = now.AddDays(-settings.Current.DataRetentionDays);
                var removed = await statistics.PruneAsync(cutoff, cancellationToken).ConfigureAwait(false);
                await store.SetValueAsync(LastPruneKey, now.ToString("O"), cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Pruned {Count} record(s) older than {Cutoff:d}", removed, cutoff);
            }

            if (await IsDueAsync(LastBackupKey, now, cancellationToken).ConfigureAwait(false))
            {
                await database.BackupAsync(cancellationToken).ConfigureAwait(false);
                await store.SetValueAsync(LastBackupKey, now.ToString("O"), cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Database backed up to {Path}", database.BackupPath);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Maintenance failed; will retry later");
        }
    }

    private async Task<bool> IsDueAsync(string key, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var value = await store.GetValueAsync(key, cancellationToken).ConfigureAwait(false);
        return value is null
            || !DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var last)
            || now - last >= Interval
            || last > now;
    }
}
