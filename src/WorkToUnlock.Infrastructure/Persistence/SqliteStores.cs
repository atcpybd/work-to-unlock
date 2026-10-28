using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Persistence;

namespace WorkToUnlock.Infrastructure.Persistence;

internal static class Db
{
    public static readonly JsonSerializerOptions Json = CreateJson();

    public static long ToUnix(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    public static DateTimeOffset FromUnix(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);

    public static object ToDb(DateTimeOffset? value) => value is { } v ? ToUnix(v) : DBNull.Value;

    public static object ToDb(long? value) => value is { } v ? v : DBNull.Value;

    public static object ToDb(string? value) => value is null ? DBNull.Value : value;

    public static DateTimeOffset? GetNullableTime(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : FromUnix(reader.GetInt64(ordinal));

    public static long? GetNullableInt64(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    public static string? GetNullableString(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static async Task InsertAuditAsync(SqliteConnection connection, SqliteTransaction? transaction, DateTimeOffset timestamp, string eventType, string description, string result, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO AuditEvents (TimestampUtc, EventType, Description, Result) VALUES ($t, $type, $description, $result);";
        command.Parameters.AddWithValue("$t", ToUnix(timestamp));
        command.Parameters.AddWithValue("$type", eventType);
        command.Parameters.AddWithValue("$description", description);
        command.Parameters.AddWithValue("$result", result);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions { WriteIndented = false };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public sealed class SqliteRewardStateStore(SqliteDatabase database) : IRewardStateStore
{
    public async Task<RewardStateRecord?> LoadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT State, AccumulatedWorkMilliseconds, RewardEarnedUtc, RewardActivatedUtc, RewardExpiresUtc,
                   RewardDurationMilliseconds, DeadlineMonotonicMilliseconds, BootId, Version
            FROM RewardState WHERE Id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new RewardStateRecord
        {
            State = (RewardState)reader.GetInt32(0),
            AccumulatedWorkMilliseconds = reader.GetInt64(1),
            RewardEarnedUtc = reader.GetNullableTime(2),
            RewardActivatedUtc = reader.GetNullableTime(3),
            RewardExpiresUtc = reader.GetNullableTime(4),
            RewardDurationMilliseconds = reader.GetNullableInt64(5),
            DeadlineMonotonicMilliseconds = reader.GetNullableInt64(6),
            BootId = reader.GetNullableString(7),
            Version = reader.GetInt64(8),
        };
    }

    public async Task<RewardStateRecord> SaveAsync(RewardStateRecord record, long expectedVersion, RewardTransition? transition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.State == RewardState.Expired)
        {
            throw new ArgumentException("EXPIRED is transient and is never persisted.", nameof(record));
        }

        var saved = record with { Version = expectedVersion + 1 };
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                INSERT INTO RewardState (Id, State, AccumulatedWorkMilliseconds, RewardEarnedUtc, RewardActivatedUtc, RewardExpiresUtc,
                                         RewardDurationMilliseconds, DeadlineMonotonicMilliseconds, BootId, Version)
                SELECT 1, $state, $acc, $earned, $activated, $expires, $duration, $deadline, $boot, $expected
                WHERE $expected = 0 AND NOT EXISTS (SELECT 1 FROM RewardState WHERE Id = 1)
                ON CONFLICT (Id) DO NOTHING;
                UPDATE RewardState
                SET State = $state, AccumulatedWorkMilliseconds = $acc, RewardEarnedUtc = $earned, RewardActivatedUtc = $activated,
                    RewardExpiresUtc = $expires, RewardDurationMilliseconds = $duration, DeadlineMonotonicMilliseconds = $deadline,
                    BootId = $boot, Version = $new
                WHERE Id = 1 AND Version = $expected;
                SELECT changes();
                """;
            update.Parameters.AddWithValue("$state", (int)saved.State);
            update.Parameters.AddWithValue("$acc", saved.AccumulatedWorkMilliseconds);
            update.Parameters.AddWithValue("$earned", Db.ToDb(saved.RewardEarnedUtc));
            update.Parameters.AddWithValue("$activated", Db.ToDb(saved.RewardActivatedUtc));
            update.Parameters.AddWithValue("$expires", Db.ToDb(saved.RewardExpiresUtc));
            update.Parameters.AddWithValue("$duration", Db.ToDb(saved.RewardDurationMilliseconds));
            update.Parameters.AddWithValue("$deadline", Db.ToDb(saved.DeadlineMonotonicMilliseconds));
            update.Parameters.AddWithValue("$boot", Db.ToDb(saved.BootId));
            update.Parameters.AddWithValue("$new", saved.Version);
            update.Parameters.AddWithValue("$expected", expectedVersion);
            var changed = Convert.ToInt64(await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            if (changed != 1)
            {
                throw new ConcurrencyConflictException($"Reward state changed concurrently (expected version {expectedVersion}).");
            }
        }

        if (transition is not null)
        {
            await RecordHistoryAsync(connection, transaction, saved, transition, cancellationToken).ConfigureAwait(false);
            await Db.InsertAuditAsync(connection, transaction, transition.TimestampUtc, "Reward" + transition.Kind, transition.Description, "Success", cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return saved;
    }

    private static async Task RecordHistoryAsync(SqliteConnection connection, SqliteTransaction transaction, RewardStateRecord record, RewardTransition transition, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$t", Db.ToUnix(transition.TimestampUtc));

        switch (transition.Kind)
        {
            case RewardTransitionKind.Earned:
                command.CommandText = "INSERT INTO RewardHistory (EarnedUtc) VALUES ($t);";
                break;

            case RewardTransitionKind.Activated:
                command.CommandText = """
                    UPDATE RewardHistory SET ActivatedUtc = $t, ExpiresUtc = $expires, DurationMilliseconds = $duration
                    WHERE Id = (SELECT MAX(Id) FROM RewardHistory WHERE ActivatedUtc IS NULL AND EndedUtc IS NULL);
                    INSERT INTO RewardHistory (EarnedUtc, ActivatedUtc, ExpiresUtc, DurationMilliseconds)
                    SELECT $earned, $t, $expires, $duration WHERE changes() = 0;
                    """;
                command.Parameters.AddWithValue("$expires", Db.ToDb(record.RewardExpiresUtc));
                command.Parameters.AddWithValue("$duration", Db.ToDb(record.RewardDurationMilliseconds));
                command.Parameters.AddWithValue("$earned", Db.ToUnix(record.RewardEarnedUtc ?? transition.TimestampUtc));
                break;

            case RewardTransitionKind.Expired:
            case RewardTransitionKind.Reset:
                command.CommandText = """
                    UPDATE RewardHistory SET EndedUtc = $t, EndReason = $reason
                    WHERE Id = (SELECT MAX(Id) FROM RewardHistory WHERE EndedUtc IS NULL);
                    """;
                command.Parameters.AddWithValue("$reason", transition.Kind.ToString());
                break;
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class SqliteApplicationRuleStore(SqliteDatabase database) : IApplicationRuleStore
{
    public async Task<IReadOnlyList<ApplicationRule>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, ExecutablePath, IdentityMetadata, RuleType, Enabled, CountsTowardGoal, CreatedAtUtc, UpdatedAtUtc
            FROM ApplicationRules ORDER BY RuleType, Name COLLATE NOCASE;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rules = new List<ApplicationRule>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rules.Add(new ApplicationRule
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                ExecutablePath = reader.GetString(2),
                Identity = ParseIdentity(reader.GetString(3)),
                RuleType = (RuleType)reader.GetInt32(4),
                Enabled = reader.GetInt64(5) != 0,
                CountsTowardGoal = reader.GetInt64(6) != 0,
                CreatedAtUtc = Db.FromUnix(reader.GetInt64(7)),
                UpdatedAtUtc = Db.FromUnix(reader.GetInt64(8)),
            });
        }

        return rules;
    }

    public async Task<ApplicationRule> SaveAsync(ApplicationRule rule, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rule);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = rule.Id == 0
            ? """
              INSERT INTO ApplicationRules (Name, ExecutablePath, IdentityMetadata, RuleType, Enabled, CountsTowardGoal, CreatedAtUtc, UpdatedAtUtc)
              VALUES ($name, $path, $identity, $type, $enabled, $counts, $created, $updated);
              SELECT last_insert_rowid();
              """
            : """
              UPDATE ApplicationRules SET Name = $name, ExecutablePath = $path, IdentityMetadata = $identity, RuleType = $type,
                     Enabled = $enabled, CountsTowardGoal = $counts, UpdatedAtUtc = $updated
              WHERE Id = $id;
              SELECT CASE WHEN changes() = 1 THEN $id ELSE 0 END;
              """;
        command.Parameters.AddWithValue("$id", rule.Id);
        command.Parameters.AddWithValue("$name", rule.Name);
        command.Parameters.AddWithValue("$path", rule.ExecutablePath);
        command.Parameters.AddWithValue("$identity", JsonSerializer.Serialize(rule.Identity, Db.Json));
        command.Parameters.AddWithValue("$type", (int)rule.RuleType);
        command.Parameters.AddWithValue("$enabled", rule.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$counts", rule.CountsTowardGoal ? 1 : 0);
        command.Parameters.AddWithValue("$created", Db.ToUnix(rule.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", Db.ToUnix(rule.UpdatedAtUtc));
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        if (id == 0)
        {
            throw new KeyNotFoundException($"Application rule {rule.Id} does not exist.");
        }

        await Db.InsertAuditAsync(connection, transaction, rule.UpdatedAtUtc, rule.Id == 0 ? "RuleAdded" : "RuleUpdated",
            $"{rule.RuleType} app '{rule.Name}' ({rule.ExecutablePath}), enabled={rule.Enabled}", "Success", cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rule with { Id = id };
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        string? description = null;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT RuleType, Name FROM ApplicationRules WHERE Id = $id;";
            select.Parameters.AddWithValue("$id", id);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                description = $"{(RuleType)reader.GetInt32(0)} app '{reader.GetString(1)}' removed";
            }
        }

        if (description is null)
        {
            return false;
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM ApplicationRules WHERE Id = $id;";
            delete.Parameters.AddWithValue("$id", id);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await Db.InsertAuditAsync(connection, transaction, DateTimeOffset.UtcNow, "RuleRemoved", description, "Success", cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static ExecutableIdentity ParseIdentity(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ExecutableIdentity>(json, Db.Json) ?? new ExecutableIdentity();
        }
        catch (JsonException)
        {
            // A damaged identity falls back to exact-path matching, which is the narrowest scope.
            return new ExecutableIdentity();
        }
    }
}

public sealed class SqliteWorkSessionStore(SqliteDatabase database) : IWorkSessionStore
{
    public async Task<long> InsertAsync(WorkSessionRecord session, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO WorkSessions (ApplicationRuleId, StartUtc, EndUtc, QualifyingMilliseconds)
            VALUES ($rule, $start, $end, $ms);
            SELECT last_insert_rowid();
            """;
        Bind(command, session);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task UpdateAsync(WorkSessionRecord session, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE WorkSessions SET EndUtc = $end, QualifyingMilliseconds = $ms WHERE Id = $id;";
        Bind(command, session);
        command.Parameters.AddWithValue("$id", session.Id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Bind(SqliteCommand command, WorkSessionRecord session)
    {
        command.Parameters.AddWithValue("$rule", session.ApplicationRuleId);
        command.Parameters.AddWithValue("$start", Db.ToUnix(session.StartUtc));
        command.Parameters.AddWithValue("$end", Db.ToUnix(session.EndUtc));
        command.Parameters.AddWithValue("$ms", session.QualifyingMilliseconds);
    }
}

public sealed class SqliteSettingsStore(SqliteDatabase database, ILogger<SqliteSettingsStore> logger) : ISettingsStore
{
    private static readonly JsonSerializerOptions SettingsJson = Db.Json;

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        var values = await LoadAllAsync(cancellationToken).ConfigureAwait(false);
        var node = JsonSerializer.SerializeToNode(AppSettings.Default, SettingsJson)!.AsObject();
        foreach (var (key, value) in values)
        {
            if (!node.ContainsKey(key))
            {
                continue;
            }

            try
            {
                var candidate = node.DeepClone().AsObject();
                candidate[key] = JsonNode.Parse(value);
                _ = candidate.Deserialize<AppSettings>(SettingsJson);
                node = candidate;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                logger.LogWarning("Ignoring unreadable setting {Key}", key);
            }
        }

        var settings = node.Deserialize<AppSettings>(SettingsJson) ?? AppSettings.Default;
        if (SettingsValidator.Validate(settings).Count > 0)
        {
            logger.LogWarning("Stored settings are invalid; using defaults for invalid values");
            settings = RepairInvalid(settings);
        }

        return settings;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var node = JsonSerializer.SerializeToNode(settings, SettingsJson)!.AsObject();
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (key, value) in node)
        {
            await UpsertAsync(connection, transaction, key, value?.ToJsonString(SettingsJson) ?? "null", cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetValueAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Settings WHERE Key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetValueAsync(string key, string value, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await UpsertAsync(connection, null, key, value, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, string>> LoadAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Key, Value FROM Settings;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values[reader.GetString(0)] = reader.GetString(1);
        }

        return values;
    }

    private static async Task UpsertAsync(SqliteConnection connection, SqliteTransaction? transaction, string key, string value, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO Settings (Key, Value) VALUES ($key, $value) ON CONFLICT (Key) DO UPDATE SET Value = excluded.Value;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static AppSettings RepairInvalid(AppSettings settings)
    {
        var defaults = AppSettings.Default;
        var invalid = SettingsValidator.Validate(settings).Select(e => e.Property).ToHashSet(StringComparer.Ordinal);
        return settings with
        {
            WorkRequirementMinutes = invalid.Contains(nameof(AppSettings.WorkRequirementMinutes)) ? defaults.WorkRequirementMinutes : settings.WorkRequirementMinutes,
            RewardDurationMinutes = invalid.Contains(nameof(AppSettings.RewardDurationMinutes)) ? defaults.RewardDurationMinutes : settings.RewardDurationMinutes,
            IdleThresholdSeconds = invalid.Contains(nameof(AppSettings.IdleThresholdSeconds)) ? defaults.IdleThresholdSeconds : settings.IdleThresholdSeconds,
            GracefulCloseTimeoutSeconds = invalid.Contains(nameof(AppSettings.GracefulCloseTimeoutSeconds)) ? defaults.GracefulCloseTimeoutSeconds : settings.GracefulCloseTimeoutSeconds,
            DataRetentionDays = invalid.Contains(nameof(AppSettings.DataRetentionDays)) ? defaults.DataRetentionDays : settings.DataRetentionDays,
            EnforcementMode = invalid.Contains(nameof(AppSettings.EnforcementMode)) ? defaults.EnforcementMode : settings.EnforcementMode,
            Theme = invalid.Contains(nameof(AppSettings.Theme)) ? defaults.Theme : settings.Theme,
        };
    }
}

public sealed class SqliteAuditLog(SqliteDatabase database) : IAuditLog
{
    public async Task WriteAsync(string eventType, string description, string result, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await Db.InsertAuditAsync(connection, null, DateTimeOffset.UtcNow, eventType, description, result, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditEvent>> GetRecentAsync(int count, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, TimestampUtc, EventType, Description, Result FROM AuditEvents ORDER BY Id DESC LIMIT $count;";
        command.Parameters.AddWithValue("$count", Math.Clamp(count, 1, 1000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var events = new List<AuditEvent>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new AuditEvent(reader.GetInt64(0), Db.FromUnix(reader.GetInt64(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }

        return events;
    }
}
