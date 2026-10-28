namespace WorkToUnlock.Core.Rules;

/// <summary>
/// Windows path helpers that work identically on every OS, so rule matching can be unit tested on Linux.
/// Paths are compared case-insensitively with backslash separators.
/// </summary>
public static class WindowsPath
{
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalized = path.Trim().Trim('"').Replace('/', '\\');

        // Collapse duplicate separators except a leading UNC prefix.
        var isUnc = normalized.StartsWith(@"\\", StringComparison.Ordinal);
        while (normalized.Contains(@"\\", StringComparison.Ordinal))
        {
            normalized = normalized.Replace(@"\\", @"\", StringComparison.Ordinal);
        }

        if (isUnc)
        {
            normalized = @"\" + normalized;
        }

        // Strip the Win32 extended-length prefix.
        if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            normalized = normalized[4..];
        }

        return normalized.TrimEnd('\\');
    }

    public static bool PathEquals(string? a, string? b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> is inside <paramref name="folder"/> (not equal to it).</summary>
    public static bool IsUnder(string? path, string? folder)
    {
        var p = Normalize(path);
        var f = Normalize(folder);
        if (p.Length == 0 || f.Length == 0)
        {
            return false;
        }

        return p.Length > f.Length + 1
            && p.StartsWith(f, StringComparison.OrdinalIgnoreCase)
            && p[f.Length] == '\\';
    }

    public static string GetFileName(string? path)
    {
        var p = Normalize(path);
        var index = p.LastIndexOf('\\');
        return index < 0 ? p : p[(index + 1)..];
    }

    public static string GetDirectory(string? path)
    {
        var p = Normalize(path);
        var index = p.LastIndexOf('\\');
        return index <= 0 ? string.Empty : p[..index];
    }

    /// <summary>True for a fully qualified drive path (C:\...) or UNC path.</summary>
    public static bool IsFullyQualified(string? path)
    {
        var p = Normalize(path);
        return (p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\')
            || p.StartsWith(@"\\", StringComparison.Ordinal);
    }

    /// <summary>True for a drive root such as "C:" or "C:\".</summary>
    public static bool IsDriveRoot(string? path)
    {
        var p = Normalize(path);
        return p.Length == 2 && char.IsAsciiLetter(p[0]) && p[1] == ':';
    }
}
