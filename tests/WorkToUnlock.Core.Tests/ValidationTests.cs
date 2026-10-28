using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Rules;

namespace WorkToUnlock.Core.Tests;

public sealed class SettingsValidatorTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        Assert.Empty(SettingsValidator.Validate(AppSettings.Default));
        Assert.Equal(60, AppSettings.Default.WorkRequirementMinutes);
        Assert.Equal(20, AppSettings.Default.RewardDurationMinutes);
        Assert.Equal(60, AppSettings.Default.IdleThresholdSeconds);
    }

    [Theory]
    [InlineData(0, 20, 60, 15, 90, nameof(AppSettings.WorkRequirementMinutes))]
    [InlineData(721, 20, 60, 15, 90, nameof(AppSettings.WorkRequirementMinutes))]
    [InlineData(60, 0, 60, 15, 90, nameof(AppSettings.RewardDurationMinutes))]
    [InlineData(60, 241, 60, 15, 90, nameof(AppSettings.RewardDurationMinutes))]
    [InlineData(60, 20, 5, 15, 90, nameof(AppSettings.IdleThresholdSeconds))]
    [InlineData(60, 20, 60, 1, 90, nameof(AppSettings.GracefulCloseTimeoutSeconds))]
    [InlineData(60, 20, 60, 15, 1, nameof(AppSettings.DataRetentionDays))]
    public void Out_of_range_values_are_rejected(int work, int reward, int idle, int grace, int retention, string property)
    {
        var settings = new AppSettings
        {
            WorkRequirementMinutes = work,
            RewardDurationMinutes = reward,
            IdleThresholdSeconds = idle,
            GracefulCloseTimeoutSeconds = grace,
            DataRetentionDays = retention,
        };

        var error = Assert.Single(SettingsValidator.Validate(settings));
        Assert.Equal(property, error.Property);
    }

    [Fact]
    public void Unknown_enum_values_are_rejected()
    {
        var settings = new AppSettings { EnforcementMode = (EnforcementMode)9, Theme = (ThemePreference)9 };

        Assert.Equal(2, SettingsValidator.Validate(settings).Count);
    }
}

public sealed class RuleSafetyValidatorTests
{
    [Fact]
    public void A_normal_restricted_app_is_allowed()
    {
        var rule = TestRules.Restricted(0, "Steam", TestRules.SteamPath, new ExecutableIdentity
        {
            FileName = "steam.exe",
            InstallRoot = @"C:\Program Files (x86)\Steam",
            Scope = MatchScope.InstallFolder,
        });

        Assert.Empty(RuleSafetyValidator.Validate(rule, TestRules.Folders));
    }

    [Theory]
    [InlineData(@"C:\Windows\explorer.exe")]
    [InlineData(@"C:\Windows\System32\notepad.exe")]
    [InlineData(@"D:\Portable\Taskmgr.exe")]
    [InlineData(@"C:\Program Files\Work to Unlock\WorkToUnlock.Agent.exe")]
    [InlineData(@"C:\Tools\msiexec.exe")]
    public void System_and_self_executables_cannot_be_restricted(string path)
    {
        var rule = TestRules.Restricted(0, "Bad", path);

        Assert.NotEmpty(RuleSafetyValidator.Validate(rule, TestRules.Folders));
        Assert.False(RuleSafetyValidator.IsSafeToEnforce(path, TestRules.Folders));
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Program Files (x86)")]
    [InlineData(@"C:\Users\dev")]
    [InlineData(@"C:\Users\dev\AppData\Local")]
    [InlineData(@"C:\Users\dev\AppData\Local\Programs")]
    public void Overly_broad_install_folders_are_rejected(string root)
    {
        var path = WindowsPath.Normalize(root) + @"\Sub\app.exe";
        var rule = TestRules.Restricted(0, "Broad", path, new ExecutableIdentity
        {
            FileName = "app.exe",
            InstallRoot = root,
            Scope = MatchScope.InstallFolder,
        });

        Assert.Contains(RuleSafetyValidator.Validate(rule, TestRules.Folders), e => e.Property == nameof(ExecutableIdentity.InstallRoot));
    }

    [Fact]
    public void Install_folder_on_a_short_custom_path_is_allowed()
    {
        var rule = TestRules.Restricted(0, "Steam", @"D:\Steam\steam.exe", new ExecutableIdentity
        {
            FileName = "steam.exe",
            InstallRoot = @"D:\Steam",
            Scope = MatchScope.InstallFolder,
        });

        Assert.Empty(RuleSafetyValidator.Validate(rule, TestRules.Folders));
    }

    [Theory]
    [InlineData("relative\\app.exe")]
    [InlineData(@"C:\Tools\script.bat")]
    public void Paths_must_be_full_executable_paths(string path)
    {
        Assert.NotEmpty(RuleSafetyValidator.Validate(TestRules.Work(0, "x", path), TestRules.Folders));
    }

    [Fact]
    public void Work_apps_may_live_in_the_windows_folder()
    {
        Assert.Empty(RuleSafetyValidator.Validate(TestRules.Work(0, "Notepad", @"C:\Windows\System32\notepad.exe"), TestRules.Folders));
    }
}

public sealed class WindowsPathTests
{
    [Theory]
    [InlineData(@"C:/Program Files//App/app.exe", @"C:\Program Files\App\app.exe")]
    [InlineData("\"C:\\App\\app.exe\"", @"C:\App\app.exe")]
    [InlineData(@"\\?\C:\App\app.exe", @"C:\App\app.exe")]
    [InlineData(@"\\server\share\app.exe", @"\\server\share\app.exe")]
    [InlineData(@"C:\App\", @"C:\App")]
    public void Normalize(string input, string expected) => Assert.Equal(expected, WindowsPath.Normalize(input));

    [Theory]
    [InlineData(@"C:\Steam\bin\x.exe", @"C:\Steam", true)]
    [InlineData(@"C:\SteamTools\x.exe", @"C:\Steam", false)]
    [InlineData(@"C:\Steam", @"C:\Steam", false)]
    [InlineData(@"c:\steam\X.EXE", @"C:\Steam\", true)]
    public void IsUnder(string path, string folder, bool expected) => Assert.Equal(expected, WindowsPath.IsUnder(path, folder));
}
