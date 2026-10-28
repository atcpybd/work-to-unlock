using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Rules;
using WorkToUnlock.Enforcement.Strict;

namespace WorkToUnlock.Enforcement.AppLocker;

/// <summary>
/// Records which AppLocker rules Work to Unlock created and what it changed, so that every change can be undone
/// exactly and unrelated rules are never touched. Persisted by the service under %ProgramData%\WorkToUnlock.
/// </summary>
public sealed record AppLockerOwnership
{
    /// <summary>Owned deny rule ids per user SID.</summary>
    public Dictionary<string, List<Guid>> DenyRulesByUser { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The owned "allow everything" baseline rule, added only when the Exe collection had no rules of its own.</summary>
    public Guid? BaselineAllowRuleId { get; init; }

    /// <summary>The Exe collection's EnforcementMode before Work to Unlock changed it ("Absent" if the element did not exist).</summary>
    public string? OriginalExeEnforcementMode { get; init; }

    public bool IsEmpty => DenyRulesByUser.Count == 0 && BaselineAllowRuleId is null && OriginalExeEnforcementMode is null;
}

public sealed record AppLockerMergeResult(string PolicyXml, AppLockerOwnership Ownership, IReadOnlyList<string> Warnings);

/// <summary>
/// Pure AppLocker policy manipulation. Takes the current local policy XML, adds or removes only the rules owned by
/// Work to Unlock (identified by id and by a marker in the description), and returns the full policy to write back.
/// Deny rules are scoped to the requesting user's SID and use path conditions, which AppLocker evaluates before allow rules.
/// </summary>
public static class AppLockerPolicyDocument
{
    public const string OwnershipMarker = "[Managed by Work to Unlock]";
    private const string ExeCollection = "Exe";
    private const string Absent = "Absent";
    private const string EveryoneSid = "S-1-1-0";

    /// <summary>Replaces the given user's owned deny rules with rules for <paramref name="rules"/> (empty removes them).</summary>
    public static AppLockerMergeResult SetUserRules(string? currentPolicyXml, AppLockerOwnership ownership, string userSid, IReadOnlyCollection<StrictRule> rules)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        var document = Parse(currentPolicyXml);
        var collection = GetOrCreateExeCollection(document, out var existedBefore);
        var warnings = new List<string>();
        var byUser = new Dictionary<string, List<Guid>>(ownership.DenyRulesByUser, StringComparer.OrdinalIgnoreCase);

        // 1. Remove this user's previous owned rules.
        var previous = byUser.GetValueOrDefault(userSid) ?? [];
        RemoveRules(collection, previous.ToHashSet());
        byUser.Remove(userSid);

        // 2. Add the new deny rules.
        var added = new List<Guid>();
        foreach (var rule in rules)
        {
            foreach (var path in DenyPaths(rule))
            {
                var id = DeterministicId(userSid, path);
                if (added.Contains(id))
                {
                    continue;
                }

                collection.Add(PathRule(id, $"Work to Unlock: block {rule.Name}", "Deny", userSid, path));
                added.Add(id);
            }
        }

        if (added.Count > 0)
        {
            byUser[userSid] = added;
        }

        var updated = ownership with { DenyRulesByUser = byUser };

        // 3. Baseline and enforcement mode: only when some user still has owned deny rules.
        if (byUser.Count > 0)
        {
            var originalMode = ownership.OriginalExeEnforcementMode
                ?? (existedBefore ? (string?)collection.Attribute("EnforcementMode") ?? "NotConfigured" : Absent);
            var hasForeignRules = collection.Elements().Any(e => !IsOwned(e, updated));

            Guid? baseline = ownership.BaselineAllowRuleId;
            if (!hasForeignRules && baseline is null)
            {
                // AppLocker denies everything not allowed once a collection is enforced. Without rules of the
                // administrator's own, allow everything so only Work to Unlock's deny rules take effect.
                baseline = DeterministicId("baseline", "*");
                collection.AddFirst(PathRule(baseline.Value, "Work to Unlock: allow all other programs", "Allow", EveryoneSid, "*"));
            }

            var mode = (string?)collection.Attribute("EnforcementMode") ?? "NotConfigured";
            if (mode == "AuditOnly")
            {
                warnings.Add("The Executable rule collection is in Audit only mode, so restricted apps are logged but not blocked.");
            }
            else if (mode != "Enabled")
            {
                collection.SetAttributeValue("EnforcementMode", "Enabled");
            }

            updated = updated with { BaselineAllowRuleId = baseline, OriginalExeEnforcementMode = originalMode };
        }
        else
        {
            updated = Restore(collection, updated);
            if (!existedBefore && collection.Parent is not null && !collection.HasElements)
            {
                collection.Remove();
            }
        }

