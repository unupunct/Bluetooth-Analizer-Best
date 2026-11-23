using BluetoothFleetMonitor.Application.Diagnostics;
using BluetoothFleetMonitor.Database;
using BluetoothFleetMonitor.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BluetoothFleetMonitor.Tests;

public sealed class SignalQueriesTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private sealed class Factory(SqliteConnection c) : IDbContextFactory<FleetDbContext>
    {
        public FleetDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FleetDbContext>().UseSqlite(c).Options);
    }
    public SignalQueriesTests() => _conn.Open();
    public void Dispose() => _conn.Dispose();

    private static readonly DateTime T0 = new(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc);
    private readonly Guid _scannerA = Guid.NewGuid(), _scannerB = Guid.NewGuid();
    private Guid _deviceId;

    private async Task<(Factory Factory, SignalQueries Signal, FaultQueries Faults)> SeedAsync()
    {
        var f = new Factory(_conn);
        await using (var db = f.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            var dev = new Device { Id = Guid.NewGuid(), AdvertisedName = "Lamp", Category = "Light", AssignedRoom = "A", FirstSeenUtc = T0, CreatedAt = T0, UpdatedAt = T0 };
            _deviceId = dev.Id;
            db.Devices.Add(dev);
            db.Scanners.AddRange(new Scanner { Id = _scannerA, Name = "PC radio", Platform = "Windows", CreatedAt = T0 }, new Scanner { Id = _scannerB, Name = "Phone", Platform = "Android", CreatedAt = T0 });
            // scanner A hears the device every 30 s for 6 h, except a 20-minute dropout at +1h and a 60-minute hole at +3h (while the scanner was stopped)
            for (var s = 0; s <= 6 * 3600; s += 30)
            {
                var t = T0.AddSeconds(s);
                if (t >= T0.AddHours(1) && t < T0.AddHours(1).AddMinutes(20)) continue;
                if (t >= T0.AddHours(3) && t < T0.AddHours(4)) continue;
                db.Observations.Add(new Observation { Id = Guid.NewGuid(), DeviceId = dev.Id, ScannerId = _scannerA, TimestampUtc = t, ProcessedAtUtc = t, Rssi = (short)(-60 + (s / 30 % 3)) });
            }
            // scanner B (a phone) adds a handful of windows
            for (var m = 0; m < 5; m++)
                db.Observations.Add(new Observation { Id = Guid.NewGuid(), DeviceId = dev.Id, ScannerId = _scannerB, TimestampUtc = T0.AddMinutes(30 + m), ProcessedAtUtc = T0, Rssi = -75 });
            db.ApplicationEvents.AddRange(
                new ApplicationEvent { Source = "Scanner", EventType = "Running", Message = "", TimestampUtc = T0 },
                new ApplicationEvent { Source = "Scanner", EventType = "Stopped", Message = "", TimestampUtc = T0.AddHours(3) },
                new ApplicationEvent { Source = "Scanner", EventType = "Running", Message = "", TimestampUtc = T0.AddHours(4) });
            await db.SaveChangesAsync();
        }
        var faults = new FaultQueries(f);
        return (f, new SignalQueries(f, faults), faults);
    }

    [Fact]
    public async Task Buckets_are_60_seconds_for_a_day_window_and_900_for_7_days()
    {
        var (_, q, _) = await SeedAsync();
        var day = await q.LoadAsync(_deviceId, new TimeRange(T0, T0.AddHours(6)), null);
        Assert.Equal(60, day.BucketSeconds);
        Assert.InRange(day.Buckets.Count, 250, 360);               // about one bucket per minute that has data
        var week = await q.LoadAsync(_deviceId, new TimeRange(T0, T0.AddDays(7)), null);
        Assert.Equal(900, week.BucketSeconds);
        Assert.InRange(week.Buckets.Count, 15, 24);                // 15-minute buckets over 5 h of data
    }

    [Fact]
    public async Task Interval_histogram_counts_whole_second_gaps()
    {
        var (_, q, _) = await SeedAsync();
        var input = await q.LoadAsync(_deviceId, new TimeRange(T0, T0.AddHours(6)), _scannerA);
        Assert.InRange(input.IntervalSecondCounts[30], 540, 600);      // 720 half-minutes minus the 20- and 60-minute holes
        Assert.Equal(2, input.IntervalSecondCounts[600]);             // the 20-minute dropout and the 60-minute hole both land in the 600+ bin
        Assert.InRange(input.MaxIntervalSeconds, 3620, 3640);   // last advert before the hole to the first after it: 60 min + one 30 s step
    }

    [Fact]
    public async Task Silences_are_cut_to_scanner_coverage()
    {
        var (_, q, _) = await SeedAsync();
        var input = await q.LoadAsync(_deviceId, new TimeRange(T0, T0.AddHours(6)), _scannerA);
        Assert.Single(input.Silences);                                 // the hole at +3h happened while the scanner was stopped
        Assert.InRange(input.Silences[0].Length.TotalMinutes, 19, 21);
    }

    [Fact]
    public async Task Scanner_filter_only_counts_that_scanners_observations()
    {
        var (_, q, _) = await SeedAsync();
        var window = new TimeRange(T0, T0.AddHours(6));
        var phoneOnly = await q.LoadAsync(_deviceId, window, _scannerB);
        Assert.Equal(5, phoneOnly.Buckets.Sum(b => b.Count));
        var all = await q.LoadAsync(_deviceId, window, null);
        Assert.True(all.Buckets.Sum(b => b.Count) > 500);
        var choices = await q.ScannersAsync(_deviceId, window);
        Assert.Null(choices[0].Id);
        Assert.Equal("All scanners", choices[0].Name);
        Assert.Contains(choices, c => c.Id == _scannerB && c.Observations == 5);
        Assert.Contains(choices, c => c.Id == _scannerA && c.Observations > 500);
    }

    [Fact]
    public async Task Unknown_device_or_empty_window_returns_empty_input_not_an_exception()
    {
        var (_, q, _) = await SeedAsync();
        var none = await q.LoadAsync(Guid.NewGuid(), new TimeRange(T0, T0.AddHours(6)), null);
        Assert.Empty(none.Buckets); Assert.Empty(none.IntervalSecondCounts); Assert.Empty(none.Silences);
        var late = await q.LoadAsync(_deviceId, new TimeRange(T0.AddDays(10), T0.AddDays(11)), null);
        Assert.Empty(late.Buckets);
        Assert.False(SignalAnalyzer.Analyze(late).Sufficient);
    }

    [Fact]
    public async Task A_scanner_that_never_heard_the_device_gives_an_empty_input()
    {
        var (_, q, _) = await SeedAsync();
        var input = await q.LoadAsync(_deviceId, new TimeRange(T0, T0.AddHours(6)), Guid.NewGuid());
        Assert.Empty(input.Buckets);
    }

    [Fact]
    public async Task DiagnoseAsync_matches_the_fleet_analysis_row_for_the_same_device()
    {
        var (_, _, faults) = await SeedAsync();
        var window = new TimeRange(T0, T0.AddHours(6)); var settings = new AnalyzerSettings();
        var (devices, coverage) = await faults.LoadAsync(window, settings);
        var expected = FaultAnalyzer.Analyze(devices, coverage, window, settings).Devices.Single();
        var d = await faults.DiagnoseAsync(_deviceId, window, settings);
        Assert.NotNull(d);
        Assert.Equal(expected.Health, d!.Health);
        Assert.Equal(expected.Interruptions, d.Interruptions);
        Assert.Null(await faults.DiagnoseAsync(Guid.NewGuid(), window, settings));
    }

    [Fact]
    public void CutToCoverage_keeps_only_covered_pieces_at_least_the_minimum_gap_and_after_first_seen()
    {
        var window = new TimeRange(T0, T0.AddHours(10));
        var coverage = new[] { new TimeRange(T0, T0.AddHours(3)), new TimeRange(T0.AddHours(5), T0.AddHours(10)) };
        var silences = new[] { new TimeRange(T0.AddHours(2), T0.AddHours(6)) };        // spans the scanner outage 3h-5h
        var cut = FaultAnalyzer.CutToCoverage(silences, coverage, window, null, 120);
        Assert.Equal(2, cut.Count);                                                    // 2h-3h and 5h-6h
        Assert.Equal(TimeSpan.FromHours(1), cut[0].Length);
        var afterFirst = FaultAnalyzer.CutToCoverage(silences, coverage, window, T0.AddHours(2).AddMinutes(30), 120);
        Assert.Equal(TimeSpan.FromMinutes(30), afterFirst[0].Length);                  // silence before the device existed is dropped
        Assert.Empty(FaultAnalyzer.CutToCoverage(new[] { new TimeRange(T0, T0.AddSeconds(60)) }, coverage, window, null, 120));
    }
}
