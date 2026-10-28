using System.Text.Json;
using Microsoft.Extensions.Logging;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Ipc;
using WorkToUnlock.Core.Persistence;
using WorkToUnlock.Core.Rewards;
using WorkToUnlock.Core.Rules;
using WorkToUnlock.Core.Time;
using WorkToUnlock.Enforcement.Discovery;
using WorkToUnlock.Enforcement.Strict;
using WorkToUnlock.Infrastructure.Ipc;

namespace WorkToUnlock.Agent.Services;

/// <summary>Serves dashboard requests. Every state change goes through the same validated paths as the agent itself.</summary>
public sealed class AgentRequestHandler(
    AgentStatusService status,
    IRewardEngine engine,
    IRuleCatalog rules,
    SettingsService settings,
    IApplicationDiscovery discovery,
    IStatisticsStore statistics,
    IAuditLog audit,
    IStrictEnforcementClient strictClient,
    DashboardBridge dashboards,
    ProgressService progress,
    ProtectionMonitor protection,
    SystemFolders folders,
    IClock clock,
    ILogger<AgentRequestHandler> logger) : IIpcRequestHandler
{
    private const int HistoryDays = 14;
    private static readonly TimeSpan InstalledAppsCacheLifetime = TimeSpan.FromMinutes(5);
    private (long Mono, IReadOnlyList<InstalledApplication> Apps)? _installedApps;

    public async Task<object?> HandleAsync(IpcClientContext client, string method, JsonElement? payload, CancellationToken cancellationToken)
    {
        switch (method)
        {
            case AgentMethods.Ping:
                return "pong";

            case AgentMethods.GetStatus:
                return status.Build();

            case AgentMethods.ActivateReward:
                var result = await engine.ActivateRewardAsync(cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Reward activation requested by pid {Pid}: {Outcome}", client.ProcessId, result.Outcome);
                return result;

            case AgentMethods.ListRules:
                return rules.Rules;

            case AgentMethods.SaveRule:
                return await SaveRuleAsync(Required<ApplicationRule>(payload), cancellationToken).ConfigureAwait(false);

            case AgentMethods.DeleteRule:
                var deleted = await rules.DeleteAsync(Required<IdRequest>(payload).Id, cancellationToken).ConfigureAwait(false);
                return deleted ? OperationResult.Ok("Application removed.") : OperationResult.Fail("That application no longer exists.");

            case AgentMethods.RevalidateRule:
                return await RevalidateAsync(Required<IdRequest>(payload).Id, cancellationToken).ConfigureAwait(false);

            case AgentMethods.InspectExecutable:
                var request = Required<PathRequest>(payload);
                return await Task.Run(() => discovery.Inspect(request.Path, request.RuleType), cancellationToken).ConfigureAwait(false);

            case AgentMethods.ListInstalledApps:
                return await ListInstalledAsync(cancellationToken).ConfigureAwait(false);

            case AgentMethods.GetSettings:
                return settings.Current;

            case AgentMethods.UpdateSettings:
                return await UpdateSettingsAsync(Required<AppSettings>(payload), cancellationToken).ConfigureAwait(false);

            case AgentMethods.SetEnforcementEnabled:
                var enforcement = Required<SetEnforcementRequest>(payload);
                return await settings.SetEnforcementEnabledAsync(enforcement.Enabled, enforcement.Reason, cancellationToken).ConfigureAwait(false);

            case AgentMethods.ResetProgress:
                var reason = Required<ResetProgressRequest>(payload).Reason;
                await engine.ResetProgressAsync(string.IsNullOrWhiteSpace(reason) ? "requested from the dashboard" : reason.Trim(), cancellationToken).ConfigureAwait(false);
                return OperationResult.Ok("Progress reset.");

            case AgentMethods.GetStatistics:
                return await statistics.GetReportAsync(clock.UtcNow, TimeZoneInfo.Local, HistoryDays, cancellationToken).ConfigureAwait(false);

            case AgentMethods.GetDailyProgress:
                return await progress.GetAsync(cancellationToken).ConfigureAwait(false);

            case AgentMethods.GetTamperReport:
                return await protection.GetReportAsync(cancellationToken).ConfigureAwait(false);

            case AgentMethods.GetRuleTimes:
                return await GetRuleTimesAsync(cancellationToken).ConfigureAwait(false);

            case AgentMethods.ExportData:
                var json = await statistics.ExportJsonAsync(cancellationToken).ConfigureAwait(false);
                await audit.WriteAsync("DataExported", "Local activity data exported from the dashboard.", "Success", cancellationToken).ConfigureAwait(false);
                return new ExportResult(json);

            case AgentMethods.GetAuditEvents:
                var count = IpcJson.FromElement<CountRequest>(payload)?.Count ?? 100;
                return new AuditEventList(await audit.GetRecentAsync(count, cancellationToken).ConfigureAwait(false));

            case AgentMethods.ShowDashboard:
                await dashboards.ShowAsync(IpcJson.FromElement<ShowDashboardRequest>(payload)?.Page).ConfigureAwait(false);
                return OperationResult.Ok();

            case AgentMethods.ExitDashboards:
                await dashboards.ExitDashboardsAsync().ConfigureAwait(false);
                return OperationResult.Ok();

            default:
                throw new IpcException($"Unknown method '{method}'.");
        }
    }

    private async Task<SaveRuleResult> SaveRuleAsync(ApplicationRule requested, CancellationToken cancellationToken)
    {
        var rule = requested with { Name = requested.Name?.Trim() ?? string.Empty, ExecutablePath = WindowsPath.Normalize(requested.ExecutablePath) };
        if (rule.Id != 0 && rules.Rules.All(r => r.Id != rule.Id))
        {
            return new SaveRuleResult { Errors = [new ValidationError(nameof(rule.Id), "That application no longer exists.")] };
        }

        // New rules without identity information are inspected so they never fall back to process names.
        if (string.IsNullOrEmpty(rule.Identity.FileName))
        {
            var inspection = await Task.Run(() => discovery.Inspect(rule.ExecutablePath, rule.RuleType), cancellationToken).ConfigureAwait(false);
            if (!inspection.Exists)
            {
                return new SaveRuleResult { Errors = inspection.Problems };
            }

            rule = rule with { ExecutablePath = inspection.ExecutablePath, Identity = inspection.Identity };
        }

        var errors = RuleSafetyValidator.Validate(rule, folders).ToList();
        var duplicate = rules.Rules.FirstOrDefault(r => r.Id != rule.Id && WindowsPath.PathEquals(r.ExecutablePath, rule.ExecutablePath));
        if (duplicate is not null)
        {
            errors.Add(new ValidationError(nameof(rule.ExecutablePath), duplicate.RuleType == rule.RuleType
                ? $"This application is already in the list as '{duplicate.Name}'."
                : $"'{duplicate.Name}' is already a {(duplicate.RuleType == RuleType.Work ? "work" : "restricted")} application. An app cannot be both."));
        }

        if (errors.Count > 0)
        {
            return new SaveRuleResult { Errors = errors };
        }

        var saved = await rules.SaveAsync(rule, cancellationToken).ConfigureAwait(false);
        return new SaveRuleResult { Succeeded = true, Rule = saved };
    }

    private async Task<SaveRuleResult> RevalidateAsync(long id, CancellationToken cancellationToken)
    {
        var rule = rules.Rules.FirstOrDefault(r => r.Id == id);
        if (rule is null)
        {
            return new SaveRuleResult { Errors = [new ValidationError(nameof(ApplicationRule.Id), "That application no longer exists.")] };
        }

        var refreshed = await Task.Run(() => discovery.Revalidate(rule), cancellationToken).ConfigureAwait(false);
        if (refreshed is null)
        {
            await audit.WriteAsync("RuleRevalidated", $"'{rule.Name}': executable not found at {rule.ExecutablePath}", "Failed", cancellationToken).ConfigureAwait(false);
            return new SaveRuleResult
            {
                Rule = rule,
                Errors = [new ValidationError(nameof(ApplicationRule.ExecutablePath), "The executable could not be found. Choose its new location with Change.")],
            };
        }

        var errors = RuleSafetyValidator.Validate(refreshed, folders);
        if (errors.Count > 0)
        {
            return new SaveRuleResult { Rule = rule, Errors = errors };
        }

        var saved = await rules.SaveAsync(refreshed, cancellationToken).ConfigureAwait(false);
        return new SaveRuleResult { Succeeded = true, Rule = saved };
    }

    private async Task<OperationResult> UpdateSettingsAsync(AppSettings requested, CancellationToken cancellationToken)
    {
        if (requested.EnforcementMode == EnforcementMode.Strict && settings.Current.EnforcementMode != EnforcementMode.Strict)
        {
            var strict = await strictClient.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (strict is null)
            {
                return OperationResult.Fail("Strict mode needs the optional Work to Unlock service, which is not installed or not running.",
                    [new ValidationError(nameof(AppSettings.EnforcementMode), "Install the strict enforcement service first (administrator).")]);
            }

            if (!strict.Supported || !strict.Consented)
            {
                return OperationResult.Fail(strict.Supported ? "An administrator must enable strict mode first." : strict.SupportMessage,
                    [new ValidationError(nameof(AppSettings.EnforcementMode), strict.Supported ? "Run 'Enable strict mode' as administrator." : strict.SupportMessage)]);
            }
        }

        return await settings.UpdateAsync(requested, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<InstalledApplication>> ListInstalledAsync(CancellationToken cancellationToken)
    {
        var mono = clock.MonotonicMilliseconds;
        if (_installedApps is { } cached && mono - cached.Mono < (long)InstalledAppsCacheLifetime.TotalMilliseconds)
        {
            return cached.Apps;
        }

        var apps = await Task.Run(discovery.ListInstalledApplications, cancellationToken).ConfigureAwait(false);
        _installedApps = (mono, apps);
        return apps;
    }

    private async Task<IReadOnlyList<RuleTime>> GetRuleTimesAsync(CancellationToken cancellationToken)
    {
        var report = await statistics.GetReportAsync(clock.UtcNow, TimeZoneInfo.Local, 1, cancellationToken).ConfigureAwait(false);
        var today = report.WorkByApplicationToday.ToDictionary(a => a.RuleId, a => a.Time);
        var week = report.WorkByApplicationThisWeek.ToDictionary(a => a.RuleId, a => a.Time);
        return rules.Rules
            .Select(r => new RuleTime(r.Id, today.GetValueOrDefault(r.Id), week.GetValueOrDefault(r.Id)))
            .ToList();
    }

    private static T Required<T>(JsonElement? payload) =>
        IpcJson.FromElement<T>(payload) ?? throw new IpcException($"Missing {typeof(T).Name} payload.");
}
