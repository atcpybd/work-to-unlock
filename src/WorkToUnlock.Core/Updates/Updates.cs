using System.Globalization;
using System.Text.Json;

namespace WorkToUnlock.Core.Updates;

/// <summary>Where releases are published. Update checks only ever read this repository's latest release.</summary>
public static class UpdateSource
{
    public const string Repository = "atcpybd/work-to-unlock";

    public static Uri LatestReleaseApi { get; } = new($"https://api.github.com/repos/{Repository}/releases/latest");

    public static Uri ReleasesPage { get; } = new($"https://github.com/{Repository}/releases");

    /// <summary>Only files from GitHub release downloads of this repository are installed.</summary>
    public static bool IsTrustedDownload(Uri? uri) =>
        uri is { Scheme: "https", Host: "github.com" }
        && uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The result of an update check, as shown in the dashboard.</summary>
public sealed record UpdateInfo
{
    public string CurrentVersion { get; init; } = string.Empty;

    public string? LatestVersion { get; init; }

    public bool IsUpdateAvailable { get; init; }

    public Uri? ReleasePage { get; init; }

    public Uri? InstallerUrl { get; init; }

    public string? InstallerName { get; init; }

    public long InstallerSize { get; init; }

    /// <summary>SHA256SUMS.txt of the release; the installer is only run when its hash is listed there.</summary>
    public Uri? ChecksumsUrl { get; init; }

    public DateTimeOffset? CheckedUtc { get; init; }

    public string? Error { get; init; }
}

public static class ReleaseParser
{
    /// <summary>Reads a GitHub "latest release" response and compares it with the running version.</summary>
    public static UpdateInfo Parse(string json, string currentVersion, DateTimeOffset checkedUtc)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        var latest = ParseVersion(tag);
        var current = ParseVersion(currentVersion);
        var info = new UpdateInfo
        {
            CurrentVersion = currentVersion,
            LatestVersion = latest?.ToString(3),
            ReleasePage = root.TryGetProperty("html_url", out var page) && Uri.TryCreate(page.GetString(), UriKind.Absolute, out var pageUri) ? pageUri : null,
            CheckedUtc = checkedUtc,
        };

        if (latest is null)
        {
            return info with { Error = "The latest release has no version number." };
        }

        Uri? installer = null, checksums = null;
        string? installerName = null;
        long installerSize = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var u) && Uri.TryCreate(u.GetString(), UriKind.Absolute, out var parsed) ? parsed : null;
                if (name is null || !UpdateSource.IsTrustedDownload(url))
                {
                    continue;
                }

                if (name.EndsWith("-x64.msi", StringComparison.OrdinalIgnoreCase))
                {
                    installer = url;
                    installerName = name;
                    installerSize = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var size) ? size : 0;
                }
                else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    checksums = url;
                }
            }
        }

        return info with
        {
            IsUpdateAvailable = current is not null && latest > current && installer is not null && checksums is not null,
            InstallerUrl = installer,
            InstallerName = installerName,
            InstallerSize = installerSize,
            ChecksumsUrl = checksums,
        };
    }

    /// <summary>Accepts "1.2.3", "v1.2.3" and "1.2.3+build"; returns null otherwise.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim().TrimStart('v', 'V');
        var end = trimmed.IndexOfAny(['+', '-', ' ']);
        if (end >= 0)
        {
            trimmed = trimmed[..end];
        }

        return Version.TryParse(trimmed, out var version) && version.Major >= 0
            ? new Version(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build))
            : null;
    }

    /// <summary>Finds the SHA-256 for a file in a "hash  name" checksum list.</summary>
    public static string? FindChecksum(string checksums, string fileName)
    {
        ArgumentNullException.ThrowIfNull(checksums);
        foreach (var line in checksums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*').Equals(fileName, StringComparison.OrdinalIgnoreCase)
                && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit))
            {
                return parts[0].ToLower(CultureInfo.InvariantCulture);
            }
        }

        return null;
    }
}
