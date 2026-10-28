using System.Collections.Concurrent;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Enforcement;
using WorkToUnlock.Core.Ipc;
using WorkToUnlock.Core.Persistence;
using WorkToUnlock.Enforcement.Discovery;
using WorkToUnlock.Enforcement.Processes;
using WorkToUnlock.Enforcement.Strict;

namespace WorkToUnlock.Agent.Tests;

internal sealed class FakeProcessInspector : IProcessInspector
{
    public int CurrentProcessId { get; set; } = 1000;

    public int CurrentSessionId { get; set; } = 1;

    public List<ProcessEntry> Processes { get; } = [];

    public List<int> CloseRequests { get; } = [];

    public List<int> Terminated { get; } = [];

    public HashSet<int> DenyTermination { get; } = [];

    public ProcessEntry Start(int pid, string path, bool owned = true, bool accessible = true, int session = 1, bool critical = false)
    {
        var entry = new ProcessEntry(pid, pid * 10L, path, session, owned, accessible, critical);
        Processes.Add(entry);
        return entry;
    }

    public IReadOnlyList<ProcessEntry> Snapshot() => Processes.ToList();

    public int RequestClose(ProcessEntry process)
    {
        CloseRequests.Add(process.ProcessId);
        return 1;
    }

    public TerminationOutcome Terminate(ProcessEntry process)
    {
        if (DenyTermination.Contains(process.ProcessId))
        {
            return TerminationOutcome.AccessDenied;
        }

        Terminated.Add(process.ProcessId);
        Processes.RemoveAll(p => p.ProcessId == process.ProcessId);
        return TerminationOutcome.Terminated;
    }
}

internal sealed class RecordingNotifier : IEnforcementNotifier
{
    public List<string> Blocked { get; } = [];

    public List<string> Closing { get; } = [];

    public void OnLaunchBlocked(ApplicationRule rule) => Blocked.Add(rule.Name);

    public void OnClosingRestrictedApp(ApplicationRule rule, TimeSpan gracePeriod) => Closing.Add(rule.Name);
}

internal sealed class FakeEnforcer : IAppEnforcer
{
    public EnforcementHealth Health { get; private set; } = EnforcementHealth.Inactive(EnforcementMode.CloseThenTerminate, "inactive");

    public bool Active { get; private set; }

    public IReadOnlyCollection<ApplicationRule> Rules { get; private set; } = [];

    public int ApplyCalls { get; private set; }

    public event EventHandler<EnforcementHealth>? HealthChanged;

    /// <summary>Changes the reported health without raising <see cref="HealthChanged"/> (like a scan loop that silently stops).</summary>
    public void SetHealthSilently(EnforcementHealth health) => Health = health;

    public Task ApplyRestrictionsAsync(IReadOnlyCollection<ApplicationRule> rules, EnforcementOptions options, CancellationToken cancellationToken)
    {
        ApplyCalls++;
        Active = true;
        Rules = rules;
        Health = new EnforcementHealth { State = EnforcementHealthState.Healthy, Mode = options.Mode, RestrictionsApplied = true, Message = "active" };
        HealthChanged?.Invoke(this, Health);
        return Task.CompletedTask;
    }

    public Task RemoveRestrictionsAsync(string reason, CancellationToken cancellationToken)
    {
        Active = false;
        Health = EnforcementHealth.Inactive(Health.Mode, reason);
        HealthChanged?.Invoke(this, Health);
        return Task.CompletedTask;
    }
}

internal sealed class FakeStrictClient : IStrictEnforcementClient
{
    public StrictStatus? Status { get; set; }

    public bool Available { get; set; }

    public int ApplyCalls { get; private set; }

    public int RemoveCalls { get; private set; }

    public Task<StrictStatus?> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(Available ? Status : null);

    public Task<OperationResult> ApplyAsync(IReadOnlyCollection<ApplicationRule> rules, CancellationToken cancellationToken)
    {
        ApplyCalls++;
        return Task.FromResult(Available ? OperationResult.Ok() : OperationResult.Fail("The strict enforcement service is not running."));
    }

