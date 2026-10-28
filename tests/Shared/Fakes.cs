using System.Collections.Concurrent;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Persistence;
using WorkToUnlock.Core.Rules;
using WorkToUnlock.Core.Time;

namespace WorkToUnlock.Testing;

/// <summary>Manually driven clock. Advance moves the wall clock and the monotonic clock together.</summary>
public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    public long MonotonicMilliseconds { get; set; } = 10_000_000;

    public string? BootId { get; set; } = "boot-1";

    public void Advance(TimeSpan by)
    {
        UtcNow += by;
        MonotonicMilliseconds += (long)by.TotalMilliseconds;
    }

    public void AdvanceMilliseconds(long ms) => Advance(TimeSpan.FromMilliseconds(ms));

    /// <summary>Simulates a reboot: monotonic counter restarts and the boot id changes.</summary>
    public void Reboot(TimeSpan downtime)
    {
        UtcNow += downtime;
        MonotonicMilliseconds = 5_000;
        BootId = "boot-" + Guid.NewGuid().ToString("N")[..6];
    }
}

public sealed class InMemoryRewardStateStore : IRewardStateStore
{
    private readonly Lock _lock = new();

    public RewardStateRecord? Stored { get; set; }

    public List<RewardTransition> Transitions { get; } = [];

    public int SaveCount { get; private set; }

    public TimeSpan SaveDelay { get; set; }

    public int FailNextSaves { get; set; }

    public Task<RewardStateRecord?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Stored);

    public async Task<RewardStateRecord> SaveAsync(RewardStateRecord record, long expectedVersion, RewardTransition? transition, CancellationToken cancellationToken)
    {
        if (SaveDelay > TimeSpan.Zero)
        {
            await Task.Delay(SaveDelay, cancellationToken);
        }

        lock (_lock)
        {
            if (FailNextSaves > 0)
            {
                FailNextSaves--;
                throw new PersistenceException("Simulated disk failure");
            }

            var current = Stored?.Version ?? 0;
            if (current != expectedVersion)
            {
                throw new ConcurrencyConflictException($"Expected version {expectedVersion} but found {current}");
            }

            Stored = record with { Version = expectedVersion + 1 };
            SaveCount++;
            if (transition is not null)
            {
                Transitions.Add(transition);
            }

            return Stored;
        }
    }
}

public sealed class InMemoryRuleStore : IApplicationRuleStore
{
    private readonly ConcurrentDictionary<long, ApplicationRule> _rules = new();
    private long _nextId;

    public InMemoryRuleStore(params ApplicationRule[] rules)
    {
        foreach (var rule in rules)
        {
            var id = rule.Id == 0 ? Interlocked.Increment(ref _nextId) : rule.Id;
            _nextId = Math.Max(_nextId, id);
            _rules[id] = rule with { Id = id };
        }
    }

    public Task<IReadOnlyList<ApplicationRule>> GetAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ApplicationRule>>(_rules.Values.OrderBy(r => r.Id).ToList());

    public Task<ApplicationRule> SaveAsync(ApplicationRule rule, CancellationToken cancellationToken)
    {
        var saved = rule.Id == 0 ? rule with { Id = Interlocked.Increment(ref _nextId) } : rule;
        _rules[saved.Id] = saved;
        return Task.FromResult(saved);
    }

    public Task<bool> DeleteAsync(long id, CancellationToken cancellationToken) => Task.FromResult(_rules.TryRemove(id, out _));
}

public sealed class InMemoryWorkSessionStore : IWorkSessionStore
{
    private long _nextId;

    public ConcurrentDictionary<long, WorkSessionRecord> Sessions { get; } = new();

    public int FailNextWrites { get; set; }

    public Task<long> InsertAsync(WorkSessionRecord session, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        var id = Interlocked.Increment(ref _nextId);
        Sessions[id] = session with { Id = id };
        return Task.FromResult(id);
    }

    public Task UpdateAsync(WorkSessionRecord session, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        Sessions[session.Id] = session;
        return Task.CompletedTask;
    }

    private void ThrowIfFailing()
    {
        if (FailNextWrites > 0)
        {
            FailNextWrites--;
            throw new PersistenceException("Simulated disk failure");
        }
    }
}

public sealed class FakeSignatureReader : IExecutableSignatureReader
{
    private readonly Dictionary<string, ExecutableSignature> _signatures = new(StringComparer.OrdinalIgnoreCase);

    public int Calls { get; private set; }

    public FakeSignatureReader Sign(string path, string publisher, bool trusted = true)
    {
        _signatures[WindowsPath.Normalize(path)] = new ExecutableSignature(publisher, trusted);
        return this;
    }

    public ExecutableSignature? GetSignature(string executablePath)
    {
        Calls++;
        return _signatures.TryGetValue(WindowsPath.Normalize(executablePath), out var signature)
            ? signature
            : new ExecutableSignature(null, false);
    }
}

public static class TestRules
{
    public const string VsCodePath = @"C:\Users\dev\AppData\Local\Programs\Microsoft VS Code\Code.exe";
    public const string BlockbenchPath = @"C:\Users\dev\AppData\Local\Programs\Blockbench\Blockbench.exe";
    public const string ChromePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    public const string SteamPath = @"C:\Program Files (x86)\Steam\steam.exe";
    public const string SteamHelperPath = @"C:\Program Files (x86)\Steam\bin\cef\cef.win7x64\steamwebhelper.exe";
    public const string DiscordPath = @"C:\Users\dev\AppData\Local\Discord\app-1.0.9035\Discord.exe";

    public static ApplicationRule Work(long id, string name, string path, bool countsTowardGoal = true, bool enabled = true) => new()
    {
        Id = id,
        Name = name,
        ExecutablePath = path,
        RuleType = RuleType.Work,
        Enabled = enabled,
        CountsTowardGoal = countsTowardGoal,
        Identity = new ExecutableIdentity { FileName = WindowsPath.GetFileName(path), Scope = MatchScope.ExactPath },
    };

    public static ApplicationRule Restricted(long id, string name, string path, ExecutableIdentity? identity = null) => new()
    {
        Id = id,
        Name = name,
        ExecutablePath = path,
        RuleType = RuleType.Restricted,
        Identity = identity ?? new ExecutableIdentity { FileName = WindowsPath.GetFileName(path), Scope = MatchScope.ExactPath },
    };

    public static SystemFolders Folders { get; } = new(
        @"C:\Windows",
        [@"C:\Program Files", @"C:\Program Files (x86)"],
        [@"C:\Program Files\Work to Unlock"]);
}
