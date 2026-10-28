using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using WorkToUnlock.Core.Updates;

namespace WorkToUnlock.Infrastructure.Updates;

public interface IReleaseClient
{
    /// <summary>The latest published release as GitHub JSON, or null when nothing is published yet.</summary>
    Task<string?> GetLatestReleaseJsonAsync(CancellationToken cancellationToken);
}

/// <summary>Reads releases of <see cref="UpdateSource.Repository"/>. This is the only network access the app makes.</summary>
public sealed class GitHubReleaseClient : IReleaseClient
{
    private static readonly HttpClient Http = CreateClient();

    public async Task<string?> GetLatestReleaseJsonAsync(CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(UpdateSource.LatestReleaseApi, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var version = typeof(GitHubReleaseClient).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WorkToUnlock", version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}

/// <summary>Downloads a release installer and checks it against the release's SHA256SUMS.txt before it may run.</summary>
public static class UpdateDownloader
{
    private static readonly HttpClient Http = GitHubReleaseClient.CreateClient();

    /// <returns>The path of the verified installer.</returns>
    /// <exception cref="InvalidOperationException">The update is not trusted or the download does not match its checksum.</exception>
    public static async Task<string> DownloadVerifiedAsync(UpdateInfo update, string directory, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!update.IsUpdateAvailable || update.InstallerName is null
            || !UpdateSource.IsTrustedDownload(update.InstallerUrl) || !UpdateSource.IsTrustedDownload(update.ChecksumsUrl)
            || update.InstallerName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("This update can't be installed automatically. Download it from the releases page instead.");
        }

        var checksums = await Http.GetStringAsync(update.ChecksumsUrl, cancellationToken).ConfigureAwait(false);
        var expected = ReleaseParser.FindChecksum(checksums, update.InstallerName)
            ?? throw new InvalidOperationException("The release doesn't list a checksum for its installer.");

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, update.InstallerName);
        using (var response = await Http.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? update.InstallerSize;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                if (total > 0)
                {
                    progress?.Report(Math.Min(1, (double)received / total));
                }
            }
        }

        string actual;
        await using (var file = File.OpenRead(path))
        {
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
        }

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidOperationException("The downloaded installer doesn't match the release checksum, so it was deleted.");
        }

        return path;
    }
}
