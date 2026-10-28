using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Persistence;
using WorkToUnlock.Core.Time;

namespace WorkToUnlock.Core.Rules;

/// <summary>In-memory view of the configured rules, backed by the rule store.</summary>
public interface IRuleCatalog
{
    IReadOnlyList<ApplicationRule> Rules { get; }

    event EventHandler? Changed;

    Task LoadAsync(CancellationToken cancellationToken);

    Task<ApplicationRule> SaveAsync(ApplicationRule rule, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken);
}

public sealed class RuleCatalog(IApplicationRuleStore store, IClock clock) : IRuleCatalog
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile IReadOnlyList<ApplicationRule> _rules = [];

    public IReadOnlyList<ApplicationRule> Rules => _rules;

    public event EventHandler? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _rules = await store.GetAllAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<ApplicationRule> SaveAsync(ApplicationRule rule, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ApplicationRule saved;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = clock.UtcNow;
            var toSave = rule with
            {
                ExecutablePath = WindowsPath.Normalize(rule.ExecutablePath),
                CreatedAtUtc = rule.Id == 0 ? now : rule.CreatedAtUtc,
                UpdatedAtUtc = now,
            };
            saved = await store.SaveAsync(toSave, cancellationToken).ConfigureAwait(false);
            _rules = await store.GetAllAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return saved;
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        bool deleted;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            deleted = await store.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
            _rules = await store.GetAllAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return deleted;
    }
}
