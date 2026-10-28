using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Ipc;

namespace WorkToUnlock.Enforcement.Discovery;

public interface IApplicationDiscovery
{
    ExecutableInspection Inspect(string executablePath, RuleType ruleType);

    /// <summary>Refreshes a rule's identity after an update. Returns null when the executable cannot be found.</summary>
    ApplicationRule? Revalidate(ApplicationRule rule);

    IReadOnlyList<InstalledApplication> ListInstalledApplications();

    IReadOnlyList<KnownApplication> DetectDefaults();
}
