using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WorkToUnlock.Infrastructure.Ipc;

/// <summary>Pipe names and ACLs. Pipes are local-only: the NETWORK group is always denied.</summary>
public static class PipeSecurityFactory
{
    public const string StrictServicePipeName = "WorkToUnlock.Strict";

    /// <summary>Per-user agent pipe name. The SID keeps users on a shared machine apart.</summary>
    public static string AgentPipeNameForCurrentUser() => (OperatingSystem.IsWindows()
        ? $"WorkToUnlock.Agent.{WindowsIdentity.GetCurrent().User!.Value}"
        : $"WorkToUnlock.Agent.{Environment.UserName}") + AppPaths.InstanceSuffix;

    /// <summary>Only the current user may connect to (or create instances of) the agent pipe.</summary>
    [SupportedOSPlatform("windows")]
    public static PipeSecurity CurrentUserOnly()
    {
        var security = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User!;
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    /// <summary>
    /// The elevated service pipe: SYSTEM and Administrators own it, signed-in users may connect but not create
    /// instances (which prevents another process from impersonating the service).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static PipeSecurity ServiceWithLocalClients()
    {
        var security = new PipeSecurity();
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        security.SetOwner(system);
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }
}
