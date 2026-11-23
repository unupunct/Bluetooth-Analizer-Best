using BluetoothFleetMonitor.Application.Diagnostics;
using BluetoothFleetMonitor.Database;
using BluetoothFleetMonitor.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BluetoothFleetMonitor.Tests;

public sealed class FaultQueriesTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private sealed class Factory(SqliteConnection c) : IDbContextFactory<FleetDbContext>
    {
        public FleetDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FleetDbContext>().UseSqlite(c).Options);
    }
    public FaultQueriesTests() => _conn.Open();
    public void Dispose() => _conn.Dispose();

    private static readonly DateTime T0 = new(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_real_dropout_is_found_by_SQL_and_scored_while_a_scanner_outage_is_not()
    {
        var f = new Factory(_conn);
        await using (var db = f.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            var dev = new Device { Id = Guid.NewGuid(), AdvertisedName = "Lamp", Category = "Light", AssignedRoom = "A", FirstSeenUtc = T0, CreatedAt = T0, UpdatedAt = T0 };
            db.Devices.Add(dev);
            var scanner = Guid.NewGuid();
            // heard every 30 s for 6 h, except: 20 min dropout at +1h (scanner listening) and 60 min at +3h (scanner stopped meanwhile)
            for (var s = 0; s <= 6 * 3600; s += 30)
            {
                var t = T0.AddSeconds(s);
                if (t >= T0.AddHours(1) && t < T0.AddHours(1).AddMinutes(20)) continue;
                if (t >= T0.AddHours(3) && t < T0.AddHours(4)) continue;
                db.Observations.Add(new Observation { Id = Guid.NewGuid(), DeviceId = dev.Id, ScannerId = scanner, TimestampUtc = t, ProcessedAtUtc = t, Rssi = (short)(-60 + (s / 30 % 3)) });
            }
            db.ApplicationEvents.AddRange(
                new ApplicationEvent { Source = "Scanner", EventType = "Running", Message = "", TimestampUtc = T0 },
                new ApplicationEvent { Source = "Scanner", EventType = "Stopped", Message = "", TimestampUtc = T0.AddHours(3) },
                new ApplicationEvent { Source = "Scanner", EventType = "Running", Message = "", TimestampUtc = T0.AddHours(4) });
            await db.SaveChangesAsync();
        }

        var window = new TimeRange(T0, T0.AddHours(6));
        var settings = new AnalyzerSettings();
        var (devices, coverage) = await new FaultQueries(f).LoadAsync(window, settings, default);
        Assert.Single(devices);
        Assert.Equal(2, coverage.Count);
        Assert.Equal(TimeSpan.FromHours(5), TimeSpan.FromTicks(coverage.Sum(c => c.Length.Ticks)));

        var r = FaultAnalyzer.Analyze(devices, coverage, window, settings).Devices.Single();
        Assert.Equal(1, r.Interruptions);                         // only the dropout that happened while the scanner listened
        Assert.InRange(r.MissingMinutes, 19, 22);
        Assert.NotNull(r.Health);
    }
}
