using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Ipc;
using WorkToUnlock.Core.Rules;
using WorkToUnlock.Enforcement.AppLocker;
using WorkToUnlock.Enforcement.Strict;
using WorkToUnlock.Infrastructure.Ipc;
using WorkToUnlock.Infrastructure.Windows;

namespace WorkToUnlock.Service;

/// <summary>Applies and removes the calling user's AppLocker deny rules. Serialized: one policy change at a time.</summary>
[SupportedOSPlatform("windows")]
public sealed class StrictEnforcementManager(AppLockerPolicyStore store, ILogger<StrictEnforcementManager> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _lastError;

    public StrictStatus GetStatus(string? userSid)
    {
        var support = AppLockerCompatibility.Check();
        var ownership = store.LoadOwnership();
        return new StrictStatus
        {
            Supported = support.Supported,
            SupportMessage = support.Message,
            Consented = StrictModeState.LoadConsent().Enabled,
            PolicyApplied = userSid is not null && ownership.DenyRulesByUser.ContainsKey(userSid),
            OwnedRuleCount = ownership.DenyRulesByUser.Values.Sum(v => v.Count) + (ownership.BaselineAllowRuleId is null ? 0 : 1),
            LastError = _lastError,
        };
    }

    public async Task<OperationResult> ApplyAsync(string userSid, IReadOnlyCollection<StrictRule> rules, CancellationToken cancellationToken)
    {
        if (!StrictModeState.LoadConsent().Enabled)
        {
            return OperationResult.Fail("An administrator has not enabled strict mode on this PC.");
        }

        var support = AppLockerCompatibility.Check();
        if (!support.Supported)
        {
            return OperationResult.Fail(support.Message);
        }

        var folders = WindowsSystemFolders.Create(AppContext.BaseDirectory);
        var unsafeRule = rules.FirstOrDefault(r => !IsSafe(r, folders));
        if (unsafeRule is not null)
        {
            logger.LogWarning("Rejected unsafe strict rule for {Path}", unsafeRule.ExecutablePath);
            return OperationResult.Fail($"'{unsafeRule.Name}' cannot be blocked with AppLocker safely.");
        }

        return await ChangeAsync(userSid, rules, cancellationToken).ConfigureAwait(false);
    }

    public Task<OperationResult> RemoveAsync(string userSid, CancellationToken cancellationToken) => ChangeAsync(userSid, [], cancellationToken);

    /// <summary>Removes every owned rule for every user (uninstall, consent withdrawal, recovery).</summary>
    public async Task<OperationResult> RollbackAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ownership = store.LoadOwnership();
            var current = store.GetLocalPolicy();
            var result = AppLockerPolicyDocument.RemoveAll(current, ownership);
            if (!string.Equals(Normalize(current), Normalize(result.PolicyXml), StringComparison.Ordinal))
            {
                store.SetLocalPolicy(result.PolicyXml, ownership);
            }

            store.SaveOwnership(result.Ownership);
            logger.LogInformation("Removed all Work to Unlock AppLocker rules");
            return OperationResult.Ok("All Work to Unlock AppLocker rules were removed.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or FormatException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            logger.LogError(ex, "AppLocker rollback failed");
            return OperationResult.Fail(ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<OperationResult> ChangeAsync(string userSid, IReadOnlyCollection<StrictRule> rules, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ownership = store.LoadOwnership();
            if (rules.Count == 0 && !ownership.DenyRulesByUser.ContainsKey(userSid))
            {
                return OperationResult.Ok("No strict rules to remove.");
            }

            var result = AppLockerPolicyDocument.SetUserRules(store.GetLocalPolicy(), ownership, userSid, rules);
            store.SetLocalPolicy(result.PolicyXml, ownership);
            store.SaveOwnership(result.Ownership);
            var warnings = result.Warnings.ToList();
            if (rules.Count > 0 && store.EnsureApplicationIdentityRunning() is { } serviceWarning)
            {
                warnings.Add(serviceWarning);
            }

            _lastError = null;
            logger.LogInformation("Strict rules for {User}: {Count} app(s)", userSid, rules.Count);
            return OperationResult.Ok(warnings.Count == 0 ? "Strict rules updated." : string.Join(" ", warnings));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or FormatException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            _lastError = ex.Message;
            logger.LogError(ex, "Updating AppLocker rules failed");
            return OperationResult.Fail($"AppLocker rules could not be updated: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsSafe(StrictRule rule, SystemFolders folders)
    {
        var candidate = new ApplicationRule
        {
            Name = rule.Name,
            ExecutablePath = rule.ExecutablePath,
            RuleType = RuleType.Restricted,
            Identity = new ExecutableIdentity { InstallRoot = rule.InstallRoot, Scope = rule.Scope, Publisher = rule.Publisher },
        };
        return RuleSafetyValidator.Validate(candidate, folders).Count == 0 && RuleSafetyValidator.IsSafeToEnforce(rule.ExecutablePath, folders);
    }

    private static string Normalize(string xml) => System.Xml.Linq.XDocument.Parse(xml).ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
}

/// <summary>Serves the agent. Only the installed agent executable may connect; the user comes from the pipe connection.</summary>
[SupportedOSPlatform("windows")]
public sealed class StrictRequestHandler(StrictEnforcementManager manager) : IIpcRequestHandler
{
    public async Task<object?> HandleAsync(IpcClientContext client, string method, JsonElement? payload, CancellationToken cancellationToken)
    {
        var user = client.UserSid ?? throw new IpcException("The calling user could not be identified.");
        return method switch
        {
            StrictMethods.Status => manager.GetStatus(user),
            StrictMethods.Apply => await manager.ApplyAsync(user, IpcJson.FromElement<StrictApplyRequest>(payload)?.Rules ?? [], cancellationToken).ConfigureAwait(false),
            StrictMethods.Remove => await manager.RemoveAsync(user, cancellationToken).ConfigureAwait(false),
            _ => throw new IpcException($"Unknown method '{method}'."),
        };
    }
}

[SupportedOSPlatform("windows")]
public sealed class StrictEnforcementService(StrictRequestHandler handler, ILoggerFactory loggerFactory, ILogger<StrictEnforcementService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var agentPath = Path.Combine(AppContext.BaseDirectory, "WorkToUnlock.Agent.exe");
        await using var server = new PipeServer(
            new PipeServerOptions
            {
                PipeName = PipeSecurityFactory.StrictServicePipeName,
                SecurityFactory = PipeSecurityFactory.ServiceWithLocalClients,
                ResolveClientUser = true,
                ClientValidator = client => client.UserSid is not null
                    && string.Equals(client.ExecutablePath, agentPath, StringComparison.OrdinalIgnoreCase),
            },
            handler,
            loggerFactory.CreateLogger<PipeServer>());
        server.Start();
        logger.LogInformation("Strict enforcement service started (consent: {Consent})", StrictModeState.LoadConsent().Enabled);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        // Deliberately keep AppLocker rules in place when the service stops: restrictions fail safe. Use
        // "WorkToUnlock.Service.exe rollback" (or uninstall) to remove them.
        logger.LogInformation("Strict enforcement service stopping; AppLocker rules are left in place");
    }
}
