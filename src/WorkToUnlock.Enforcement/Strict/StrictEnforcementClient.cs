using Microsoft.Extensions.Logging;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Ipc;
using WorkToUnlock.Infrastructure.Ipc;

namespace WorkToUnlock.Enforcement.Strict;

/// <summary>Requests understood by the elevated strict-enforcement service.</summary>
public static class StrictMethods
{
    public const string Status = "strictStatus";
    public const string Apply = "strictApply";
    public const string Remove = "strictRemove";
}

/// <summary>A restricted app as sent to the service. The service derives the user from the pipe connection.</summary>
public sealed record StrictRule(string Name, string ExecutablePath, string? Publisher, string? InstallRoot, MatchScope Scope);

public sealed record StrictApplyRequest(IReadOnlyList<StrictRule> Rules);

public sealed record StrictStatus
{
    public bool Supported { get; init; }

    public string SupportMessage { get; init; } = string.Empty;

    /// <summary>An administrator explicitly enabled strict mode on this machine.</summary>
    public bool Consented { get; init; }

    /// <summary>Work to Unlock-owned AppLocker deny rules are currently in the local policy for the caller.</summary>
    public bool PolicyApplied { get; init; }

    public int OwnedRuleCount { get; init; }

    public string? LastError { get; init; }
}

public interface IStrictEnforcementClient
{
    /// <summary>Returns null when the service is not installed or not running.</summary>
    Task<StrictStatus?> GetStatusAsync(CancellationToken cancellationToken);

    Task<OperationResult> ApplyAsync(IReadOnlyCollection<ApplicationRule> rules, CancellationToken cancellationToken);

    Task<OperationResult> RemoveAsync(CancellationToken cancellationToken);
}

/// <summary>Talks to the optional elevated service over its local named pipe.</summary>
public sealed class StrictEnforcementClient(ILogger<StrictEnforcementClient> logger) : IStrictEnforcementClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PolicyTimeout = TimeSpan.FromSeconds(90);

    public async Task<StrictStatus?> GetStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var client = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            return await client.RequestAsync<StrictStatus>(StrictMethods.Status, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or IpcException)
        {
            logger.LogDebug(ex, "Strict enforcement service is not reachable");
            return null;
        }
    }

    public Task<OperationResult> ApplyAsync(IReadOnlyCollection<ApplicationRule> rules, CancellationToken cancellationToken)
    {
        var request = new StrictApplyRequest(rules
            .Where(r => r.RuleType == RuleType.Restricted && r.Enabled)
            .Select(r => new StrictRule(r.Name, r.ExecutablePath, r.Identity.Publisher, r.Identity.InstallRoot, r.Identity.Scope))
            .ToList());
        return SendAsync(StrictMethods.Apply, request, cancellationToken);
    }

    public Task<OperationResult> RemoveAsync(CancellationToken cancellationToken) => SendAsync(StrictMethods.Remove, null, cancellationToken);

    private async Task<OperationResult> SendAsync(string method, object? payload, CancellationToken cancellationToken)
    {
        try
        {
            await using var client = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            return await client.RequestAsync<OperationResult>(method, payload, cancellationToken, PolicyTimeout).ConfigureAwait(false)
                ?? OperationResult.Fail("The strict enforcement service returned no result.");
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return OperationResult.Fail("The strict enforcement service is not running.");
        }
        catch (IpcException ex)
        {
            return OperationResult.Fail(ex.Message);
        }
    }

    private static Task<IpcClient> ConnectAsync(CancellationToken cancellationToken) =>
        IpcClient.ConnectAsync(PipeSecurityFactory.StrictServicePipeName, ConnectTimeout, requireSameUserServer: false, cancellationToken);
}
