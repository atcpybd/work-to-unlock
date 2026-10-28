using WorkToUnlock.Core.Ipc;
using WorkToUnlock.Core.Rewards;
using WorkToUnlock.Core.Time;
using WorkToUnlock.Core.Tracking;

namespace WorkToUnlock.Agent.Services;

/// <summary>Builds the status snapshot shared by the tray and the dashboards.</summary>
public sealed class AgentStatusService(
    IRewardEngine engine,
    IWorkTracker tracker,
    EnforcementCoordinator enforcement,
    SettingsService settings,
    AgentInfo info,
    IClock clock)
{
    private volatile bool _databaseHealthy = true;

    public void SetDatabaseHealthy(bool healthy) => _databaseHealthy = healthy;

    public AgentStatus Build() => new()
    {
        Reward = engine.GetStatus(),
        Tracking = tracker.Current,
        Enforcement = enforcement.Health,
        EnforcementEnabled = settings.Current.EnforcementEnabled,
        PersistenceHealthy = _databaseHealthy && tracker.PersistenceHealthy,
        NotificationsEnabled = settings.Current.NotificationsEnabled,
        AgentVersion = info.Version,
        AgentStartedUtc = info.StartedUtc,
        GeneratedUtc = clock.UtcNow,
    };
}
