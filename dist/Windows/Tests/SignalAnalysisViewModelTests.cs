using BluetoothFleetMonitor.Database;
using BluetoothFleetMonitor.Desktop.Services;
using BluetoothFleetMonitor.Desktop.ViewModels;
using BluetoothFleetMonitor.Domain;
using Microsoft.EntityFrameworkCore;

namespace BluetoothFleetMonitor.Tests;

/// <summary>The page's view-model against a real (file) SQLite database, so overlapping loads really overlap.</summary>
public sealed class SignalAnalysisViewModelTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bfm-vm-" + Guid.NewGuid().ToString("N"));
    private sealed class Factory(string path) : IDbContextFactory<FleetDbContext>
    {
        public FleetDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FleetDbContext>().UseSqlite($"Data Source={path}").Options);
    }

    private Factory _factory = null!;
    private SignalAnalysisViewModel _vm = null!;
    private readonly Guid _a = Guid.NewGuid(), _b = Guid.NewGuid(), _pc = Guid.NewGuid(), _phone = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _factory = new Factory(Path.Combine(_dir, "t.db"));
        var now = DateTime.UtcNow;
        await using (var db = _factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            foreach (var (id, name) in new[] { (_a, "Lamp A"), (_b, "Lamp B") })
                db.Devices.Add(new Device { Id = id, AdvertisedName = name, Category = "Light", FirstSeenUtc = now.AddDays(-5), CreatedAt = now, UpdatedAt = now, ObservationCount = 100 });
            db.Scanners.AddRange(new Scanner { Id = _pc, Name = "PC radio", Platform = "Windows", CreatedAt = now }, new Scanner { Id = _phone, Name = "Phone", Platform = "Android", CreatedAt = now });
            foreach (var id in new[] { _a, _b })
                for (var s = 0; s < 30 * 60; s += 30)               // the PC radio heard both devices every 30 s for the last 30 minutes
                    db.Observations.Add(new Observation { Id = Guid.NewGuid(), DeviceId = id, ScannerId = _pc, TimestampUtc = now.AddSeconds(-s), ProcessedAtUtc = now, Rssi = -60, Source = "Windows" });
            for (var m = 0; m < 5; m++)                              // the phone heard device A only, three hours ago
                db.Observations.Add(new Observation { Id = Guid.NewGuid(), DeviceId = _a, ScannerId = _phone, TimestampUtc = now.AddHours(-3).AddMinutes(m), ProcessedAtUtc = now, Rssi = -75, Source = "Android" });
            await db.SaveChangesAsync();
        }
        var faults = new FaultQueries(_factory);
        var selection = new SelectionService();
        var faultsVm = new FaultsViewModel(faults, selection, new AppPaths(_dir));
        _vm = new SignalAnalysisViewModel(new SignalQueries(_factory, faults), faults, faultsVm, selection);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Opening_another_device_while_a_load_is_running_ends_up_showing_the_newest_device()
    {
        var first = _vm.ShowAsync(_a);
        var second = _vm.ShowAsync(_b);
        await Task.WhenAll(first, second);
        Assert.Equal("Lamp B", _vm.Title);
    }

    [Fact]
    public async Task A_scanner_that_is_not_in_the_new_window_falls_back_to_all_scanners_with_all_scanners_data()
    {
        _vm.Window = "Last 24 hours";
        await _vm.ShowAsync(_a);
        _vm.SelectedScanner = _vm.Scanners.Single(s => s.Name == "Phone");
        await _vm.RefreshAsync();
        Assert.Equal(5, _vm.Scanners.Single(s => s.Name == "Phone").Observations);

        _vm.Window = "Last hour";                                    // the phone never heard it in this window
        await _vm.RefreshAsync();
        Assert.Null(_vm.SelectedScanner!.Id);                        // the picker says All scanners ...
        Assert.NotEmpty(_vm.StatLines);                              // ... and the numbers really are all scanners' numbers
    }
}
