using WorkToUnlock.Core.Updates;

namespace WorkToUnlock.Core.Tests;

public sealed class UpdateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Download = "https://github.com/atcpybd/work-to-unlock/releases/download/v1.2.0/";

    private static string Release(string tag, string? msiUrl = Download + "WorkToUnlock-1.2.0-x64.msi", bool checksums = true) => $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://github.com/atcpybd/work-to-unlock/releases/tag/{{tag}}",
          "assets": [
            { "name": "WorkToUnlock-1.2.0-win-x64.zip", "size": 10, "browser_download_url": "{{Download}}WorkToUnlock-1.2.0-win-x64.zip" }
            {{(msiUrl is null ? "" : $$""", { "name": "WorkToUnlock-1.2.0-x64.msi", "size": 4096, "browser_download_url": "{{msiUrl}}" }""")}}
            {{(checksums ? $$""", { "name": "SHA256SUMS.txt", "size": 200, "browser_download_url": "{{Download}}SHA256SUMS.txt" }""" : "")}}
          ]
        }
        """;

    [Fact]
    public void A_newer_release_with_an_installer_is_an_update()
    {
        var info = ReleaseParser.Parse(Release("v1.2.0"), "1.1.0", Now);

        Assert.True(info.IsUpdateAvailable);
        Assert.Equal("1.2.0", info.LatestVersion);
        Assert.Equal("WorkToUnlock-1.2.0-x64.msi", info.InstallerName);
        Assert.Equal(4096, info.InstallerSize);
        Assert.NotNull(info.ChecksumsUrl);
        Assert.Equal(Now, info.CheckedUtc);
    }

    [Theory]
    [InlineData("v1.1.0")]
    [InlineData("v1.0.9")]
    public void The_same_or_an_older_release_is_not_an_update(string tag)
    {
        Assert.False(ReleaseParser.Parse(Release(tag), "1.1.0", Now).IsUpdateAvailable);
    }

    [Fact]
    public void Installers_from_anywhere_else_are_ignored()
    {
        var info = ReleaseParser.Parse(Release("v1.2.0", msiUrl: "https://example.com/atcpybd/work-to-unlock/releases/download/v1.2.0/WorkToUnlock-1.2.0-x64.msi"), "1.1.0", Now);

        Assert.False(info.IsUpdateAvailable);
        Assert.Null(info.InstallerUrl);
    }

    [Fact]
    public void A_release_without_checksums_is_not_offered()
    {
        Assert.False(ReleaseParser.Parse(Release("v1.2.0", checksums: false), "1.1.0", Now).IsUpdateAvailable);
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3+abc123", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("latest", null)]
    [InlineData("", null)]
    public void Versions_are_read_from_tags(string text, string? expected)
    {
        Assert.Equal(expected, ReleaseParser.ParseVersion(text)?.ToString(3));
    }

    [Fact]
    public void Checksums_are_found_by_file_name()
    {
        var hash = new string('a', 64);
        var list = $"{new string('b', 64)}  WorkToUnlock-1.2.0-win-x64.zip\r\n{hash.ToUpperInvariant()}  WorkToUnlock-1.2.0-x64.msi\r\n";

        Assert.Equal(hash, ReleaseParser.FindChecksum(list, "WorkToUnlock-1.2.0-x64.msi"));
        Assert.Null(ReleaseParser.FindChecksum(list, "other.msi"));
        Assert.Null(ReleaseParser.FindChecksum("short  WorkToUnlock-1.2.0-x64.msi", "WorkToUnlock-1.2.0-x64.msi"));
    }
}
