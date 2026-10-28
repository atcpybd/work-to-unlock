using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using WorkToUnlock.Core.Persistence;

namespace WorkToUnlock.Infrastructure.Persistence;

public sealed record DatabaseOpenResult(bool Recovered, bool RestoredFromBackup, string? Message);

/// <summary>
/// Owns the SQLite database file: connection settings, schema migrations, integrity checks, backups and
/// recovery from corruption.
/// </summary>
public sealed class SqliteDatabase(string databasePath, string backupPath, ILogger<SqliteDatabase> logger)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Private,
        Pooling = true,
        ForeignKeys = true,
        DefaultTimeout = 30,
    }.ToString();

    public string DatabasePath { get; } = databasePath;

    public string BackupPath { get; } = backupPath;

    public int SchemaVersion => Migrations.Latest;

    /// <summary>Opens a connection with durability settings applied. Callers dispose it.</summary>
    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA synchronous = FULL; PRAGMA busy_timeout = 5000;";
            pragma.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA synchronous = FULL; PRAGMA busy_timeout = 5000;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Creates or upgrades the database. A corrupt database is moved aside and replaced, from the latest backup
    /// when that backup is healthy, otherwise with an empty database (progress is lost, restrictions stay on).
    /// </summary>
    public async Task<DatabaseOpenResult> InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(DatabasePath))!);

        try
        {
            await OpenAndMigrateAsync(cancellationToken).ConfigureAwait(false);
            return new DatabaseOpenResult(false, false, null);
        }
        catch (Exception ex) when (IsCorruption(ex))
        {
            logger.LogError(ex, "Database {Path} is corrupt; recovering", DatabasePath);
        }

        var quarantined = Quarantine(DatabasePath);
        if (File.Exists(BackupPath))
        {
            try
            {
                File.Copy(BackupPath, DatabasePath, overwrite: true);
                await OpenAndMigrateAsync(cancellationToken).ConfigureAwait(false);
                var message = $"The database was damaged and has been restored from the last backup. The damaged copy was saved as {Path.GetFileName(quarantined)}.";
                logger.LogWarning("{Message}", message);
                return new DatabaseOpenResult(true, true, message);
            }
            catch (Exception ex) when (IsCorruption(ex))
            {
                logger.LogError(ex, "Backup database is also unusable");
                Quarantine(DatabasePath);
            }
        }

        await OpenAndMigrateAsync(cancellationToken).ConfigureAwait(false);
        var fresh = $"The database was damaged and has been recreated. Work progress was reset. The damaged copy was saved as {Path.GetFileName(quarantined)}.";
        logger.LogWarning("{Message}", fresh);
        return new DatabaseOpenResult(true, false, fresh);
    }

    /// <summary>Writes a consistent copy of the database next to it (VACUUM INTO), replacing the previous backup.</summary>
    public async Task BackupAsync(CancellationToken cancellationToken)
    {
        var temp = BackupPath + ".tmp";
        if (File.Exists(temp))
        {
            File.Delete(temp);
        }

        await using (var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "VACUUM INTO $path";
            command.Parameters.AddWithValue("$path", temp);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temp, BackupPath, overwrite: true);
    }

    private async Task OpenAndMigrateAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            await wal.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA quick_check;";
            var result = (string?)await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new PersistenceException($"Integrity check failed: {result}");
            }
        }

        await Migrations.ApplyAsync(connection, logger, cancellationToken).ConfigureAwait(false);
    }

    private string Quarantine(string path)
    {
        SqliteConnection.ClearAllPools();
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var target = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}.corrupt-{stamp}.db");
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var source = path + suffix;
            if (File.Exists(source))
            {
                File.Move(source, target + suffix, overwrite: true);
            }
        }

        return target;
    }

    private static bool IsCorruption(Exception ex) => ex switch
    {
        // SQLITE_CORRUPT = 11, SQLITE_NOTADB = 26
        SqliteException { SqliteErrorCode: 11 or 26 } => true,
        PersistenceException => true,
        _ => false,
    };
}
