using BluetoothFleetMonitor.Application.Abstractions;
using BluetoothFleetMonitor.Application.Scanning;
using BluetoothFleetMonitor.Database;
using BluetoothFleetMonitor.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BluetoothFleetMonitor.Tests;

public sealed class StoreTests : IDisposable
{
    private sealed class Factory(SqliteConnection conn) : IDbContextFactory<FleetDbContext>
    {
        public FleetDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<FleetDbContext>().UseSqlite(conn).Options);
    }

    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly Factory _factory;
    private readonly EfFleetStore _store;
    private readonly Guid _scanner = Guid.NewGuid();

    public StoreTests()
    {
        _conn.Open();
        _factory = new Factory(_conn);
        using var db = _factory.CreateDbContext();
        db.Database.Migrate();
        _store = new EfFleetStore(_factory);
    }

    public void Dispose() => _conn.Dispose();

    private static ProcessedObservation Obs(Guid deviceId, DateTime observed, short rssi, bool isNew = false,
        string? name = "LVS-Lush", Guid? id = null, params IdentifierRecord[] ids) =>
        new(new BleAdvertisement(id ?? Guid.NewGuid(), observed, 0xAABBCCDDEE01, AddressKind.Public, rssi, name,
                new[] { new ManufacturerBlock(0x004C, new byte[] { 1, 2, 3 }) }, Array.Empty<Guid>(), new byte[] { 9 }),
            deviceId, isNew, ids, DateTime.UtcNow);

    private static readonly DateTime T0 = new(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task New_device_is_created_with_its_identifiers_and_first_statistics()
    {
        var dev = Guid.NewGuid();
        var stored = await _store.SaveBatchAsync(_scanner, new[]
        {
            Obs(dev, T0, -60, isNew: true, ids: new[] { new IdentifierRecord("StableAddress", "AABBCCDDEE01", 1.0) }),
        });

        Assert.Equal(1, stored);
        await using var db = _factory.CreateDbContext();
        var d = await db.Devices.Include(x => x.Identifiers).SingleAsync();
        Assert.Equal(dev, d.Id);
        Assert.Equal("LVS-Lush", d.AdvertisedName);
        Assert.Equal(T0, d.FirstSeenUtc);
        Assert.Equal(T0, d.LastSeenUtc);
        Assert.Equal((short)-60, d.LastRssi);
        Assert.Equal(1, d.ObservationCount);
        Assert.Equal("Apple", d.Manufacturer);
        Assert.Single(d.Identifiers, i => i.IdentifierValue == "AABBCCDDEE01" && i.Confidence == 1.0);
    }

    [Fact]
    public async Task Duplicate_observation_id_is_stored_once_and_not_counted_twice()
    {
        var dev = Guid.NewGuid();
        var id = Guid.NewGuid();
        var batch = new[] { Obs(dev, T0, -60, isNew: true, id: id) };

        Assert.Equal(1, await _store.SaveBatchAsync(_scanner, batch));
        Assert.Equal(0, await _store.SaveBatchAsync(_scanner, batch));

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.Observations.CountAsync());
        Assert.Equal(1, (await db.Devices.SingleAsync()).ObservationCount);
    }

    [Fact]
    public async Task Duplicate_inside_one_batch_is_stored_once()
    {
        var dev = Guid.NewGuid();
        var id = Guid.NewGuid();
        var stored = await _store.SaveBatchAsync(_scanner, new[]
        {
            Obs(dev, T0, -60, isNew: true, id: id),
            Obs(dev, T0, -60, id: id),
        });
        Assert.Equal(1, stored);
    }

    [Fact]
    public async Task Stale_observation_does_not_overwrite_current_state()
    {
        var dev = Guid.NewGuid();
        await _store.SaveBatchAsync(_scanner, new[] { Obs(dev, T0.AddMinutes(10), -50, isNew: true) });
        // A phone uploads something it heard earlier, after the server already saw the device more recently.
        await _store.SaveBatchAsync(_scanner, new[] { Obs(dev, T0, -90) });

        await using var db = _factory.CreateDbContext();
        var d = await db.Devices.SingleAsync();
        Assert.Equal(T0.AddMinutes(10), d.LastSeenUtc);
        Assert.Equal((short)-50, d.LastRssi);
        Assert.Equal(T0, d.FirstSeenUtc);        // history is still extended backwards
        Assert.Equal((short)-90, d.MinRssi);
        Assert.Equal(2, d.ObservationCount);
        Assert.Equal(2, await db.Observations.CountAsync());
    }

    [Fact]
    public async Task Rssi_statistics_track_min_max_and_average()
    {
        var dev = Guid.NewGuid();
        await _store.SaveBatchAsync(_scanner, new[]
        {
            Obs(dev, T0, -60, isNew: true),
            Obs(dev, T0.AddSeconds(1), -80),
            Obs(dev, T0.AddSeconds(2), -70),
        });

        await using var db = _factory.CreateDbContext();
        var d = await db.Devices.SingleAsync();
        Assert.Equal((short)-80, d.MinRssi);
        Assert.Equal((short)-60, d.MaxRssi);
        Assert.Equal(-70.0, d.AverageRssi);
        Assert.Equal(3, d.ObservationCount);
    }

    [Fact]
    public async Task Observation_keeps_original_time_and_records_the_processing_time_separately()
    {
        var dev = Guid.NewGuid();
        var processed = new DateTime(2026, 11, 21, 8, 0, 0, DateTimeKind.Utc);
        var o = Obs(dev, T0, -60, isNew: true) with { ProcessedAtUtc = processed };
        await _store.SaveBatchAsync(_scanner, new[] { o });

        await using var db = _factory.CreateDbContext();
        var row = await db.Observations.SingleAsync();
        Assert.Equal(T0, row.TimestampUtc);
        Assert.Equal(processed, row.ProcessedAtUtc);
        Assert.Null(row.UploadedAtUtc);
        Assert.Equal(_scanner, row.ScannerId);
    }

    [Fact]
    public async Task LoadKnownDevices_returns_devices_with_identifiers()
    {
        var dev = Guid.NewGuid();
        await _store.SaveBatchAsync(_scanner, new[]
        {
            Obs(dev, T0, -60, isNew: true, ids: new[] { new IdentifierRecord("StableAddress", "AABBCCDDEE01", 1.0) }),
        });

        var known = await _store.LoadKnownDevicesAsync();

        var k = Assert.Single(known);
        Assert.Equal(dev, k.DeviceId);
        Assert.Contains(k.Identifiers, i => i.Type == "StableAddress" && i.Value == "AABBCCDDEE01");
    }

    [Fact]
    public async Task Events_and_scanner_are_persisted()
    {
        await _store.EnsureScannerAsync(new Scanner { Id = _scanner, Name = "This PC", Platform = "Windows", CreatedAt = T0 });
        await _store.EnsureScannerAsync(new Scanner { Id = _scanner, Name = "This PC", Platform = "Windows", CreatedAt = T0, Status = "Running" });
        await _store.RecordEventAsync(new ApplicationEvent { Source = "t", EventType = "X", Message = "m", Severity = Severity.Info, TimestampUtc = T0 });

        await using var db = _factory.CreateDbContext();
        Assert.Equal("Running", (await db.Scanners.SingleAsync()).Status);
        Assert.Equal("m", (await db.ApplicationEvents.SingleAsync()).Message);
    }
}
