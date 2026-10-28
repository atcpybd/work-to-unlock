using System.Xml.Linq;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Enforcement.AppLocker;
using WorkToUnlock.Enforcement.Strict;

namespace WorkToUnlock.Agent.Tests;

public sealed class AppLockerPolicyDocumentTests
{
    private const string User = "S-1-5-21-1-2-3-1001";
    private const string OtherUser = "S-1-5-21-1-2-3-1002";

    private static readonly StrictRule Steam = new("Steam", @"C:\Program Files (x86)\Steam\steam.exe", "Valve Corp.", @"C:\Program Files (x86)\Steam", MatchScope.InstallFolder);
    private static readonly StrictRule Game = new("Game", @"D:\Games\game.exe", null, null, MatchScope.ExactPath);

    private const string AdminPolicy = """
        <AppLockerPolicy Version="1">
          <RuleCollection Type="Exe" EnforcementMode="Enabled">
            <FilePathRule Id="921cc481-6e17-4653-8f75-050b80acca20" Name="(Default Rule) All files located in the Program Files folder" Description="" UserOrGroupSid="S-1-1-0" Action="Allow">
              <Conditions><FilePathCondition Path="%PROGRAMFILES%\*" /></Conditions>
            </FilePathRule>
          </RuleCollection>
          <RuleCollection Type="Msi" EnforcementMode="AuditOnly" />
        </AppLockerPolicy>
        """;

    private static XElement Exe(string xml) => XDocument.Parse(xml).Root!.Elements("RuleCollection").Single(e => (string?)e.Attribute("Type") == "Exe");

    [Fact]
    public void Empty_policy_gets_user_scoped_denies_a_baseline_allow_and_enforcement()
    {
        var result = AppLockerPolicyDocument.SetUserRules(null, new AppLockerOwnership(), User, [Steam, Game]);

        var exe = Exe(result.PolicyXml);
        Assert.Equal("Enabled", (string?)exe.Attribute("EnforcementMode"));
        var denies = exe.Elements("FilePathRule").Where(r => (string?)r.Attribute("Action") == "Deny").ToList();
        Assert.Equal(2, denies.Count);
        Assert.All(denies, d => Assert.Equal(User, (string?)d.Attribute("UserOrGroupSid")));
        Assert.Contains(denies, d => d.Descendants("FilePathCondition").Single().Attribute("Path")!.Value == @"C:\Program Files (x86)\Steam\*");
        var allow = Assert.Single(exe.Elements("FilePathRule"), r => (string?)r.Attribute("Action") == "Allow");
        Assert.Equal("*", allow.Descendants("FilePathCondition").Single().Attribute("Path")!.Value);
        Assert.Equal("Absent", result.Ownership.OriginalExeEnforcementMode);
        Assert.NotNull(result.Ownership.BaselineAllowRuleId);
    }

    [Fact]
    public void Existing_administrator_rules_are_preserved_and_no_baseline_is_added()
    {
        var result = AppLockerPolicyDocument.SetUserRules(AdminPolicy, new AppLockerOwnership(), User, [Game]);

        var document = XDocument.Parse(result.PolicyXml);
        Assert.Contains(document.Descendants("FilePathRule"), r => (string?)r.Attribute("Id") == "921cc481-6e17-4653-8f75-050b80acca20");
        Assert.Equal("AuditOnly", (string?)document.Root!.Elements("RuleCollection").Single(e => (string?)e.Attribute("Type") == "Msi").Attribute("EnforcementMode"));
        Assert.Null(result.Ownership.BaselineAllowRuleId);
        Assert.Equal("Enabled", result.Ownership.OriginalExeEnforcementMode);
    }

    [Fact]
    public void Removing_rules_restores_the_original_policy_exactly()
    {
        var applied = AppLockerPolicyDocument.SetUserRules(AdminPolicy, new AppLockerOwnership(), User, [Game, Steam]);

        var removed = AppLockerPolicyDocument.SetUserRules(applied.PolicyXml, applied.Ownership, User, []);

        Assert.True(XNode.DeepEquals(XDocument.Parse(AdminPolicy).Root, XDocument.Parse(removed.PolicyXml).Root));
        Assert.True(removed.Ownership.IsEmpty);
    }

    [Fact]
    public void Removing_from_an_empty_policy_removes_everything_that_was_added()
    {
        var applied = AppLockerPolicyDocument.SetUserRules(null, new AppLockerOwnership(), User, [Steam]);

        var removed = AppLockerPolicyDocument.RemoveAll(applied.PolicyXml, applied.Ownership);

        Assert.Empty(XDocument.Parse(removed.PolicyXml).Root!.Elements("RuleCollection"));
    }

    [Fact]
    public void Users_are_managed_independently()
    {
        var first = AppLockerPolicyDocument.SetUserRules(null, new AppLockerOwnership(), User, [Steam]);
        var second = AppLockerPolicyDocument.SetUserRules(first.PolicyXml, first.Ownership, OtherUser, [Game]);

        var firstRemoved = AppLockerPolicyDocument.SetUserRules(second.PolicyXml, second.Ownership, User, []);

        var exe = Exe(firstRemoved.PolicyXml);
        Assert.Equal("Enabled", (string?)exe.Attribute("EnforcementMode"));
        Assert.Single(exe.Elements("FilePathRule"), r => (string?)r.Attribute("Action") == "Deny");
        Assert.Equal(OtherUser, (string?)exe.Elements("FilePathRule").Single(r => (string?)r.Attribute("Action") == "Deny").Attribute("UserOrGroupSid"));
        Assert.NotNull(firstRemoved.Ownership.BaselineAllowRuleId);
    }

    [Fact]
    public void Reapplying_the_same_rules_is_idempotent()
    {
        var first = AppLockerPolicyDocument.SetUserRules(null, new AppLockerOwnership(), User, [Steam, Game]);
        var again = AppLockerPolicyDocument.SetUserRules(first.PolicyXml, first.Ownership, User, [Steam, Game]);

        Assert.True(XNode.DeepEquals(XDocument.Parse(first.PolicyXml).Root, XDocument.Parse(again.PolicyXml).Root));
    }

    [Fact]
    public void Owned_rules_are_found_by_marker_when_the_ownership_file_was_lost()
    {
        var applied = AppLockerPolicyDocument.SetUserRules(AdminPolicy, new AppLockerOwnership(), User, [Game]);

        var removed = AppLockerPolicyDocument.RemoveAll(applied.PolicyXml, new AppLockerOwnership());

        Assert.DoesNotContain(XDocument.Parse(removed.PolicyXml).Descendants("FilePathRule"),
            r => ((string?)r.Attribute("Description"))!.Contains(AppLockerPolicyDocument.OwnershipMarker, StringComparison.Ordinal));
    }

    [Fact]
    public void Audit_only_collections_are_left_in_audit_mode_with_a_warning()
    {
        const string audit = """<AppLockerPolicy Version="1"><RuleCollection Type="Exe" EnforcementMode="AuditOnly" /></AppLockerPolicy>""";

        var result = AppLockerPolicyDocument.SetUserRules(audit, new AppLockerOwnership(), User, [Game]);

        Assert.Equal("AuditOnly", (string?)Exe(result.PolicyXml).Attribute("EnforcementMode"));
        Assert.Single(result.Warnings);
    }
}
