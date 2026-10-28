using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Ipc;
using WorkToUnlock.Core.Rules;

namespace WorkToUnlock.Enforcement.Discovery;

/// <summary>A default application found on this machine.</summary>
public sealed record KnownApplication(string Name, string ExecutablePath, RuleType RuleType);

/// <summary>
/// Inspects executables chosen by the user and suggests how to identify them: publisher, install folder, and
/// helper/updater executables (Steam's steamwebhelper.exe, Discord's versioned app-x.y.z folders, ...).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class ApplicationDiscovery(IExecutableSignatureReader signatures, SystemFolders folders, ILogger<ApplicationDiscovery> logger) : IApplicationDiscovery
{
    private const int MaxFilesScanned = 400;
    private const int MaxSignatureChecks = 60;
    private const long MaxSignatureCheckBytes = 300L * 1024 * 1024;

    /// <summary>Folders inside install roots that hold unrelated content (games, caches).</summary>
    private static readonly string[] SkippedSubfolders = [@"\steamapps\", @"\cache\", @"\logs\", @"\dumps\"];

    public ExecutableInspection Inspect(string executablePath, RuleType ruleType)
    {
        var path = WindowsPath.Normalize(executablePath);
        try
        {
            path = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Missing(path, "The path is not valid.");
        }

        if (!File.Exists(path))
        {
            return Missing(path, "The file does not exist.");
        }

        var version = TryGetVersionInfo(path);
        var signature = signatures.GetSignature(path);
        var trustedPublisher = signature is { Trusted: true } ? signature.Publisher : null;
        var installRoot = ruleType == RuleType.Restricted ? SuggestInstallRoot(path) : null;
        var related = installRoot is null ? [] : FindRelatedExecutables(installRoot, path, trustedPublisher, version?.CompanyName);

        var scope = ruleType switch
        {
            RuleType.Restricted when installRoot is not null => MatchScope.InstallFolder,
            _ when trustedPublisher is not null => MatchScope.PublisherAndFileName,
            _ => MatchScope.ExactPath,
        };

        var identity = new ExecutableIdentity
        {
            FileName = Path.GetFileName(path),
            Publisher = trustedPublisher,
            SignatureTrusted = signature?.Trusted ?? false,
            ProductName = Clean(version?.ProductName),
            CompanyName = Clean(version?.CompanyName),
            FileVersion = Clean(version?.FileVersion),
            InstallRoot = installRoot,
            Scope = scope,
            RelatedExecutables = related,
            LastValidatedUtc = DateTimeOffset.UtcNow,
        };

        var name = Clean(version?.FileDescription) ?? Clean(version?.ProductName) ?? Path.GetFileNameWithoutExtension(path);
        var candidate = new ApplicationRule { Name = name, ExecutablePath = path, RuleType = ruleType, Identity = identity };

        return new ExecutableInspection
        {
            Exists = true,
            ExecutablePath = path,
            SuggestedName = name,
            Identity = identity,
            Problems = RuleSafetyValidator.Validate(candidate, folders),
        };
    }

    /// <summary>
    /// Re-checks a rule after an update: refreshes its identity from the configured path, or, when the path no longer
    /// exists, looks for the newest executable with the same name and publisher (for example a new Squirrel app-x.y.z folder).
    /// </summary>
    public ApplicationRule? Revalidate(ApplicationRule rule)
    {
        var path = rule.ExecutablePath;
        if (!File.Exists(path))
        {
            path = FindRelocated(rule) ?? path;
        }

        var inspection = Inspect(path, rule.RuleType);
        if (!inspection.Exists)
        {
            return null;
        }

        var identity = inspection.Identity;
        if (rule.Identity.Publisher is not null && identity.Publisher is not null
            && !string.Equals(rule.Identity.Publisher, identity.Publisher, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Publisher of {Path} changed from {Old} to {New}", path, rule.Identity.Publisher, identity.Publisher);
        }

        // Keep the user's chosen scope unless the new identity cannot support it.
        var scope = rule.Identity.Scope switch
        {
            MatchScope.InstallFolder when identity.InstallRoot is null => identity.Scope,
            MatchScope.PublisherAndFileName when identity.Publisher is null => MatchScope.ExactPath,
            var s => s,
        };

        return rule with { ExecutablePath = inspection.ExecutablePath, Identity = identity with { Scope = scope } };
    }

    /// <summary>Lists installed applications from the Uninstall and App Paths registry keys.</summary>
    public IReadOnlyList<InstalledApplication> ListInstalledApplications()
    {
        var apps = new Dictionary<string, InstalledApplication>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, view) in new[] { (RegistryHive.CurrentUser, RegistryView.Default), (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                ReadUninstallEntries(root, apps);
                ReadAppPaths(root, apps);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                logger.LogDebug(ex, "Could not read installed applications from {Hive}", hive);
            }
        }

        return apps.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Finds the default work apps (VS Code, Blockbench) and restricted apps (Steam, Discord).</summary>
    public IReadOnlyList<KnownApplication> DetectDefaults()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var found = new List<KnownApplication>();

        void AddFirst(string name, RuleType type, params string?[] candidates)
        {
            var path = candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(c));
            if (path is not null)
            {
                found.Add(new KnownApplication(name, Path.GetFullPath(path), type));
            }
        }

        AddFirst("Visual Studio Code", RuleType.Work,
            Path.Combine(local, @"Programs\Microsoft VS Code\Code.exe"),
            Path.Combine(programFiles, @"Microsoft VS Code\Code.exe"));
        AddFirst("Blockbench", RuleType.Work,
            Path.Combine(local, @"Programs\Blockbench\Blockbench.exe"),
            Path.Combine(programFiles, @"Blockbench\Blockbench.exe"));
        AddFirst("Steam", RuleType.Restricted,
            ReadSteamPath(),
            Path.Combine(programFilesX86, @"Steam\steam.exe"),
            Path.Combine(programFiles, @"Steam\steam.exe"));
        AddFirst("Discord", RuleType.Restricted, LatestSquirrelExecutable(Path.Combine(local, "Discord"), "Discord.exe"));

        return found;
    }

    /// <summary>
    /// The folder that contains an application's helpers. Squirrel apps (Discord) keep executables in versioned
    /// app-x.y.z folders, so the root is one level above. Generic shared folders are never returned.
    /// </summary>
    internal string? SuggestInstallRoot(string executablePath)
    {
        var directory = WindowsPath.GetDirectory(executablePath);
        if (SquirrelFolder().IsMatch(WindowsPath.GetFileName(directory)))
        {
            directory = WindowsPath.GetDirectory(directory);
        }

        var probe = new ApplicationRule
        {
            Name = "probe",
            ExecutablePath = executablePath,
            RuleType = RuleType.Restricted,
            Identity = new ExecutableIdentity { InstallRoot = directory, Scope = MatchScope.InstallFolder },
        };

        return RuleSafetyValidator.Validate(probe, folders).Any(e => e.Property == nameof(ExecutableIdentity.InstallRoot)) ? null : directory;
    }

    private IReadOnlyList<string> FindRelatedExecutables(string root, string mainExecutable, string? publisher, string? company)
    {
        var related = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var signatureChecks = 0;
        try
        {
            var files = Directory.EnumerateFiles(root, "*.exe", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 4,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            });

            foreach (var file in files.Take(MaxFilesScanned))
            {
                if (SkippedSubfolders.Any(s => file.Contains(s, StringComparison.OrdinalIgnoreCase))
                    || string.Equals(file, mainExecutable, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var sameCompany = company is not null && string.Equals(Clean(TryGetVersionInfo(file)?.CompanyName), company, StringComparison.OrdinalIgnoreCase);
                if (publisher is not null && signatureChecks < MaxSignatureChecks && new FileInfo(file).Length <= MaxSignatureCheckBytes)
                {
                    signatureChecks++;
                    var signature = signatures.GetSignature(file);
                    if (signature is { Trusted: true } && string.Equals(signature.Publisher, publisher, StringComparison.OrdinalIgnoreCase))
                    {
                        related.Add(Path.GetFileName(file));
                    }
                }
                else if (sameCompany)
                {
                    related.Add(Path.GetFileName(file));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not scan {Root}", root);
        }

        return related.ToList();
    }

    private string? FindRelocated(ApplicationRule rule)
    {
        var fileName = WindowsPath.GetFileName(rule.ExecutablePath);
        var root = rule.Identity.InstallRoot ?? SuggestInstallRoot(rule.ExecutablePath);
        if (root is null || !Directory.Exists(root))
        {
            return null;
        }

        return LatestSquirrelExecutable(root, fileName)
            ?? Directory.EnumerateFiles(root, fileName, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true })
                .FirstOrDefault(f => rule.Identity.Publisher is null
                    || string.Equals(signatures.GetSignature(f)?.Publisher, rule.Identity.Publisher, StringComparison.OrdinalIgnoreCase));
    }

    private static string? LatestSquirrelExecutable(string root, string fileName)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        return Directory.EnumerateDirectories(root, "app-*")
            .Select(d => (Dir: d, Version: Version.TryParse(Path.GetFileName(d)[4..], out var v) ? v : null))
            .Where(d => d.Version is not null && File.Exists(Path.Combine(d.Dir, fileName)))
            .OrderByDescending(d => d.Version)
            .Select(d => Path.Combine(d.Dir, fileName))
            .FirstOrDefault();
    }

    private static string? ReadSteamPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamExe") is string exe ? WindowsPath.Normalize(exe) : null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    private static void ReadUninstallEntries(RegistryKey root, Dictionary<string, InstalledApplication> apps)
    {
        using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
        if (uninstall is null)
        {
            return;
        }

        foreach (var name in uninstall.GetSubKeyNames())
        {
            using var entry = uninstall.OpenSubKey(name);
            if (entry?.GetValue("DisplayName") is not string displayName
                || entry.GetValue("SystemComponent") is 1
                || entry.GetValue("ParentKeyName") is not null)
            {
                continue;
            }

            var exe = ExecutableFromIcon(entry.GetValue("DisplayIcon") as string)
                ?? ExecutableFromInstallLocation(entry.GetValue("InstallLocation") as string, displayName);
            if (exe is not null && !apps.ContainsKey(exe))
            {
                apps[exe] = new InstalledApplication(displayName.Trim(), exe, entry.GetValue("Publisher") as string, "Installed programs");
            }
        }
    }

    private static void ReadAppPaths(RegistryKey root, Dictionary<string, InstalledApplication> apps)
    {
        using var appPaths = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths");
        if (appPaths is null)
        {
            return;
        }

        foreach (var name in appPaths.GetSubKeyNames())
        {
            using var entry = appPaths.OpenSubKey(name);
            if (entry?.GetValue(null) is string value)
            {
                var exe = ExecutableFromIcon(Environment.ExpandEnvironmentVariables(value));
                if (exe is not null && !apps.ContainsKey(exe))
                {
                    var display = Clean(TryGetVersionInfo(exe)?.FileDescription) ?? Path.GetFileNameWithoutExtension(exe);
                    apps[exe] = new InstalledApplication(display, exe, Clean(TryGetVersionInfo(exe)?.CompanyName), "App Paths");
                }
            }
        }
    }

    private static string? ExecutableFromIcon(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var path = value.Trim();
        var comma = path.LastIndexOf(',');
        if (comma > 2 && !path[comma..].Contains('\\'))
        {
            path = path[..comma];
        }

        path = WindowsPath.Normalize(Environment.ExpandEnvironmentVariables(path));
        return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && WindowsPath.IsFullyQualified(path) && File.Exists(path) ? path : null;
    }

    private static string? ExecutableFromInstallLocation(string? location, string displayName)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        var root = WindowsPath.Normalize(location);
        if (!Directory.Exists(root))
        {
            return null;
        }

        try
        {
            var exes = Directory.GetFiles(root, "*.exe", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).Contains("unins", StringComparison.OrdinalIgnoreCase)
                    && !Path.GetFileName(f).Equals("Update.exe", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var compact = displayName.Replace(" ", string.Empty, StringComparison.Ordinal);
            return exes.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(compact, StringComparison.OrdinalIgnoreCase))
                ?? (exes.Count == 1 ? exes[0] : LatestSquirrelExecutable(root, compact + ".exe"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static FileVersionInfo? TryGetVersionInfo(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ExecutableInspection Missing(string path, string message) => new()
    {
        Exists = false,
        ExecutablePath = path,
        SuggestedName = WindowsPath.GetFileName(path),
        Problems = [new ValidationError(nameof(ApplicationRule.ExecutablePath), message)],
    };

    [GeneratedRegex(@"^app-\d+(\.\d+)*$", RegexOptions.IgnoreCase)]
    private static partial Regex SquirrelFolder();
}
