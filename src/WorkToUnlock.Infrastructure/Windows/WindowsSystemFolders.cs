using WorkToUnlock.Core.Rules;

namespace WorkToUnlock.Infrastructure.Windows;

public static class WindowsSystemFolders
{
    /// <summary>Builds the folder set used by the rule safety checks.</summary>
    /// <param name="protectedInstallDirectories">Folders containing Work to Unlock itself.</param>
    public static SystemFolders Create(params string[] protectedInstallDirectories)
    {
        var programFiles = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            }
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return new SystemFolders(
            string.IsNullOrEmpty(windows) ? @"C:\Windows" : windows,
            programFiles,
            protectedInstallDirectories.Where(d => !string.IsNullOrWhiteSpace(d)).Select(WindowsPath.Normalize).ToList());
    }
}
