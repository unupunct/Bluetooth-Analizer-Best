using BluetoothFleetMonitor.Database;
using BluetoothFleetMonitor.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BluetoothFleetMonitor.Tests;

public sealed class DatabaseTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");

    public DatabaseTests() => _conn.Open();
    public void Dispose() => _conn.Dispose();

    private FleetDbContext NewContext() =>
        new(new DbContextOptionsBuilder<FleetDbContext>().UseSqlite(_conn).Options);

    private async Task<FleetDbContext> MigratedAsync()
    {
        var db = NewContext();
        await db.Database.MigrateAsync();
        return db;
    }

    [Fact]
    public async Task Migrates_empty_database_with_all_spec_tables()
    {
        await using var db = await MigratedAsync();
        var tables = new[] { "Devices", "DeviceIdentifiers", "Scanners", "Observations", "PresenceEvents",
                             "ConnectionTests", "InspectionSessions", "FaultReports", "UsbEvents", "ApplicationEvents" };
        foreach (var t in tables)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{t}'";
            Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        }
        Assert.Equal(0, await db.Devices.CountAsync());
    }

    [Fact]
    public async Task Observation_with_duplicate_id_is_rejected_by_the_database()
    {
        await using var db = await MigratedAsync();
        var id = Guid.NewGuid();
        db.Observations.Add(new Observation { Id = id, DeviceId = Guid.NewGuid(), ScannerId = Guid.NewGuid(), TimestampUtc = DateTime.UtcNow, ProcessedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();

        await using var db2 = NewContext();
        db2.Observations.Add(new Observation { Id = id, DeviceId = Guid.NewGuid(), ScannerId = Guid.NewGuid(), TimestampUtc = DateTime.UtcNow, ProcessedAtUtc = DateTime.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
    }

    [Theory]
    [InlineData("Observations", "DeviceId")]
    [InlineData("Observations", "ScannerId")]
    [InlineData("Observations", "TimestampUtc")]
    [InlineData("Observations", "LocationLabel")]
    [InlineData("PresenceEvents", "EventType")]
    [InlineData("PresenceEvents", "DeviceId")]
    [InlineData("Devices", "Category")]
    [InlineData("ApplicationEvents", "TimestampUtc")]
    public async Task Required_indexes_exist(string table, string column)
    {
        await using var db = await MigratedAsync();
        var indexed = new List<string>();
        using (var list = _conn.CreateCommand())
        {
            list.CommandText = $"PRAGMA index_list('{table}')";
            var names = new List<string>();
            using (var r = list.ExecuteReader()) while (r.Read()) names.Add(r.GetString(1));
            foreach (var n in names)
            {
                using var info = _conn.CreateCommand();
                info.CommandText = $"PRAGMA index_info('{n}')";
                using var r = info.ExecuteReader();
                while (r.Read()) indexed.Add(r.GetString(2));
            }
        }
        Assert.Contains(column, indexed);
    }

    [Fact]
    public async Task Timestamps_round_trip_as_utc()
    {
        await using var db = await MigratedAsync();
        var observed = new DateTime(2026, 11, 20, 10, 30, 0, DateTimeKind.Utc);
        var id = Guid.NewGuid();
        db.Observations.Add(new Observation { Id = id, DeviceId = Guid.NewGuid(), ScannerId = Guid.NewGuid(), TimestampUtc = observed, ProcessedAtUtc = observed.AddSeconds(2) });
        await db.SaveChangesAsync();

        await using var db2 = NewContext();
        var back = await db2.Observations.SingleAsync(o => o.Id == id);
        Assert.Equal(DateTimeKind.Utc, back.TimestampUtc.Kind);
        Assert.Equal(observed, back.TimestampUtc);
        Assert.Null(back.UploadedAtUtc);
        Assert.Equal(observed.AddSeconds(2), back.ProcessedAtUtc);
    }

    [Fact]
    public async Task Integrity_check_passes_on_a_fresh_database()
    {
        await using var db = await MigratedAsync();
        Assert.True(await DatabaseMaintenance.CheckIntegrityAsync(db));
    }

    [Fact]
    public async Task Backup_creates_a_readable_copy_with_the_data()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bfm-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "fleet.db");
        try
        {
            var options = new DbContextOptionsBuilder<FleetDbContext>().UseSqlite($"Data Source={dbPath}").Options;
            await using (var db = new FleetDbContext(options))
            {
                await db.Database.MigrateAsync();
                db.Devices.Add(new Device { CustomName = "Lush 01", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }

            var backup = await DatabaseMaintenance.BackupAsync(dbPath);

            Assert.True(File.Exists(backup));
            var backupOptions = new DbContextOptionsBuilder<FleetDbContext>().UseSqlite($"Data Source={backup}").Options;
            await using var copy = new FleetDbContext(backupOptions);
            Assert.Equal("Lush 01", (await copy.Devices.SingleAsync()).CustomName);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { /* temp cleanup is best effort */ }
        }
    }
}