    public Task<OperationResult> RemoveAsync(CancellationToken cancellationToken)
    {
        RemoveCalls++;
        return Task.FromResult(Available ? OperationResult.Ok() : OperationResult.Fail("The strict enforcement service is not running."));
    }
}

internal sealed class InMemorySettingsStore : ISettingsStore
{
    public AppSettings Settings { get; set; } = AppSettings.Default;

    public ConcurrentDictionary<string, string> Values { get; } = new();

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        Settings = settings;
        return Task.CompletedTask;
    }

    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken) => Task.FromResult(Values.TryGetValue(key, out var v) ? v : null);

    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken)
    {
        Values[key] = value;
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryAuditLog : IAuditLog
{
    public List<AuditEvent> Events { get; } = [];

    public Task WriteAsync(string eventType, string description, string result, CancellationToken cancellationToken)
    {
        lock (Events)
        {
            Events.Add(new AuditEvent(Events.Count + 1, DateTimeOffset.UtcNow, eventType, description, result));
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEvent>> GetRecentAsync(int count, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AuditEvent>>(Events.AsEnumerable().Reverse().Take(count).ToList());
}

internal sealed class InMemoryDowntimeStore : IDowntimeStore
{
    public List<DowntimePeriod> Periods { get; } = [];

    public Task AddAsync(string kind, DateTimeOffset startUtc, DateTimeOffset? endUtc, CancellationToken cancellationToken)
    {
        lock (Periods)
        {
            Periods.Add(new DowntimePeriod(kind, startUtc, endUtc));
        }

        return Task.CompletedTask;
    }

    public Task CloseOpenAsync(string kind, DateTimeOffset endUtc, CancellationToken cancellationToken)
    {
        lock (Periods)
        {
            for (var i = 0; i < Periods.Count; i++)
            {
                if (Periods[i].Kind == kind && Periods[i].EndUtc is null)
                {
                    Periods[i] = Periods[i] with { EndUtc = endUtc };
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DowntimePeriod>> GetSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        lock (Periods)
        {
            return Task.FromResult<IReadOnlyList<DowntimePeriod>>(Periods.Where(p => p.EndUtc is null || p.EndUtc >= sinceUtc).ToList());
        }
    }
}
internal sealed class InMemoryAchievementStore : IAchievementStore
{
    public ConcurrentDictionary<string, DateTimeOffset> Unlocked { get; } = new(StringComparer.Ordinal);

    public Task<IReadOnlyDictionary<string, DateTimeOffset>> GetUnlockedAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, DateTimeOffset>>(new Dictionary<string, DateTimeOffset>(Unlocked, StringComparer.Ordinal));

    public Task<bool> UnlockAsync(string id, DateTimeOffset unlockedUtc, CancellationToken cancellationToken) => Task.FromResult(Unlocked.TryAdd(id, unlockedUtc));
}
internal sealed class FakeReleaseClient : WorkToUnlock.Infrastructure.Updates.IReleaseClient
{
    public string? Json { get; set; }

    public Exception? Failure { get; set; }

    public int Calls { get; private set; }

    public Task<string?> GetLatestReleaseJsonAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Failure is null ? Task.FromResult(Json) : Task.FromException<string?>(Failure);
    }
}
internal sealed class FakeDiscovery : IApplicationDiscovery
{
    public Func<string, RuleType, ExecutableInspection> OnInspect { get; set; } = (path, _) => new ExecutableInspection
    {
        Exists = true,
        ExecutablePath = path,
        SuggestedName = Path.GetFileNameWithoutExtension(path),
        Identity = new ExecutableIdentity { FileName = path[(path.LastIndexOf('\\') + 1)..], Scope = MatchScope.ExactPath },
    };

    public int InspectCalls { get; private set; }

    public ExecutableInspection Inspect(string executablePath, RuleType ruleType)
    {
        InspectCalls++;
        return OnInspect(executablePath, ruleType);
    }

    public ApplicationRule? Revalidate(ApplicationRule rule) => rule;

    public IReadOnlyList<InstalledApplication> ListInstalledApplications() => [];

    public IReadOnlyList<KnownApplication> DetectDefaults() => [];
}
