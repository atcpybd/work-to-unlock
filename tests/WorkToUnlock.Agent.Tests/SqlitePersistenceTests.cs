using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Persistence;
using WorkToUnlock.Core.Rewards;
using WorkToUnlock.Infrastructure.Persistence;

namespace WorkToUnlock.Agent.Tests;

/// <summary>Persistence tests against real SQLite files. Portable: no Windows APIs involved.</summary>
public sealed class SqlitePersistenceTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wtu-tests", Guid.NewGuid().ToString("N"));
    private SqliteDatabase _database = null!;

    private string DatabasePath => Path.Combine(_directory, "test.db");

    private string BackupPath => Path.Combine(_directory, "test.backup.db");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _database = new SqliteDatabase(DatabasePath, BackupPath, NullLogger<SqliteDatabase>.Instance);
        await _database.InitializeAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Migrations_create_the_schema_and_are_idempotent()
    {
        var again = await _database.InitializeAsync(CancellationToken.None);

        Assert.False(again.Recovered);
        await using var connection = await _database.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        var tables = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }

        Assert.Equal(["Achievements", "ApplicationRules", "AuditEvents", "BlockedAttempts", "Downtime", "RewardHistory", "RewardState", "Settings", "WorkSessions"], tables);
    }

    [Fact]
    public async Task Reward_state_round_trips_with_versions_history_and_audit()
    {
        var store = new SqliteRewardStateStore(_database);
        var now = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.Null(await store.LoadAsync(CancellationToken.None));
        var ready = await store.SaveAsync(new RewardStateRecord { State = RewardState.RewardReady, AccumulatedWorkMilliseconds = 3_600_000, RewardEarnedUtc = now }, 0,
            new RewardTransition(RewardTransitionKind.Earned, now, "earned"), CancellationToken.None);
        var unlocked = await store.SaveAsync(ready with
        {
            State = RewardState.Unlocked,
            RewardActivatedUtc = now.AddMinutes(1),
            RewardExpiresUtc = now.AddMinutes(21),
            RewardDurationMilliseconds = 1_200_000,
            DeadlineMonotonicMilliseconds = 99,
            BootId = "7",
        }, ready.Version, new RewardTransition(RewardTransitionKind.Activated, now.AddMinutes(1), "activated"), CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(unlocked, loaded);
        Assert.Equal(2, loaded!.Version);
        var report = await new SqliteStatisticsStore(_database).GetReportAsync(now, TimeZoneInfo.Utc, 7, CancellationToken.None);
        Assert.Equal(1, report.RewardsEarned);
        Assert.Equal(1, report.RewardsActivated);
        Assert.Equal(TimeSpan.FromMinutes(20), report.RewardTimeActivated);
        var audit = await new SqliteAuditLog(_database).GetRecentAsync(10, CancellationToken.None);
        Assert.Equal(["RewardActivated", "RewardEarned"], audit.Select(a => a.EventType));
    }

    [Fact]
    public async Task A_paused_break_round_trips_without_adding_reward_history()
    {
        var store = new SqliteRewardStateStore(_database);
        var now = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var unlocked = await store.SaveAsync(new RewardStateRecord
        {
            State = RewardState.Unlocked,
            AccumulatedWorkMilliseconds = 3_600_000,
            RewardActivatedUtc = now,
            RewardExpiresUtc = now.AddMinutes(20),
            RewardDurationMilliseconds = 1_200_000,
        }, 0, new RewardTransition(RewardTransitionKind.Activated, now, "activated"), CancellationToken.None);

        var paused = await store.SaveAsync(unlocked with { PausedRemainingMilliseconds = 900_000, PausedUtc = now.AddMinutes(5) }, unlocked.Version,
            new RewardTransition(RewardTransitionKind.Paused, now.AddMinutes(5), "paused"), CancellationToken.None);

        Assert.Equal(paused, await store.LoadAsync(CancellationToken.None));
        var report = await new SqliteStatisticsStore(_database).GetReportAsync(now, TimeZoneInfo.Utc, 7, CancellationToken.None);
        Assert.Equal(1, report.RewardsActivated);
        var audit = await new SqliteAuditLog(_database).GetRecentAsync(10, CancellationToken.None);
        Assert.Equal("RewardPaused", audit[0].EventType);
    }

    [Fact]
    public async Task Stale_versions_are_rejected()
    {
        var store = new SqliteRewardStateStore(_database);
        await store.SaveAsync(new RewardStateRecord { AccumulatedWorkMilliseconds = 1 }, 0, null, CancellationToken.None);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            store.SaveAsync(new RewardStateRecord { AccumulatedWorkMilliseconds = 2 }, 0, null, CancellationToken.None));
    }

    [Fact]
    public async Task Engine_progress_survives_a_restart_with_sqlite()
    {
        var clock = new FakeClock();
        var settings = new RewardSettings(TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(20));
        using (var engine = new RewardEngine(new SqliteRewardStateStore(_database), clock, NullLogger<RewardEngine>.Instance))
        {
            await engine.InitializeAsync(settings, CancellationToken.None);
            await engine.AddWorkAsync(TimeSpan.FromMinutes(35), CancellationToken.None);
            await engine.FlushAsync(CancellationToken.None);
        }

        using var restarted = new RewardEngine(new SqliteRewardStateStore(_database), clock, NullLogger<RewardEngine>.Instance);
        await restarted.InitializeAsync(settings, CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(35), restarted.GetStatus().AccumulatedWork);
    }

    [Fact]
    public async Task Rules_round_trip_including_identity()
    {
        var store = new SqliteApplicationRuleStore(_database);
        var saved = await store.SaveAsync(TestRules.Restricted(0, "Steam", TestRules.SteamPath, new ExecutableIdentity
        {
            FileName = "steam.exe",
            Publisher = "Valve Corp.",
            SignatureTrusted = true,
            InstallRoot = @"C:\Program Files (x86)\Steam",
            Scope = MatchScope.InstallFolder,
            RelatedExecutables = ["steamwebhelper.exe"],
        }), CancellationToken.None);

        var loaded = Assert.Single(await store.GetAllAsync(CancellationToken.None));

        Assert.True(saved.Id > 0);
        Assert.Equal(saved.Identity.InstallRoot, loaded.Identity.InstallRoot);
        Assert.Equal(["steamwebhelper.exe"], loaded.Identity.RelatedExecutables);
        Assert.Equal(MatchScope.InstallFolder, loaded.Identity.Scope);

        await store.SaveAsync(loaded with { Enabled = false }, CancellationToken.None);
        Assert.False(Assert.Single(await store.GetAllAsync(CancellationToken.None)).Enabled);
        Assert.True(await store.DeleteAsync(saved.Id, CancellationToken.None));
        Assert.Empty(await store.GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Settings_round_trip_and_bad_values_fall_back_to_defaults()
    {
        var store = new SqliteSettingsStore(_database, NullLogger<SqliteSettingsStore>.Instance);
        await store.SaveAsync(AppSettings.Default with { WorkRequirementMinutes = 45, Theme = ThemePreference.Dark }, CancellationToken.None);
        await store.SetValueAsync(nameof(AppSettings.IdleThresholdSeconds), "\"not a number\"", CancellationToken.None);
        await store.SetValueAsync(nameof(AppSettings.RewardDurationMinutes), "9999", CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(45, loaded.WorkRequirementMinutes);
        Assert.Equal(ThemePreference.Dark, loaded.Theme);
        Assert.Equal(AppSettings.Default.IdleThresholdSeconds, loaded.IdleThresholdSeconds);
        Assert.Equal(AppSettings.Default.RewardDurationMinutes, loaded.RewardDurationMinutes);
    }

    [Fact]
    public async Task Statistics_group_work_by_day_and_application()
    {
        var rules = new SqliteApplicationRuleStore(_database);
        var code = await rules.SaveAsync(TestRules.Work(0, "Code", TestRules.VsCodePath), CancellationToken.None);
        var bench = await rules.SaveAsync(TestRules.Work(0, "Blockbench", TestRules.BlockbenchPath), CancellationToken.None);
        var sessions = new SqliteWorkSessionStore(_database);
        var now = new DateTimeOffset(2026, 3, 4, 15, 0, 0, TimeSpan.Zero); // a Wednesday
        await sessions.InsertAsync(new WorkSessionRecord { ApplicationRuleId = code.Id, StartUtc = now.AddHours(-2), EndUtc = now.AddHours(-1), QualifyingMilliseconds = 1_800_000 }, CancellationToken.None);
        await sessions.InsertAsync(new WorkSessionRecord { ApplicationRuleId = bench.Id, StartUtc = now.AddHours(-1), EndUtc = now, QualifyingMilliseconds = 600_000 }, CancellationToken.None);
        await sessions.InsertAsync(new WorkSessionRecord { ApplicationRuleId = code.Id, StartUtc = now.AddDays(-1), EndUtc = now.AddDays(-1).AddHours(1), QualifyingMilliseconds = 3_600_000 }, CancellationToken.None);

        var report = await new SqliteStatisticsStore(_database).GetReportAsync(now, TimeZoneInfo.Utc, 7, CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(40), report.WorkToday);
        Assert.Equal(2, report.WorkByApplicationToday.Count);
        Assert.Equal("Code", report.WorkByApplicationToday[0].Name);
        Assert.Equal(7, report.DailyHistory.Count);
        Assert.Equal(TimeSpan.FromHours(1), report.DailyHistory[^2].QualifyingWork);
        Assert.True(report.WorkThisWeek >= report.WorkToday);
    }

    [Fact]
    public async Task Blocked_attempts_are_counted_per_day_and_application()
    {
        var rules = new SqliteApplicationRuleStore(_database);
        var steam = await rules.SaveAsync(TestRules.Restricted(0, "Steam", TestRules.SteamPath), CancellationToken.None);
        var discord = await rules.SaveAsync(TestRules.Restricted(0, "Discord", TestRules.DiscordPath), CancellationToken.None);
        var store = new SqliteBlockedAttemptStore(_database);
        var now = new DateTimeOffset(2026, 3, 4, 15, 0, 0, TimeSpan.Zero);
        await store.RecordAsync(steam.Id, now.AddHours(-1), CancellationToken.None);
        await store.RecordAsync(steam.Id, now.AddMinutes(-5), CancellationToken.None);
        await store.RecordAsync(discord.Id, now.AddMinutes(-1), CancellationToken.None);
        await store.RecordAsync(steam.Id, now.AddDays(-1), CancellationToken.None);
        var statistics = new SqliteStatisticsStore(_database);

        var report = await statistics.GetReportAsync(now, TimeZoneInfo.Utc, 7, CancellationToken.None);
        var days = await statistics.GetDailyTotalsAsync(now, TimeZoneInfo.Utc, 2, CancellationToken.None);

        Assert.Equal(3, report.BlockedToday);
        Assert.Equal(("Steam", 2), (report.BlockedTodayByApplication[0].Name, report.BlockedTodayByApplication[0].Count));
        Assert.Equal([1, 3], days.Select(d => d.BlockedAttempts));
    }

    [Fact]
    public async Task Downtime_periods_open_close_and_are_listed_since_a_time()
    {
        var store = new SqliteDowntimeStore(_database);
        var now = new DateTimeOffset(2026, 3, 4, 15, 0, 0, TimeSpan.Zero);
        await store.AddAsync(new(DowntimeKinds.AgentNotRunning, now.AddDays(-20), now.AddDays(-20).AddHours(1)), CancellationToken.None);
        await store.AddAsync(new(DowntimeKinds.AgentNotRunning, now.AddHours(-3), now.AddHours(-2)), CancellationToken.None);
        await store.AddAsync(new(DowntimeKinds.EnforcementDisabled, now.AddHours(-1), null), CancellationToken.None);
        await store.AddAsync(new(DowntimeKinds.AppAllowed, now.AddHours(-1), null, 7, "Steam"), CancellationToken.None);
        await store.AddAsync(new(DowntimeKinds.AppAllowed, now.AddHours(-1), null, 8, "Discord"), CancellationToken.None);

        Assert.Equal(4, (await store.GetSinceAsync(now.AddDays(-7), CancellationToken.None)).Count);
        await store.CloseOpenAsync(DowntimeKinds.EnforcementDisabled, null, now, CancellationToken.None);
        await store.CloseOpenAsync(DowntimeKinds.AppAllowed, 7, now, CancellationToken.None);

        var periods = await store.GetSinceAsync(now.AddDays(-7), CancellationToken.None);
        Assert.Equal(now, periods.Single(p => p.Kind == DowntimeKinds.EnforcementDisabled).EndUtc);
        Assert.Equal(now, periods.Single(p => p.RuleId == 7).EndUtc);
        var discord = periods.Single(p => p.RuleId == 8);
        Assert.Null(discord.EndUtc);
        Assert.Equal("Discord", discord.Subject);
        Assert.Equal([8L], (await store.GetSinceAsync(now.AddMinutes(1), CancellationToken.None)).Select(p => p.RuleId!.Value));
    }

    [Fact]
    public async Task Pruning_removes_old_activity_only()
    {
        var sessions = new SqliteWorkSessionStore(_database);
        var now = DateTimeOffset.UtcNow;
        await sessions.InsertAsync(new WorkSessionRecord { ApplicationRuleId = 1, StartUtc = now.AddDays(-100), EndUtc = now.AddDays(-100), QualifyingMilliseconds = 1 }, CancellationToken.None);
        await sessions.InsertAsync(new WorkSessionRecord { ApplicationRuleId = 1, StartUtc = now.AddDays(-1), EndUtc = now.AddDays(-1), QualifyingMilliseconds = 1 }, CancellationToken.None);

        var removed = await new SqliteStatisticsStore(_database).PruneAsync(now.AddDays(-90), CancellationToken.None);

        Assert.Equal(1, removed);
    }

    [Fact]
    public async Task Export_contains_every_table_as_json()
    {
        await new SqliteAuditLog(_database).WriteAsync("Test", "hello", "Success", CancellationToken.None);

        var json = await new SqliteStatisticsStore(_database).ExportJsonAsync(CancellationToken.None);

        using var document = JsonDocument.Parse(json);
        foreach (var name in new[] { "settings", "applicationRules", "rewardState", "rewardHistory", "workSessions", "auditEvents" })
        {
            Assert.True(document.RootElement.TryGetProperty(name, out _), name);
        }

        Assert.Equal("hello", document.RootElement.GetProperty("auditEvents")[0].GetProperty("Description").GetString());
    }

    [Fact]
    public async Task Corrupt_database_is_quarantined_and_recreated()
    {
        SqliteConnection.ClearAllPools();
        await File.WriteAllTextAsync(DatabasePath, "this is not a database, it is garbage that fills the header area of the file....");
        File.Delete(DatabasePath + "-wal");
        File.Delete(DatabasePath + "-shm");

        var result = await _database.InitializeAsync(CancellationToken.None);

        Assert.True(result.Recovered);
        Assert.False(result.RestoredFromBackup);
        Assert.Contains(Directory.GetFiles(_directory), f => f.Contains(".corrupt-", StringComparison.Ordinal));
        Assert.Null(await new SqliteRewardStateStore(_database).LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Corrupt_database_is_restored_from_the_backup()
    {
        var store = new SqliteRewardStateStore(_database);
        await store.SaveAsync(new RewardStateRecord { AccumulatedWorkMilliseconds = 1_234 }, 0, null, CancellationToken.None);
        await _database.BackupAsync(CancellationToken.None);
        SqliteConnection.ClearAllPools();
        File.Delete(DatabasePath + "-wal");
        File.Delete(DatabasePath + "-shm");
        await File.WriteAllTextAsync(DatabasePath, "garbage garbage garbage garbage garbage garbage garbage garbage garbage garbage");

        var result = await _database.InitializeAsync(CancellationToken.None);

        Assert.True(result.RestoredFromBackup);
        Assert.Equal(1_234, (await store.LoadAsync(CancellationToken.None))!.AccumulatedWorkMilliseconds);
    }
}
