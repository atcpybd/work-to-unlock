namespace WorkToUnlock.Core.Domain;

public enum RuleType
{
    /// <summary>An approved application whose foreground use counts as work.</summary>
    Work = 1,

    /// <summary>A distracting application that is blocked outside reward periods.</summary>
    Restricted = 2,
}

/// <summary>How broadly a rule matches executables beyond its exact path.</summary>
public enum MatchScope
{
    /// <summary>Only the exact executable path matches.</summary>
    ExactPath = 0,

    /// <summary>
    /// The exact path, or any executable with the same file name whose Authenticode publisher matches.
    /// Survives updates that move the executable (for example versioned install folders).
    /// </summary>
    PublisherAndFileName = 1,

    /// <summary>
    /// Any executable inside the install folder that is signed by the same publisher or is a known
    /// related executable. Used for restricted apps with helper and updater processes.
    /// </summary>
    InstallFolder = 2,
}

/// <summary>
/// Identity information captured for an executable when a rule is created or revalidated.
/// Stored as JSON in ApplicationRules.IdentityMetadata.
/// </summary>
public sealed record ExecutableIdentity
{
    public string FileName { get; init; } = string.Empty;

    /// <summary>Subject common name of the Authenticode signer, when the file is signed.</summary>
    public string? Publisher { get; init; }

    /// <summary>True when the signature chain validated at capture time.</summary>
    public bool SignatureTrusted { get; init; }

    public string? ProductName { get; init; }

    public string? CompanyName { get; init; }

    public string? FileVersion { get; init; }

    /// <summary>Install folder used by <see cref="MatchScope.InstallFolder"/>.</summary>
    public string? InstallRoot { get; init; }

    public MatchScope Scope { get; init; } = MatchScope.ExactPath;

    /// <summary>File names of helper/updater executables discovered in the install folder.</summary>
    public IReadOnlyList<string> RelatedExecutables { get; init; } = [];

    public DateTimeOffset? LastValidatedUtc { get; init; }
}

/// <summary>A user-configured work or restricted application.</summary>
public sealed record ApplicationRule
{
    public long Id { get; init; }

    public required string Name { get; init; }

    public required string ExecutablePath { get; init; }

    public ExecutableIdentity Identity { get; init; } = new();

    public RuleType RuleType { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Work rules only: whether time in this app counts toward the work requirement. When false the
    /// time is still shown in statistics but does not earn rewards.
    /// </summary>
    public bool CountsTowardGoal { get; init; } = true;

    public DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; }
}