        return new AppLockerMergeResult(Serialize(document), updated, warnings);
    }

    /// <summary>Removes every owned rule for every user and restores the original enforcement mode.</summary>
    public static AppLockerMergeResult RemoveAll(string? currentPolicyXml, AppLockerOwnership ownership)
    {
        var document = Parse(currentPolicyXml);
        var collection = GetOrCreateExeCollection(document, out var existedBefore);
        RemoveRules(collection, ownership.DenyRulesByUser.Values.SelectMany(v => v).ToHashSet());
        var restored = Restore(collection, ownership with { DenyRulesByUser = new(StringComparer.OrdinalIgnoreCase) });
        if (!existedBefore && collection.Parent is not null && !collection.HasElements)
        {
            collection.Remove();
        }
        return new AppLockerMergeResult(Serialize(document), restored, []);
    }

    /// <summary>Path conditions for one restricted app. Install-folder rules cover helpers and updaters.</summary>
    internal static IReadOnlyList<string> DenyPaths(StrictRule rule)
    {
        if (rule.Scope == MatchScope.InstallFolder && !string.IsNullOrWhiteSpace(rule.InstallRoot))
        {
            return [WindowsPath.Normalize(rule.InstallRoot) + @"\*"];
        }

        return [WindowsPath.Normalize(rule.ExecutablePath)];
    }

    private static AppLockerOwnership Restore(XElement collection, AppLockerOwnership ownership)
    {
        if (ownership.BaselineAllowRuleId is { } baseline)
        {
            RemoveRules(collection, new HashSet<Guid> { baseline });
        }

        // Remove any stray rule carrying our marker (for example after a lost ownership file).
        collection.Elements().Where(e => ((string?)e.Attribute("Description"))?.Contains(OwnershipMarker, StringComparison.Ordinal) == true).Remove();

        switch (ownership.OriginalExeEnforcementMode)
        {
            case Absent when !collection.HasElements:
                collection.Remove();
                break;
            case Absent:
                // An administrator added rules of their own meanwhile: leave their enforcement mode alone.
                break;
            case { } mode:
                collection.SetAttributeValue("EnforcementMode", mode);
                break;
        }

        return new AppLockerOwnership();
    }

    private static bool IsOwned(XElement rule, AppLockerOwnership ownership)
    {
        var id = Guid.TryParse((string?)rule.Attribute("Id"), out var g) ? g : Guid.Empty;
        return id == ownership.BaselineAllowRuleId
            || ownership.DenyRulesByUser.Values.Any(ids => ids.Contains(id))
            || ((string?)rule.Attribute("Description"))?.Contains(OwnershipMarker, StringComparison.Ordinal) == true;
    }

    private static void RemoveRules(XElement collection, IReadOnlySet<Guid> ids) =>
        collection.Elements().Where(e => Guid.TryParse((string?)e.Attribute("Id"), out var id) && ids.Contains(id)).Remove();

    private static XElement PathRule(Guid id, string name, string action, string sid, string path) =>
        new("FilePathRule",
            new XAttribute("Id", id.ToString()),
            new XAttribute("Name", name),
            new XAttribute("Description", OwnershipMarker),
            new XAttribute("UserOrGroupSid", sid),
            new XAttribute("Action", action),
            new XElement("Conditions", new XElement("FilePathCondition", new XAttribute("Path", path))));

    private static XElement GetOrCreateExeCollection(XDocument document, out bool existed)
    {
        var root = document.Root!;
        var collection = root.Elements("RuleCollection").FirstOrDefault(e => (string?)e.Attribute("Type") == ExeCollection);
        existed = collection is not null;
        if (collection is null)
        {
            collection = new XElement("RuleCollection", new XAttribute("Type", ExeCollection), new XAttribute("EnforcementMode", "NotConfigured"));
            root.Add(collection);
        }

        return collection;
    }

    private static XDocument Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return new XDocument(new XElement("AppLockerPolicy", new XAttribute("Version", "1")));
        }

        var document = XDocument.Parse(xml);
        if (document.Root?.Name.LocalName != "AppLockerPolicy")
        {
            throw new FormatException("The current policy is not an AppLocker policy document.");
        }

        return document;
    }

    private static string Serialize(XDocument document) => document.Root!.ToString(SaveOptions.None);

    /// <summary>Stable ids so re-applying the same rule is idempotent.</summary>
    internal static Guid DeterministicId(string scope, string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"WorkToUnlock|{scope}|{path.ToUpperInvariant()}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
