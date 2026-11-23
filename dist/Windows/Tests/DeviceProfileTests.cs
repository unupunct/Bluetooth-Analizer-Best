using System.Text.RegularExpressions;
using BluetoothFleetMonitor.Application.Diagnostics;
using BluetoothFleetMonitor.Database;
using BluetoothFleetMonitor.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BluetoothFleetMonitor.Tests;

public sealed class DeviceProfileTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    public DeviceProfileTests() => _conn.Open();
    public void Dispose() => _conn.Dispose();
    private sealed class Factory(SqliteConnection c) : IDbContextFactory<FleetDbContext>
    {
        public FleetDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FleetDbContext>().UseSqlite(c).Options);
    }

    private static DeviceProfileInput Input(string? mfr = null, string? services = null, AddressKind kind = AddressKind.Public, bool stable = true, bool fp = false,
        string? advertised = "Lamp", IReadOnlyDictionary<string, long>? bySource = null)
        => new("Lamp 1", advertised, "Light", "Studio", "WS-01", mfr, services, kind, stable, fp,
            new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc), 140,
            bySource ?? new Dictionary<string, long> { ["Windows"] = 100, ["Android"] = 40 });

    private static string Line(DeviceProfile p, string label) => p.Lines.Single(l => l.Label == label).Value;

    [Fact]
    public void Maker_is_taken_from_the_first_manufacturer_block()
    {
        Assert.Equal("Nordic Semiconductor", DeviceProfileBuilder.Build(Input(mfr: "0059:0102030405")).Maker);
        Assert.Equal("Company 0xFFFF", DeviceProfileBuilder.Build(Input(mfr: "FFFF:00")).Maker);
        var none = DeviceProfileBuilder.Build(Input(mfr: null));
        Assert.Null(none.Maker);
        Assert.Contains("Unknown", Line(none, "Maker"));
    }

    [Fact]
    public void Services_are_named_when_known_and_raw_when_not()
    {
        var p = DeviceProfileBuilder.Build(Input(services: "0000180f-0000-1000-8000-00805f9b34fb;12345678-1234-1234-1234-123456789abc"));
        var v = Line(p, "Services");
        Assert.Contains("Battery", v);
        Assert.Contains("12345678-1234-1234-1234-123456789abc", v);
        Assert.Contains("None announced", Line(DeviceProfileBuilder.Build(Input(services: null)), "Services"));
    }

    [Fact]
    public void Address_kind_and_identity_are_explained_without_guessing()
    {
        Assert.Contains("unknown", Line(DeviceProfileBuilder.Build(Input(kind: AddressKind.Unknown, stable: false)), "Address").ToLowerInvariant());
        var stable = DeviceProfileBuilder.Build(Input(kind: AddressKind.Public, stable: true));
        Assert.Contains("stable", Line(stable, "Address").ToLowerInvariant());
        Assert.Contains("stable address", Line(stable, "Identity").ToLowerInvariant());
        Assert.Contains("fingerprint", Line(DeviceProfileBuilder.Build(Input(kind: AddressKind.RandomResolvablePrivate, stable: false, fp: true)), "Identity").ToLowerInvariant());
    }

    [Fact]
    public void Counts_per_source_are_listed()
    {
        var p = DeviceProfileBuilder.Build(Input());
        Assert.Contains(p.Lines, l => l.Value.Contains("100") && l.Label.Contains("Windows"));
        Assert.Contains(p.Lines, l => l.Value.Contains("40") && l.Label.Contains("Android"));
    }

    [Fact]
    public void LookupQuery_uses_maker_and_name_and_never_the_address()
    {
        Assert.Equal("Logitech MX Keys bluetooth", LookupLink.Query("Logitech", "MX Keys"));
        Assert.Equal("Logitech bluetooth", LookupLink.Query("Logitech", "Device a1b2c3d4"));
        Assert.Equal("Logitech bluetooth", LookupLink.Query("Logitech", "  "));
        Assert.Equal("Logitech bluetooth", LookupLink.Query("Logitech", "AA:BB:CC:DD:EE:FF"));
        Assert.Null(LookupLink.Query(null, null));
        Assert.Null(LookupLink.Query("Company 0xFFFF", "Device a1b2c3d4"));
        Assert.DoesNotMatch(new Regex("[0-9A-Fa-f]{12}"), LookupLink.Query("Logitech", "Keys AABBCCDDEEFF") ?? "");
    }

    [Fact]
    public void Url_is_encoded()
    {
        var url = LookupLink.Url("Acme & Co #1 \"x\" é bluetooth");
        Assert.StartsWith("https://duckduckgo.com/?q=", url);
        Assert.Contains("%26", url); Assert.Contains("%23", url); Assert.Contains("%22", url);
        Assert.DoesNotContain(" ", url);
        Assert.DoesNotContain("&Co", url.Replace("%26", ""));
    }

    [Fact]
    public async Task LoadProfileAsync_reads_the_device_and_its_per_source_counts()
    {
        var f = new Factory(_conn); var t0 = new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc); var id = Guid.NewGuid(); var sc = Guid.NewGuid();
        await using (var db = f.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            db.Devices.Add(new Device { Id = id, AdvertisedName = "Lamp", Category = "Light", AssignedRoom = "A", FirstSeenUtc = t0, LastSeenUtc = t0.AddHours(1), ObservationCount = 3, CreatedAt = t0, UpdatedAt = t0 });
            db.DeviceIdentifiers.Add(new DeviceIdentifier { DeviceId = id, IdentifierType = IdentifierTypes.StableAddress, IdentifierValue = "AABBCCDDEEFF", FirstSeen = t0, LastSeen = t0 });
            db.Observations.AddRange(
                new Observation { Id = Guid.NewGuid(), DeviceId = id, ScannerId = sc, TimestampUtc = t0, ProcessedAtUtc = t0, Rssi = -60, Source = "Windows", AddressKind = AddressKind.Public, ManufacturerData = "0059:0102" },
                new Observation { Id = Guid.NewGuid(), DeviceId = id, ScannerId = sc, TimestampUtc = t0.AddMinutes(1), ProcessedAtUtc = t0, Rssi = -61, Source = "Windows", AddressKind = AddressKind.Public, ManufacturerData = "0059:0102", ServiceUuids = "0000180f-0000-1000-8000-00805f9b34fb" },
                new Observation { Id = Guid.NewGuid(), DeviceId = id, ScannerId = sc, TimestampUtc = t0.AddMinutes(2), ProcessedAtUtc = t0, Rssi = -70, Source = "Android", AddressKind = AddressKind.Unknown });
            await db.SaveChangesAsync();
        }
        var q = new SignalQueries(f, new FaultQueries(f));
        var p = await q.LoadProfileAsync(id);
        Assert.NotNull(p);
        Assert.Equal(2, p!.ObservationsBySource["Windows"]);
        Assert.Equal(1, p.ObservationsBySource["Android"]);
        Assert.True(p.HasStableAddress);
        Assert.Equal("0059:0102", p.ManufacturerData);                   // newest observation that carries manufacturer data
        Assert.Contains("180f", p.ServiceUuids);
        Assert.Null(await q.LoadProfileAsync(Guid.NewGuid()));
    }
}
