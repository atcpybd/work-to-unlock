using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Ipc;
using WorkToUnlock.Core.Persistence;

namespace WorkToUnlock.Agent.Services;

/// <summary>Holds the current settings, validates and audits every change.</summary>
public sealed class SettingsService(ISettingsStore store, IAuditLog audit, ILogger<SettingsService> logger)
{
    private static readonly JsonSerializerOptions DiffJson = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings _current = AppSettings.Default;

    public AppSettings Current => Volatile.Read(ref _current);

    public event EventHandler<AppSettings>? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _current, await store.LoadAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Applies a settings update from the dashboard. <see cref="AppSettings.EnforcementEnabled"/> is ignored here; it can
    /// only change through <see cref="SetEnforcementEnabledAsync"/>.
    /// </summary>
    public async Task<OperationResult> UpdateAsync(AppSettings requested, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requested);
        var errors = SettingsValidator.Validate(requested);
        if (errors.Count > 0)
        {
            return OperationResult.Fail("Some settings are not valid.", errors);
        }

        AppSettings updated;
        string changes;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = Current;
            updated = requested with { EnforcementEnabled = previous.EnforcementEnabled };
            changes = Describe(previous, updated);
            if (changes.Length == 0)
            {
                return OperationResult.Ok("No changes.");
            }

            await store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _current, updated);
            await audit.WriteAsync("SettingsChanged", changes, "Success", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        logger.LogInformation("Settings changed: {Changes}", changes);
        RaiseChanged(updated);
        return OperationResult.Ok("Settings saved.");
    }

    /// <summary>Turns enforcement on or off. Always audited with the user's stated reason.</summary>
    public async Task<OperationResult> SetEnforcementEnabledAsync(bool enabled, string reason, CancellationToken cancellationToken)
    {
        reason = reason?.Trim() ?? string.Empty;
        if (!enabled && reason.Length == 0)
        {
            return OperationResult.Fail("A reason is required to disable enforcement.");
        }

        AppSettings updated;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Current.EnforcementEnabled == enabled)
            {
                return OperationResult.Ok(enabled ? "Enforcement is already enabled." : "Enforcement is already disabled.");
            }

            updated = Current with { EnforcementEnabled = enabled };
            await store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _current, updated);
            await audit.WriteAsync(
                enabled ? "EnforcementEnabled" : "EnforcementDisabled",
                enabled ? "Enforcement re-enabled by the user." : $"Enforcement disabled by the user. Reason: {reason}",
                "Success",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        logger.LogWarning("Enforcement {State} by the user", enabled ? "enabled" : "disabled");
        RaiseChanged(updated);
        return OperationResult.Ok(enabled ? "Enforcement enabled." : "Enforcement disabled.");
    }

    private void RaiseChanged(AppSettings settings)
    {
        try
        {
            Changed?.Invoke(this, settings);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "A settings listener failed");
        }
    }

    private static string Describe(AppSettings before, AppSettings after)
    {
        var a = JsonSerializer.SerializeToNode(before, DiffJson)!.AsObject();
        var b = JsonSerializer.SerializeToNode(after, DiffJson)!.AsObject();
        return string.Join(", ", b
            .Where(p => a[p.Key]?.ToJsonString() != p.Value?.ToJsonString())
            .Select(p => $"{p.Key}: {a[p.Key]?.ToJsonString()} → {p.Value?.ToJsonString()}"));
    }
}
