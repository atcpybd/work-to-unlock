using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using WorkToUnlock.Core.Persistence;

namespace WorkToUnlock.Infrastructure.Persistence;

/// <summary>
/// Ordered schema migrations tracked with PRAGMA user_version. Each migration runs in its own transaction
/// together with the version bump, so a crash mid-migration leaves the previous version intact.
/// Never edit a released migration; append a new one.
/// </summary>
internal static class Migrations
{
    private static readonly string[] Scripts =
    [
        // 1: initial schema. Timestamps are Unix epoch milliseconds (UTC).
        """
        CREATE TABLE ApplicationRules (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL,
            ExecutablePath TEXT NOT NULL,
            IdentityMetadata TEXT NOT NULL DEFAULT '{}',
            RuleType INTEGER NOT NULL,
            Enabled INTEGER NOT NULL DEFAULT 1,
            CountsTowardGoal INTEGER NOT NULL DEFAULT 1,
            CreatedAtUtc INTEGER NOT NULL,
            UpdatedAtUtc INTEGER NOT NULL
        );
        CREATE INDEX IX_ApplicationRules_RuleType ON ApplicationRules (RuleType);

        CREATE TABLE WorkSessions (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ApplicationRuleId INTEGER NOT NULL,
            StartUtc INTEGER NOT NULL,
            EndUtc INTEGER NOT NULL,
            QualifyingMilliseconds INTEGER NOT NULL CHECK (QualifyingMilliseconds >= 0)
        );
        CREATE INDEX IX_WorkSessions_StartUtc ON WorkSessions (StartUtc);

        CREATE TABLE RewardState (
            Id INTEGER PRIMARY KEY CHECK (Id = 1),
            State INTEGER NOT NULL,
            AccumulatedWorkMilliseconds INTEGER NOT NULL,
            RewardEarnedUtc INTEGER NULL,
            RewardActivatedUtc INTEGER NULL,
            RewardExpiresUtc INTEGER NULL,
            RewardDurationMilliseconds INTEGER NULL,
            DeadlineMonotonicMilliseconds INTEGER NULL,
            BootId TEXT NULL,
            Version INTEGER NOT NULL
        );

        CREATE TABLE RewardHistory (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            EarnedUtc INTEGER NOT NULL,
            ActivatedUtc INTEGER NULL,
            ExpiresUtc INTEGER NULL,
            DurationMilliseconds INTEGER NULL,
            EndedUtc INTEGER NULL,
            EndReason TEXT NULL
        );

        CREATE TABLE Settings (
            Key TEXT PRIMARY KEY NOT NULL,
            Value TEXT NOT NULL
        );

        CREATE TABLE AuditEvents (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            TimestampUtc INTEGER NOT NULL,
            EventType TEXT NOT NULL,
            Description TEXT NOT NULL,
            Result TEXT NOT NULL
        );
        CREATE INDEX IX_AuditEvents_TimestampUtc ON AuditEvents (TimestampUtc);
        """,

        // 2: blocked launch attempts, achievements, downtime (agent not running / enforcement off),
        //    break balance and paused breaks.
        """
        CREATE TABLE BlockedAttempts (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            TimestampUtc INTEGER NOT NULL,
            ApplicationRuleId INTEGER NOT NULL
        );
        CREATE INDEX IX_BlockedAttempts_TimestampUtc ON BlockedAttempts (TimestampUtc);

        CREATE TABLE Achievements (
            Id TEXT PRIMARY KEY NOT NULL,
            UnlockedUtc INTEGER NOT NULL
        );

        CREATE TABLE Downtime (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Kind TEXT NOT NULL,
            StartUtc INTEGER NOT NULL,
            EndUtc INTEGER NULL
        );
        CREATE INDEX IX_Downtime_StartUtc ON Downtime (StartUtc);

        ALTER TABLE RewardState ADD COLUMN BalanceMilliseconds INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE RewardState ADD COLUMN PausedRemainingMilliseconds INTEGER NULL;
        ALTER TABLE RewardState ADD COLUMN PausedUtc INTEGER NULL;
        """,
    ];

    public static int Latest => Scripts.Length;

    public static async Task ApplyAsync(SqliteConnection connection, ILogger logger, CancellationToken cancellationToken)
    {
        var current = await GetVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        if (current > Latest)
        {
            throw new PersistenceException($"The database schema (version {current}) is newer than this version of Work to Unlock supports ({Latest}).");
        }

        for (var version = current + 1; version <= Latest; version++)
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = Scripts[version - 1] + $"\nPRAGMA user_version = {version};";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Applied database migration {Version}", version);
        }
    }

    public static async Task<int> GetVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }
}
