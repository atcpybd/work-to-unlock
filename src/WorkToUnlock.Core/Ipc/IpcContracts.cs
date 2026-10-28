using System.Text.Json;
using System.Text.Json.Serialization;
using WorkToUnlock.Core.Activity;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Enforcement;
using WorkToUnlock.Core.Persistence;

namespace WorkToUnlock.Core.Ipc;

/// <summary>Wire envelope: one JSON object per line.</summary>
public sealed class IpcMessage
{
    public const string RequestType = "request";
    public const string ResponseType = "response";
    public const string EventType = "event";

    public string Type { get; set; } = RequestType;

    public long Id { get; set; }

    /// <summary>Method name for requests, event name for events.</summary>
    public string? Name { get; set; }

    public JsonElement? Payload { get; set; }

    public string? Error { get; set; }
}

/// <summary>Methods served by the per-user agent.</summary>
public static class AgentMethods
{
    public const string Ping = "ping";
    public const string GetStatus = "getStatus";
    public const string ActivateReward = "activateReward";
    public const string Subscribe = "subscribe";
    public const string ListRules = "listRules";
    public const string SaveRule = "saveRule";
    public const string DeleteRule = "deleteRule";
    public const string RevalidateRule = "revalidateRule";
    public const string InspectExecutable = "inspectExecutable";
    public const string ListInstalledApps = "listInstalledApps";
    public const string GetSettings = "getSettings";
    public const string UpdateSettings = "updateSettings";
    public const string SetEnforcementEnabled = "setEnforcementEnabled";
    public const string ResetProgress = "resetProgress";
    public const string GetStatistics = "getStatistics";
    public const string GetRuleTimes = "getRuleTimes";
    public const string ExportData = "exportData";
    public const string GetAuditEvents = "getAuditEvents";
    public const string ShowDashboard = "showDashboard";
    public const string ExitDashboards = "exitDashboards";
    public const string GetDailyProgress = "getDailyProgress";

    public const string GetTamperReport = "getTamperReport";

    public const string GetAchievements = "getAchievements";

    public const string EndBreakEarly = "endBreakEarly";
}

/// <summary>Events pushed by the agent to subscribed dashboards.</summary>
public static class AgentEvents
{
    public const string Status = "status";
    public const string RulesChanged = "rulesChanged";
    public const string SettingsChanged = "settingsChanged";
    public const string ShowDashboard = "showDashboard";
    public const string ExitDashboard = "exitDashboard";
}

public static class IpcJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static T? FromElement<T>(JsonElement? element) =>
        element is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } e
            ? e.Deserialize<T>(Options)
            : default;
}

// ----- DTOs -----

public sealed record AgentStatus
{
    public required RewardStatus Reward { get; init; }

    public required TrackingSnapshot Tracking { get; init; }

    public required EnforcementHealth Enforcement { get; init; }

    public bool EnforcementEnabled { get; init; }

    public bool PersistenceHealthy { get; init; } = true;

    public bool NotificationsEnabled { get; init; } = true;

    public string AgentVersion { get; init; } = string.Empty;

    public DateTimeOffset AgentStartedUtc { get; init; }

    public DateTimeOffset GeneratedUtc { get; init; }
}

public sealed record OperationResult
{
    public bool Succeeded { get; init; }

    public string? Message { get; init; }

    public IReadOnlyList<ValidationError> Errors { get; init; } = [];

    public static OperationResult Ok(string? message = null) => new() { Succeeded = true, Message = message };

    public static OperationResult Fail(string message, IReadOnlyList<ValidationError>? errors = null) =>
        new() { Succeeded = false, Message = message, Errors = errors ?? [] };
}

public sealed record SaveRuleResult
{
    public bool Succeeded { get; init; }

    public ApplicationRule? Rule { get; init; }

    public IReadOnlyList<ValidationError> Errors { get; init; } = [];
}

public sealed record IdRequest(long Id);

public sealed record PathRequest(string Path, RuleType RuleType);

public sealed record ResetProgressRequest(string Reason);

public sealed record SetEnforcementRequest(bool Enabled, string Reason);

public sealed record ShowDashboardRequest(string? Page);

public sealed record CountRequest(int Count);

/// <summary>What the agent learned about an executable chosen by the user.</summary>
public sealed record ExecutableInspection
{
    public bool Exists { get; init; }

    public string ExecutablePath { get; init; } = string.Empty;

    public string SuggestedName { get; init; } = string.Empty;

    public ExecutableIdentity Identity { get; init; } = new();

    public IReadOnlyList<ValidationError> Problems { get; init; } = [];
}

public sealed record InstalledApplication(string Name, string ExecutablePath, string? Publisher, string Source);

public sealed record RuleTime(long RuleId, TimeSpan Today, TimeSpan ThisWeek);

public sealed record ExportResult(string Json);

public sealed record AuditEventList(IReadOnlyList<AuditEvent> Events);
