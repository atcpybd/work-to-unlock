namespace WorkToUnlock.Infrastructure;

/// <summary>Per-user data locations. All data stays on this machine.</summary>
public sealed class AppPaths
{
    /// <summary>Overrides the data directory (used by tests and portable runs).</summary>
    public const string DataDirectoryVariable = "WORKTOUNLOCK_DATA_DIR";

    /// <summary>
    /// Names an isolated instance (separate pipe and single-instance mutex, no startup registration). Used by
    /// integration tests so they never interfere with a real installation.
    /// </summary>
    public const string InstanceVariable = "WORKTOUNLOCK_INSTANCE";

    /// <summary>The isolated instance name, or null for the normal installation.</summary>
    public static string? InstanceName
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(InstanceVariable);
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var cleaned = new string(value.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());
            return cleaned.Length == 0 ? null : cleaned;
        }
    }

    /// <summary>Suffix for per-instance kernel object and pipe names.</summary>
    public static string InstanceSuffix => InstanceName is { } name ? "." + name : string.Empty;

    public AppPaths(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        LogsDirectory = Path.Combine(dataDirectory, "logs");
        DatabasePath = Path.Combine(dataDirectory, "worktounlock.db");
        BackupPath = Path.Combine(dataDirectory, "worktounlock.backup.db");
    }

    public string DataDirectory { get; }

    public string LogsDirectory { get; }

    public string DatabasePath { get; }

    public string BackupPath { get; }

    public static AppPaths ForCurrentUser()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        var root = !string.IsNullOrWhiteSpace(overridden)
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkToUnlock");
        return new AppPaths(root);
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
