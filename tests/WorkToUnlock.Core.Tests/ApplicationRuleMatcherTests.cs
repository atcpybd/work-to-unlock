using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Rules;

namespace WorkToUnlock.Core.Tests;

public sealed class ApplicationRuleMatcherTests
{
    private readonly FakeSignatureReader _signatures = new();
    private readonly ApplicationRuleMatcher _matcher;

    public ApplicationRuleMatcherTests() => _matcher = new ApplicationRuleMatcher(_signatures);

    [Fact]
    public void Exact_path_matches_case_insensitively_without_reading_signatures()
    {
        var rule = TestRules.Work(1, "Code", TestRules.VsCodePath);

        var match = _matcher.Match(TestRules.VsCodePath.ToUpperInvariant(), [rule], RuleType.Work);

        Assert.Same(rule, match);
        Assert.Equal(0, _signatures.Calls);
    }

    [Fact]
    public void Same_file_name_in_another_folder_does_not_match_an_exact_rule()
    {
        var rule = TestRules.Work(1, "Code", TestRules.VsCodePath);

        Assert.Null(_matcher.Match(@"C:\Temp\Code.exe", [rule], RuleType.Work));
    }

    [Fact]
    public void Rule_type_and_enabled_state_are_respected()
    {
        var work = TestRules.Work(1, "Code", TestRules.VsCodePath);
        var disabled = TestRules.Restricted(2, "Steam", TestRules.SteamPath) with { Enabled = false };

        Assert.Null(_matcher.Match(TestRules.VsCodePath, [work], RuleType.Restricted));
        Assert.Null(_matcher.Match(TestRules.SteamPath, [disabled], RuleType.Restricted));
    }

    [Fact]
    public void Publisher_scope_survives_an_update_that_moves_the_executable()
    {
        var rule = TestRules.Restricted(1, "Discord", TestRules.DiscordPath, new ExecutableIdentity
        {
            FileName = "Discord.exe",
            Publisher = "Discord Inc.",
            Scope = MatchScope.PublisherAndFileName,
        });
        const string updated = @"C:\Users\dev\AppData\Local\Discord\app-1.0.9100\Discord.exe";
        _signatures.Sign(updated, "Discord Inc.");

        Assert.Same(rule, _matcher.Match(updated, [rule], RuleType.Restricted));
    }

    [Fact]
    public void Publisher_scope_rejects_untrusted_or_different_publishers()
    {
        var rule = TestRules.Restricted(1, "Discord", TestRules.DiscordPath, new ExecutableIdentity
        {
            FileName = "Discord.exe",
            Publisher = "Discord Inc.",
            Scope = MatchScope.PublisherAndFileName,
        });
        _signatures.Sign(@"C:\Fake\Discord.exe", "Discord Inc.", trusted: false);
        _signatures.Sign(@"C:\Other\Discord.exe", "Someone Else");

        Assert.Null(_matcher.Match(@"C:\Fake\Discord.exe", [rule], RuleType.Restricted));
        Assert.Null(_matcher.Match(@"C:\Other\Discord.exe", [rule], RuleType.Restricted));
    }

    [Fact]
    public void Install_folder_scope_matches_signed_helper_processes()
    {
        var rule = TestRules.Restricted(1, "Steam", TestRules.SteamPath, new ExecutableIdentity
        {
            FileName = "steam.exe",
            Publisher = "Valve Corp.",
            InstallRoot = @"C:\Program Files (x86)\Steam",
            Scope = MatchScope.InstallFolder,
        });
        _signatures.Sign(TestRules.SteamHelperPath, "Valve Corp.");
        _signatures.Sign(@"C:\Program Files (x86)\Steam\steamapps\common\Indie\game.exe", "Indie Studio");

        Assert.Same(rule, _matcher.Match(TestRules.SteamHelperPath, [rule], RuleType.Restricted));
        Assert.Null(_matcher.Match(@"C:\Program Files (x86)\Steam\steamapps\common\Indie\game.exe", [rule], RuleType.Restricted));
        Assert.Null(_matcher.Match(@"C:\Program Files (x86)\SteamTools\steam.exe", [rule], RuleType.Restricted));
    }

    [Fact]
    public void Install_folder_scope_for_unsigned_apps_matches_known_file_names_only()
    {
        var rule = TestRules.Restricted(1, "Game", @"D:\Games\Thing\thing.exe", new ExecutableIdentity
        {
            FileName = "thing.exe",
            InstallRoot = @"D:\Games\Thing",
            Scope = MatchScope.InstallFolder,
            RelatedExecutables = ["thing-helper.exe"],
        });

        Assert.Same(rule, _matcher.Match(@"D:\Games\Thing\bin\thing-helper.exe", [rule], RuleType.Restricted));
        Assert.Same(rule, _matcher.Match(@"D:\Games\Thing\old\thing.exe", [rule], RuleType.Restricted));
        Assert.Null(_matcher.Match(@"D:\Games\Thing\tools\notepad.exe", [rule], RuleType.Restricted));
    }

    [Fact]
    public void Exact_match_wins_over_broader_rules()
    {
        var broad = TestRules.Restricted(1, "Folder", @"D:\Apps\Suite\main.exe", new ExecutableIdentity
        {
            FileName = "main.exe",
            InstallRoot = @"D:\Apps\Suite",
            Scope = MatchScope.InstallFolder,
            RelatedExecutables = ["tool.exe"],
        });
        var exact = TestRules.Restricted(2, "Tool", @"D:\Apps\Suite\tool.exe");

        Assert.Same(exact, _matcher.Match(@"D:\Apps\Suite\tool.exe", [broad, exact], RuleType.Restricted));
    }
}
