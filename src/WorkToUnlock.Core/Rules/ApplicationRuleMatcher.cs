using WorkToUnlock.Core.Domain;

namespace WorkToUnlock.Core.Rules;

/// <summary>Signature information for an executable on disk.</summary>
public sealed record ExecutableSignature(string? Publisher, bool Trusted);

/// <summary>Reads (and caches) the Authenticode publisher of executables.</summary>
public interface IExecutableSignatureReader
{
    /// <summary>Returns the signature of the file, or null when the file cannot be read.</summary>
    ExecutableSignature? GetSignature(string executablePath);
}

public interface IApplicationRuleMatcher
{
    /// <summary>
    /// Finds the enabled rule of the given type that matches the executable, or null.
    /// Exact path matches win over publisher or folder matches.
    /// </summary>
    ApplicationRule? Match(string executablePath, IReadOnlyList<ApplicationRule> rules, RuleType ruleType);
}

/// <summary>
/// Identifies applications by path, Authenticode publisher and install folder rather than by process name,
/// so a renamed or look-alike executable does not match an approved or restricted application.
/// </summary>
public sealed class ApplicationRuleMatcher(IExecutableSignatureReader signatureReader) : IApplicationRuleMatcher
{
    public ApplicationRule? Match(string executablePath, IReadOnlyList<ApplicationRule> rules, RuleType ruleType)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (string.IsNullOrWhiteSpace(executablePath) || rules.Count == 0)
        {
            return null;
        }

        var normalized = WindowsPath.Normalize(executablePath);
        List<ApplicationRule>? candidates = null;

        foreach (var rule in rules)
        {
            if (!rule.Enabled || rule.RuleType != ruleType)
            {
                continue;
            }

            if (WindowsPath.PathEquals(rule.ExecutablePath, normalized))
            {
                return rule;
            }

            if (CouldMatchBeyondPath(rule, normalized))
            {
                (candidates ??= []).Add(rule);
            }
        }

        if (candidates is null)
        {
            return null;
        }

        // Only now pay for reading the signature (the reader caches per file version).
        var signature = signatureReader.GetSignature(normalized);
        foreach (var rule in candidates)
        {
            if (MatchesBeyondPath(rule, normalized, signature))
            {
                return rule;
            }
        }

        return null;
    }

    private static bool CouldMatchBeyondPath(ApplicationRule rule, string path)
    {
        var identity = rule.Identity;
        var fileName = WindowsPath.GetFileName(path);

        return identity.Scope switch
        {
            MatchScope.PublisherAndFileName =>
                identity.Publisher is not null && FileNameMatches(rule, fileName),
            MatchScope.InstallFolder =>
                identity.InstallRoot is not null && WindowsPath.IsUnder(path, identity.InstallRoot),
            _ => false,
        };
    }

    private static bool MatchesBeyondPath(ApplicationRule rule, string path, ExecutableSignature? signature)
    {
        var identity = rule.Identity;
        var fileName = WindowsPath.GetFileName(path);
        var publisherMatches = identity.Publisher is not null
            && signature is { Trusted: true, Publisher: not null }
            && string.Equals(signature.Publisher, identity.Publisher, StringComparison.OrdinalIgnoreCase);

        return identity.Scope switch
        {
            MatchScope.PublisherAndFileName => publisherMatches && FileNameMatches(rule, fileName),

            // Inside the install folder: same trusted publisher, or (for unsigned apps) a known file name.
            MatchScope.InstallFolder => identity.Publisher is not null
                ? publisherMatches
                : FileNameMatches(rule, fileName) || IsRelated(identity, fileName),

            _ => false,
        };
    }

    private static bool FileNameMatches(ApplicationRule rule, string fileName) =>
        string.Equals(WindowsPath.GetFileName(rule.ExecutablePath), fileName, StringComparison.OrdinalIgnoreCase);

    private static bool IsRelated(ExecutableIdentity identity, string fileName) =>
        identity.RelatedExecutables.Any(r => string.Equals(r, fileName, StringComparison.OrdinalIgnoreCase));
}
