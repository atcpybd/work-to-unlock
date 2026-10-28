using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WorkToUnlock.Core.Persistence;

namespace WorkToUnlock.Infrastructure.Persistence;

/// <summary>Aggregates local activity data for the statistics page, retention and export.</summary>
public sealed class SqliteStatisticsStore(SqliteDatabase database) : IStatisticsStore
{
    public async Task<StatisticsReport> GetReportAsync(DateTimeOffset now, TimeZoneInfo timeZone, int historyDays, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        historyDays = Math.Clamp(historyDays, 1, 366);

        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var firstDay = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek - (int)firstDay + 7) % 7));
        var historyStart = today.AddDays(-(historyDays - 1));
        var rangeStart = historyStart < weekStart ? historyStart : weekStart;

        var todayStartUtc = StartOfDayUtc(today, timeZone);
        var weekStartUtc = StartOfDayUtc(weekStart, timeZone);

        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var fromUtc = StartOfDayUtc(rangeStart, timeZone);
        var sessions = await LoadSessionsAsync(connection, fromUtc, cancellationToken).ConfigureAwait(false);
        var blocked = await LoadBlockedAsync(connection, fromUtc, cancellationToken).ConfigureAwait(false);

        var todaySessions = sessions.Where(s => s.Start >= todayStartUtc).ToList();
        var weekSessions = sessions.Where(s => s.Start >= weekStartUtc).ToList();
        var blockedToday = blocked.Where(b => b.When >= todayStartUtc).ToList();
        var history = Bucket(historyStart, historyDays, timeZone, sessions, blocked);

        int earned, activated;
        long activatedMs;
        await using (var rewards = connection.CreateCommand())
        {
            rewards.CommandText = """
                SELECT COUNT(*), COUNT(ActivatedUtc), COALESCE(SUM(CASE WHEN ActivatedUtc IS NOT NULL THEN DurationMilliseconds END), 0)
                FROM RewardHistory;
                """;
            await using var reader = await rewards.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            earned = reader.GetInt32(0);
            activated = reader.GetInt32(1);
            activatedMs = reader.GetInt64(2);
        }

        return new StatisticsReport
        {
            WorkToday = TimeSpan.FromMilliseconds(todaySessions.Sum(s => s.Ms)),
            WorkThisWeek = TimeSpan.FromMilliseconds(weekSessions.Sum(s => s.Ms)),
            RewardsEarned = earned,
            RewardsActivated = activated,
            RewardTimeActivated = TimeSpan.FromMilliseconds(activatedMs),
            WorkByApplicationToday = ByApplication(todaySessions),
            WorkByApplicationThisWeek = ByApplication(weekSessions),
            DailyHistory = history,
            BlockedToday = blockedToday.Count,
            BlockedTodayByApplication = blockedToday
                .GroupBy(b => b.RuleId)
                .Select(g => new ApplicationCount(g.Key, g.First().Name, g.Count()))
                .OrderByDescending(c => c.Count)
                .ToList(),
        };
    }

    public async Task<IReadOnlyList<DailyActivity>> GetDailyTotalsAsync(DateTimeOffset now, TimeZoneInfo timeZone, int days, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        days = Math.Clamp(days, 1, 3660);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone).DateTime);
        var firstDay = today.AddDays(-(days - 1));
        var fromUtc = StartOfDayUtc(firstDay, timeZone);

        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var sessions = await LoadSessionsAsync(connection, fromUtc, cancellationToken).ConfigureAwait(false);
        var blocked = await LoadBlockedAsync(connection, fromUtc, cancellationToken).ConfigureAwait(false);
        return Bucket(firstDay, days, timeZone, sessions, blocked);
    }

    /// <summary>Work sessions starting at or after <paramref name="fromUtc"/>, attributed to the local day they started.</summary>
    private static async Task<List<(long RuleId, string Name, DateTimeOffset Start, long Ms)>> LoadSessionsAsync(SqliteConnection connection, DateTimeOffset fromUtc, CancellationToken cancellationToken)
    {
        var sessions = new List<(long RuleId, string Name, DateTimeOffset Start, long Ms)>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.ApplicationRuleId, COALESCE(r.Name, '(removed app)'), s.StartUtc, s.QualifyingMilliseconds
            FROM WorkSessions s LEFT JOIN ApplicationRules r ON r.Id = s.ApplicationRuleId
            WHERE s.StartUtc >= $from;
            """;
        command.Parameters.AddWithValue("$from", Db.ToUnix(fromUtc));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sessions.Add((reader.GetInt64(0), reader.GetString(1), Db.FromUnix(reader.GetInt64(2)), reader.GetInt64(3)));
        }

        return sessions;
    }

    private static async Task<List<(long RuleId, string Name, DateTimeOffset When)>> LoadBlockedAsync(SqliteConnection connection, DateTimeOffset fromUtc, CancellationToken cancellationToken)
    {
        var attempts = new List<(long RuleId, string Name, DateTimeOffset When)>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT b.ApplicationRuleId, COALESCE(r.Name, '(removed app)'), b.TimestampUtc
            FROM BlockedAttempts b LEFT JOIN ApplicationRules r ON r.Id = b.ApplicationRuleId
            WHERE b.TimestampUtc >= $from;
            """;
        command.Parameters.AddWithValue("$from", Db.ToUnix(fromUtc));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            attempts.Add((reader.GetInt64(0), reader.GetString(1), Db.FromUnix(reader.GetInt64(2))));
        }

        return attempts;
    }

    private static List<DailyActivity> Bucket(
        DateOnly firstDay,
        int days,
        TimeZoneInfo timeZone,
        List<(long RuleId, string Name, DateTimeOffset Start, long Ms)> sessions,
        List<(long RuleId, string Name, DateTimeOffset When)> blocked) =>
        Enumerable.Range(0, days)
            .Select(i => firstDay.AddDays(i))
            .Select(day =>
            {
                var start = StartOfDayUtc(day, timeZone);
                var end = StartOfDayUtc(day.AddDays(1), timeZone);
                var ms = sessions.Where(s => s.Start >= start && s.Start < end).Sum(s => s.Ms);
                var attempts = blocked.Count(b => b.When >= start && b.When < end);
                return new DailyActivity(day, TimeSpan.FromMilliseconds(ms), attempts);
            })
            .ToList();

    public async Task<int> PruneAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM WorkSessions WHERE EndUtc < $cutoff;
            DELETE FROM AuditEvents WHERE TimestampUtc < $cutoff;
            DELETE FROM RewardHistory WHERE EndedUtc IS NOT NULL AND EndedUtc < $cutoff;
            DELETE FROM BlockedAttempts WHERE TimestampUtc < $cutoff;
            DELETE FROM Downtime WHERE EndUtc IS NOT NULL AND EndUtc < $cutoff;
            """;
        command.Parameters.AddWithValue("$cutoff", Db.ToUnix(cutoffUtc));
        var removed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return removed;
    }

    public async Task<string> ExportJsonAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", "work-to-unlock-export");
            writer.WriteNumber("schemaVersion", database.SchemaVersion);
            writer.WriteString("exportedUtc", DateTimeOffset.UtcNow);
            foreach (var table in new[] { "Settings", "ApplicationRules", "RewardState", "RewardHistory", "WorkSessions", "BlockedAttempts", "Downtime", "Achievements", "AuditEvents" })
            {
                await WriteTableAsync(connection, writer, table, cancellationToken).ConfigureAwait(false);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static IReadOnlyList<ApplicationTime> ByApplication(IEnumerable<(long RuleId, string Name, DateTimeOffset Start, long Ms)> sessions) =>
        sessions
            .GroupBy(s => s.RuleId)
            .Select(g => new ApplicationTime(g.Key, g.First().Name, TimeSpan.FromMilliseconds(g.Sum(s => s.Ms))))
            .OrderByDescending(a => a.Time)
            .ToList();

    private static DateTimeOffset StartOfDayUtc(DateOnly day, TimeZoneInfo timeZone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        return new DateTimeOffset(local, timeZone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static async Task WriteTableAsync(SqliteConnection connection, Utf8JsonWriter writer, string table, CancellationToken cancellationToken)
    {
        writer.WriteStartArray(char.ToLowerInvariant(table[0]) + table[1..]);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table} ORDER BY 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            writer.WriteStartObject();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                if (reader.IsDBNull(i))
                {
                    writer.WriteNull(name);
                    continue;
                }

                var value = reader.GetValue(i);
                switch (value)
                {
                    case long number when name.EndsWith("Utc", StringComparison.Ordinal):
                        writer.WriteString(name, Db.FromUnix(number));
                        break;
                    case long number:
                        writer.WriteNumber(name, number);
                        break;
                    case double real:
                        writer.WriteNumber(name, real);
                        break;
                    default:
                        writer.WriteString(name, Convert.ToString(value, CultureInfo.InvariantCulture));
                        break;
                }
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
