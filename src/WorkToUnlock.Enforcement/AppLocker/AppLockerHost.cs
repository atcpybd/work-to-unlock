using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace WorkToUnlock.Enforcement.AppLocker;

public sealed record AppLockerSupport(bool Supported, string Message, string Edition);

/// <summary>
/// Checks whether this Windows installation can enforce AppLocker rules written to the local policy.
/// Microsoft documents Group Policy (local policy) management of AppLocker only for Enterprise, Education and Server
/// editions, so Home and Pro are reported as unsupported rather than silently doing nothing.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AppLockerCompatibility
{
    private static readonly string[] SupportedEditions =
    [
        "Enterprise", "EnterpriseS", "EnterpriseN", "EnterpriseSN", "Education", "EducationN",
        "IoTEnterprise", "IoTEnterpriseS", "ServerStandard", "ServerDatacenter", "ServerStandardEval", "ServerDatacenterEval",
    ];

    public static AppLockerSupport Check()
    {
        string edition;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            edition = key?.GetValue("EditionID") as string ?? "Unknown";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            edition = "Unknown";
        }

        if (!SupportedEditions.Contains(edition, StringComparer.OrdinalIgnoreCase))
        {
            return new AppLockerSupport(false, $"This Windows edition ({edition}) does not enforce local AppLocker policies. Strict mode needs Windows Enterprise, Education or Server; process enforcement keeps working.", edition);
        }

        try
        {
            using var service = new ServiceController("AppIDSvc");
            _ = service.Status;
        }
        catch (InvalidOperationException)
        {
            return new AppLockerSupport(false, "The Application Identity service (AppIDSvc) is missing, so AppLocker cannot be enforced.", edition);
        }

        return new AppLockerSupport(true, $"AppLocker is supported on this edition ({edition}).", edition);
    }
}

/// <summary>
/// Reads and writes the local AppLocker policy with the built-in AppLocker PowerShell module, keeping an ownership
/// file and a backup of the policy that existed before the first change.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AppLockerPolicyStore(string stateDirectory, ILogger logger)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string OwnershipPath => Path.Combine(stateDirectory, "applocker-owned.json");

    public AppLockerOwnership LoadOwnership()
    {
        try
        {
            return File.Exists(OwnershipPath)
                ? JsonSerializer.Deserialize<AppLockerOwnership>(File.ReadAllText(OwnershipPath), Json) ?? new AppLockerOwnership()
                : new AppLockerOwnership();
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "The AppLocker ownership file is damaged; owned rules will be recognized by their marker");
            return new AppLockerOwnership();
        }
    }

    public void SaveOwnership(AppLockerOwnership ownership)
    {
        Directory.CreateDirectory(stateDirectory);
        if (ownership.IsEmpty)
        {
            File.Delete(OwnershipPath);
            return;
        }

        var temp = OwnershipPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(ownership, Json));
        File.Move(temp, OwnershipPath, overwrite: true);
    }

    public string GetLocalPolicy() => RunPowerShell("Get-AppLockerPolicy -Local -Xml");

    /// <summary>Replaces the local policy with <paramref name="policyXml"/> (which already contains every unrelated rule).</summary>
    public void SetLocalPolicy(string policyXml, AppLockerOwnership previousOwnership)
    {
        Directory.CreateDirectory(stateDirectory);
        if (previousOwnership.IsEmpty)
        {
            // First change: keep the administrator's original policy for manual recovery.
            File.WriteAllText(Path.Combine(stateDirectory, $"applocker-backup-{DateTime.UtcNow:yyyyMMddHHmmss}.xml"), GetLocalPolicy(), Encoding.UTF8);
        }

        var file = Path.Combine(stateDirectory, "applocker-pending.xml");
        File.WriteAllText(file, policyXml, Encoding.UTF8);
        try
        {
            RunPowerShell($"Set-AppLockerPolicy -XmlPolicy '{file.Replace("'", "''", StringComparison.Ordinal)}'");
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>AppLocker needs the Application Identity service. Starts it if it is not running.</summary>
    public string? EnsureApplicationIdentityRunning()
    {
        try
        {
            using var service = new ServiceController("AppIDSvc");
            if (service.Status != ServiceControllerStatus.Running)
            {
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            }

            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(ex, "Could not start the Application Identity service");
            return "The Application Identity service could not be started, so AppLocker rules are not enforced.";
        }
    }

    private string RunPowerShell(string command)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", $"[Console]::OutputEncoding = [Text.Encoding]::UTF8; Import-Module AppLocker; {command}" })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell could not be started.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            process.Kill();
            throw new InvalidOperationException("The AppLocker command timed out.");
        }

        if (process.ExitCode != 0 || error.Result.Trim().Length > 0)
        {
            throw new InvalidOperationException($"AppLocker command failed: {error.Result.Trim()}");
        }

        return output.Result.Trim();
    }
}
