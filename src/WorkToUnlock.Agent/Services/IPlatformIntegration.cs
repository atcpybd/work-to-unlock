namespace WorkToUnlock.Agent.Services;

/// <summary>OS integration: tray, session notifications, startup registration and crash-restart registration.</summary>
public interface IPlatformIntegration
{
    void Start();

    void ApplyStartupRegistration(bool startWithWindows);
}

public sealed class NullPlatformIntegration : IPlatformIntegration
{
    public void Start()
    {
    }

    public void ApplyStartupRegistration(bool startWithWindows)
    {
    }
}
