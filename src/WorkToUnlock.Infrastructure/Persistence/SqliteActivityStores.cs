using Microsoft.Data.Sqlite;
using WorkToUnlock.Core.Persistence;

namespace WorkToUnlock.Infrastructure.Persistence;

public sealed class SqliteBlockedAttemptStore(SqliteDatabase database) : IBlockedAttemptStore
{
    public async Task RecordAsync(long ruleId, DateTimeOffset timestampUtc, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO BlockedAttempts (TimestampUtc, ApplicationRuleId) VALUES ($t, $rule);";
        command.Parameters.AddWithValue("$t", Db.ToUnix(timestampUtc));
        command.Parameters.AddWithValue("$rule", ruleId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class SqliteAchievementStore(SqliteDatabase database) : IAchievementStore
{
    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> GetUnlockedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, UnlockedUtc FROM Achievements;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var unlocked = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            unlocked[reader.GetString(0)] = Db.FromUnix(reader.GetInt64(1));
        }

        return unlocked;
    }

    public async Task<bool> UnlockAsync(string id, DateTimeOffset unlockedUtc, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Achievements (Id, UnlockedUtc) VALUES ($id, $t) ON CONFLICT (Id) DO NOTHING; SELECT changes();";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$t", Db.ToUnix(unlockedUtc));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }
}

public sealed class SqliteDowntimeStore(SqliteDatabase database) : IDowntimeStore
{
    public async Task AddAsync(string kind, DateTimeOffset startUtc, DateTimeOffset? endUtc, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Downtime (Kind, StartUtc, EndUtc) VALUES ($kind, $start, $end);";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$start", Db.ToUnix(startUtc));
        command.Parameters.AddWithValue("$end", Db.ToDb(endUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CloseOpenAsync(string kind, DateTimeOffset endUtc, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Downtime SET EndUtc = $end WHERE Kind = $kind AND EndUtc IS NULL;";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$end", Db.ToUnix(endUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DowntimePeriod>> GetSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Kind, StartUtc, EndUtc FROM Downtime WHERE EndUtc IS NULL OR EndUtc >= $since ORDER BY StartUtc;";
        command.Parameters.AddWithValue("$since", Db.ToUnix(sinceUtc));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var periods = new List<DowntimePeriod>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            periods.Add(new DowntimePeriod(reader.GetString(0), Db.FromUnix(reader.GetInt64(1)), reader.GetNullableTime(2)));
        }

        return periods;
    }
}
