using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace WorkToUnlock.Service;

/// <summary>Administrator consent for strict mode. Stored where only administrators and SYSTEM can write.</summary>
public sealed record StrictModeConsent
{
    public bool Enabled { get; init; }

    public string? ChangedBy { get; init; }

    public DateTimeOffset? ChangedUtc { get; init; }
}

[SupportedOSPlatform("windows")]
public static class StrictModeState
{
    public const string ServiceName = "WorkToUnlockStrict";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Directory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WorkToUnlock");

    public static string ConsentPath => Path.Combine(Directory, "strict-mode.json");

    public static string LogsDirectory => Path.Combine(Directory, "logs");

    /// <summary>Creates the state folder with an ACL that grants access only to SYSTEM and Administrators.</summary>
    public static void EnsureDirectory()
    {
        var info = new DirectoryInfo(Directory);
        if (!info.Exists)
        {
            info.Create();
        }

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
        System.IO.Directory.CreateDirectory(LogsDirectory);
    }

    public static StrictModeConsent LoadConsent()
    {
        try
        {
            return File.Exists(ConsentPath) ? JsonSerializer.Deserialize<StrictModeConsent>(File.ReadAllText(ConsentPath)) ?? new() : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new StrictModeConsent();
        }
    }

    public static void SaveConsent(StrictModeConsent consent)
    {
        EnsureDirectory();
        File.WriteAllText(ConsentPath, JsonSerializer.Serialize(consent, Json));
    }

    public static bool IsAdministrator() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
}
