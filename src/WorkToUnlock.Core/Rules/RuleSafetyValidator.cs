using WorkToUnlock.Core.Domain;

namespace WorkToUnlock.Core.Rules;

/// <summary>Machine-specific folders the safety rules need. Provided by the platform layer.</summary>
public sealed record SystemFolders(
    string WindowsDirectory,
    IReadOnlyList<string> ProgramFilesDirectories,
    IReadOnlyList<string> ProtectedInstallDirectories);

/// <summary>
/// Prevents rules that could interfere with Windows, with uninstalling, or with Work to Unlock itself.
/// Applied when rules are saved and again by the enforcer before acting on a process.
/// </summary>
public static class RuleSafetyValidator
{
    /// <summary>Executables that must never be restricted regardless of location.</summary>
    public static readonly IReadOnlySet<string> ProtectedExecutableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "csrss.exe", "winlogon.exe", "wininit.exe", "lsass.exe", "services.exe", "smss.exe",
        "svchost.exe", "dwm.exe", "sihost.exe", "ctfmon.exe", "conhost.exe", "fontdrvhost.exe", "taskmgr.exe",
        "runtimebroker.exe", "shellexperiencehost.exe", "startmenuexperiencehost.exe", "searchhost.exe",
        "searchapp.exe", "textinputhost.exe", "lockapp.exe", "logonui.exe", "userinit.exe", "consent.exe",
        "msiexec.exe", "systemsettings.exe", "control.exe", "mmc.exe", "regedit.exe", "cmd.exe",
        "powershell.exe", "pwsh.exe", "applicationframehost.exe", "securityhealthsystray.exe",
        "worktounlock.app.exe", "worktounlock.agent.exe", "worktounlock.service.exe",
    };

    public static IReadOnlyList<ValidationError> Validate(ApplicationRule rule, SystemFolders folders)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(folders);
        var errors = new List<ValidationError>();

        if (string.IsNullOrWhiteSpace(rule.Name))
        {
            errors.Add(new(nameof(rule.Name), "Name is required."));
        }

        var path = WindowsPath.Normalize(rule.ExecutablePath);
        if (!WindowsPath.IsFullyQualified(path))
        {
            errors.Add(new(nameof(rule.ExecutablePath), "Choose an executable using a full path."));
            return errors;
        }

        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new(nameof(rule.ExecutablePath), "Only .exe files can be added."));
        }

        if (!Enum.IsDefined(rule.RuleType))
        {
            errors.Add(new(nameof(rule.RuleType), "Unknown rule type."));
        }

        if (rule.RuleType == RuleType.Restricted)
        {
            ValidateRestriction(rule, path, folders, errors);
        }

        return errors;
    }

    /// <summary>True when the enforcer may act on a process with this executable path.</summary>
    public static bool IsSafeToEnforce(string executablePath, SystemFolders folders)
    {
        var path = WindowsPath.Normalize(executablePath);
        return WindowsPath.IsFullyQualified(path)
            && !ProtectedExecutableNames.Contains(WindowsPath.GetFileName(path))
            && !WindowsPath.IsUnder(path, folders.WindowsDirectory)
            && !folders.ProtectedInstallDirectories.Any(d => WindowsPath.IsUnder(path, d));
    }

    private static void ValidateRestriction(ApplicationRule rule, string path, SystemFolders folders, List<ValidationError> errors)
    {
        if (ProtectedExecutableNames.Contains(WindowsPath.GetFileName(path)))
        {
            errors.Add(new(nameof(rule.ExecutablePath), "This is a Windows or Work to Unlock component and cannot be restricted."));
        }

        if (WindowsPath.IsUnder(path, folders.WindowsDirectory))
        {
            errors.Add(new(nameof(rule.ExecutablePath), "Programs inside the Windows folder cannot be restricted."));
        }

        if (folders.ProtectedInstallDirectories.Any(d => WindowsPath.IsUnder(path, d)))
        {
            errors.Add(new(nameof(rule.ExecutablePath), "Work to Unlock's own files cannot be restricted."));
        }

        if (rule.Identity.Scope == MatchScope.InstallFolder)
        {
            var root = rule.Identity.InstallRoot;
            if (string.IsNullOrWhiteSpace(root) || !WindowsPath.IsFullyQualified(root))
            {
                errors.Add(new(nameof(rule.Identity.InstallRoot), "Install-folder matching needs a full install folder path."));
            }
            else if (IsTooBroad(root, folders))
            {
                errors.Add(new(nameof(rule.Identity.InstallRoot), "The install folder is too broad to restrict safely."));
            }
            else if (!WindowsPath.IsUnder(path, root))
            {
                errors.Add(new(nameof(rule.Identity.InstallRoot), "The executable must be inside the install folder."));
            }
        }
    }

    private static bool IsTooBroad(string root, SystemFolders folders)
    {
        var r = WindowsPath.Normalize(root);
        if (WindowsPath.IsDriveRoot(r)
            || WindowsPath.PathEquals(r, folders.WindowsDirectory)
            || WindowsPath.IsUnder(r, folders.WindowsDirectory)
            || folders.ProgramFilesDirectories.Any(p => WindowsPath.PathEquals(r, p)))
        {
            return true;
        }

        // Profile-level folders such as C:\Users\name or ...\AppData\Local contain unrelated programs.
        var segments = r.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || (segments.Length <= 3 && segments[1].Equals("Users", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        string[] broadSuffixes = [@"\AppData", @"\AppData\Local", @"\AppData\Roaming", @"\AppData\Local\Programs"];
        return broadSuffixes.Any(s => r.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }
}
