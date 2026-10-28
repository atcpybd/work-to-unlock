using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace WorkToUnlock.Agent.Windows;

/// <summary>Registers the agent to start at sign-in.</summary>
[SupportedOSPlatform("windows")]
internal static class StartupRegistration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "WorkToUnlock";
    public const string AutostartArgument = "--autostart";

    /// <summary>
    /// The installer registers the agent for every user under HKLM. Without an installer (portable/dev runs) the agent
    /// registers itself under HKCU. Either way, the per-user "Start with Windows" setting is honoured at sign-in.
    /// </summary>
    public static void Apply(bool enabled, string agentPath, ILogger logger)
    {
        try
        {
            using var machine = Registry.LocalMachine.OpenSubKey(RunKeyPath);
            var machineWide = machine?.GetValue(ValueName) is string;

            using var user = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled && !machineWide)
            {
                user.SetValue(ValueName, $"\"{agentPath}\" {AutostartArgument}", RegistryValueKind.String);
            }
            else if (user.GetValue(ValueName) is not null)
            {
                user.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            logger.LogWarning(ex, "Could not update the startup registration");
        }
    }
}

/// <summary>Windows Error Reporting restart registration: the agent is relaunched after a crash or hang.</summary>
[SupportedOSPlatform("windows")]
internal static partial class CrashRecovery
{
    public const string RestartedArgument = "--restarted";

    public static void Register(ILogger logger)
    {
        // Flags 0: restart after crashes and hangs, but not after updates or reboots (startup registration covers those).
        var hr = RegisterApplicationRestart(RestartedArgument, 0);
        if (hr != 0)
        {
            logger.LogWarning("RegisterApplicationRestart failed: 0x{Result:X8}", hr);
        }
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegisterApplicationRestart(string commandLine, uint flags);
}
