using Microsoft.Extensions.Logging;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Enforcement;
using WorkToUnlock.Core.Rewards;
using WorkToUnlock.Core.Rules;
using WorkToUnlock.Enforcement.Strict;

namespace WorkToUnlock.Agent.Services;

/// <summary>
/// Decides whether restrictions should be active (enforcement enabled, no reward running, restricted apps configured)
/// and drives the process enforcer and, in strict mode, the elevated AppLocker service. Reconciliations are serialized;
/// the reward engine only notifies after its new state is durable, so restrictions never follow an unsaved state.
/// </summary>
public sealed class EnforcementCoordinator(
    IAppEnforcer processEnforcer,
    IStrictEnforcementClient strictClient,
    IRewardEngine engine,
    IRuleCatalog rules,
    SettingsService settings,
    ILogger<EnforcementCoordinator> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile string? _strictProblem;
    private volatile bool _strictApplied;
    private bool _started;

    /// <summary>Combined health, computed on every read from the enforcer's live health (never a cached copy).</summary>
    public EnforcementHealth Health => Compose(processEnforcer.Health);

    public event EventHandler<EnforcementHealth>? HealthChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        engine.StateChanged += (_, _) => Trigger("reward state changed");
        rules.Changed += (_, _) => Trigger("rules changed");
        settings.Changed += (_, _) => Trigger("settings changed");
        processEnforcer.HealthChanged += (_, _) => PublishHealth();
        await ReconcileAsync("agent started", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Brings enforcement in line with the current state.</summary>
    public async Task ReconcileAsync(string reason, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = settings.Current;
            var status = engine.GetStatus();
            var restricted = rules.Rules.Where(r => r.RuleType == RuleType.Restricted && r.Enabled).ToList();
            var options = EnforcementOptions.From(current);

            if (!current.EnforcementEnabled)
            {
                await RemoveAsync("Enforcement is disabled. Restricted apps can run.", current.EnforcementMode, cancellationToken).ConfigureAwait(false);
            }
            else if (!status.RestrictionsActive)
            {
                var until = status.RewardExpiresUtc?.ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture);
                await RemoveAsync(until is null ? "Reward active. Restricted apps are available." : $"Reward active until {until}. Restricted apps are available.", current.EnforcementMode, cancellationToken).ConfigureAwait(false);
            }
            else if (restricted.Count == 0)
            {
                await RemoveAsync("No restricted apps are configured.", current.EnforcementMode, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await processEnforcer.ApplyRestrictionsAsync(restricted, options, cancellationToken).ConfigureAwait(false);
                await ApplyStrictAsync(current.EnforcementMode, restricted, cancellationToken).ConfigureAwait(false);
            }

            logger.LogDebug("Enforcement reconciled ({Reason})", reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Enforcement reconciliation failed ({Reason})", reason);
            _strictProblem = $"Enforcement could not be updated: {ex.Message}";
        }
        finally
        {
            _gate.Release();
        }

        PublishHealth();
    }

    public void Dispose() => _gate.Dispose();

    private async Task RemoveAsync(string message, EnforcementMode mode, CancellationToken cancellationToken)
    {
        await processEnforcer.RemoveRestrictionsAsync(message, cancellationToken).ConfigureAwait(false);
        if (_strictApplied || mode == EnforcementMode.Strict)
        {
            var result = await strictClient.RemoveAsync(cancellationToken).ConfigureAwait(false);
            _strictApplied = !result.Succeeded && _strictApplied;
            _strictProblem = result.Succeeded || mode != EnforcementMode.Strict ? null : $"Strict mode: {result.Message}";
        }
        else
        {
            _strictProblem = null;
        }
    }

    private async Task ApplyStrictAsync(EnforcementMode mode, IReadOnlyCollection<ApplicationRule> restricted, CancellationToken cancellationToken)
    {
        if (mode != EnforcementMode.Strict)
        {
            if (_strictApplied)
            {
                var removed = await strictClient.RemoveAsync(cancellationToken).ConfigureAwait(false);
                _strictApplied = !removed.Succeeded;
            }

            _strictProblem = null;
            return;
        }

        var result = await strictClient.ApplyAsync(restricted, cancellationToken).ConfigureAwait(false);
        _strictApplied = result.Succeeded;
        _strictProblem = result.Succeeded
            ? null
            : $"Strict mode is not active ({result.Message}). Falling back to process enforcement only.";
        if (!result.Succeeded)
        {
            logger.LogWarning("Strict enforcement unavailable: {Message}", result.Message);
        }
    }

    private void Trigger(string reason) => _ = Task.Run(() => ReconcileAsync(reason, CancellationToken.None));

    private void PublishHealth() => HealthChanged?.Invoke(this, Health);

    private EnforcementHealth Compose(EnforcementHealth process)
    {
        var problems = process.Problems.ToList();
        if (_strictProblem is not null)
        {
            problems.Add(_strictProblem);
        }

        var state = process.State;
        if (_strictProblem is not null && state == EnforcementHealthState.Healthy)
        {
            state = EnforcementHealthState.Degraded;
        }

        return process with
        {
            State = state,
            Mode = settings.Current.EnforcementMode,
            LaunchPreventionActive = _strictApplied && process.RestrictionsApplied,
            Problems = problems,
        };
    }
}
