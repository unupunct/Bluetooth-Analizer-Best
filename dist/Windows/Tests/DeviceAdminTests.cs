using BluetoothFleetMonitor.Application.Abstractions;
using BluetoothFleetMonitor.Application.Csv;
using BluetoothFleetMonitor.Application.Scanning;
using BluetoothFleetMonitor.Database;
using BluetoothFleetMonitor.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BluetoothFleetMonitor.Tests;

public class CsvTableTests
{
    [Fact]
    public void Round_trips_commas_quotes_and_line_breaks()
    {
        var rows = new List<IReadOnlyList<string?>> { new[] { "a,b", "he said \"hi\"", "line1\nline2", "" } };
        var parsed = CsvTable.Parse(CsvTable.Write(rows));
        Assert.Equal(new[] { "a,b", "he said \"hi\"", "line1\nline2", "" }, parsed.Single());
    }

    [Fact]
    public void Spreadsheet_formulas_are_neutralised_on_export()
    {
        var text = CsvTable.Write(new List<IReadOnlyList<string?>> { new[] { "=HYPERLINK(\"http://x\")", "+1+1", "@SUM(A1)", "-5", "-cmd" } });
        var cells = CsvTable.Parse(text).Single();
        Assert.StartsWith("'=", cells[0]);
        Assert.StartsWith("'+", cells[1]);
        Assert.StartsWith("'@", cells[2]);
        Assert.Equal("-5", cells[3]);       // a plain negative number stays a number
        Assert.StartsWith("'-", cells[4]);
    }
}

public sealed class DeviceAdminTests : IDisposable
{
    private sealed class Factory(SqliteConnection conn) : IDbContextFactory<FleetDbContext>
    {
        public FleetDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<FleetDbContext>().UseSqlite(conn).Options);
    }

    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly Factory _factory;
    private readonly EfFleetStore _store;
    private readonly DeviceAdminService _admin;
    private readonly Guid _scanner = Guid.NewGuid();
    private static readonly DateTime T0 = new(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);

    public DeviceAdminTests()
    {
        _conn.Open();
        _factory = new Factory(_conn);
        using (var db = _factory.CreateDbContext()) db.Database.Migrate();
        _store = new EfFleetStore(_factory);
        _admin = new DeviceAdminService(_factory);
    }

    public void Dispose() => _conn.Dispose();

    private async Task<Guid> SeedAsync(string address, params (int sec, short rssi)[] points)
    {
        var dev = Guid.NewGuid();
        var batch = points.Select((p, i) => new ProcessedObservation(
            new BleAdvertisement(Guid.NewGuid(), T0.AddSeconds(p.sec), Convert.ToUInt64(address, 16), AddressKind.Public, p.rssi, "LVS-Lush",
                Array.Empty<ManufacturerBlock>(), Array.Empty<Guid>(), Array.Empty<byte>()),
            dev, i == 0, i == 0 ? new[] { new IdentifierRecord(IdentifierTypes.StableAddress, address, 1.0) } : Array.Empty<IdentifierRecord>(),
            T0.AddMinutes(5))).ToList();
        await _store.SaveBatchAsync(_scanner, batch);
        return dev;
    }

    [Fact]
    public async Task Details_can_be_edited_and_persist()
    {
        var dev = await SeedAsync("AABBCCDDEE01", (0, -60));

        await _admin.UpdateDetailsAsync(dev, new DeviceDetails("Lush - Studio 01", "Toy", "Studio 01", "WS-3", "needs new battery"));

        await using var db = _factory.CreateDbContext();
        var d = await db.Devices.SingleAsync();
        Assert.Equal("Lush - Studio 01", d.CustomName);
        Assert.Equal("Toy", d.Category);
        Assert.Equal("Studio 01", d.AssignedRoom);
        Assert.Equal("Lush - Studio 01", d.DisplayName);
    }

    [Fact]
    public async Task Csv_export_then_import_updates_editable_fields_by_id()
    {
        var dev = await SeedAsync("AABBCCDDEE01", (0, -60));
        var csv = await _admin.ExportCsvAsync();
        var edited = csv.Replace("LVS-Lush,Unknown,", "LVS-Lush,Unknown,"); // header/shape check below
        var rows = CsvTable.Parse(csv);
        var header = rows[0];
        var data = rows[1];
        data[Array.IndexOf(header, "CustomName")] = "Lush 07";
        data[Array.IndexOf(header, "Room")] = "Studio 02";

        var result = await _admin.ImportCsvAsync(CsvTable.Write(new[] { (IReadOnlyList<string?>)header, data }));

        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Unmatched);
        await using var db = _factory.CreateDbContext();
        var d = await db.Devices.SingleAsync();
        Assert.Equal("Lush 07", d.CustomName);
        Assert.Equal("Studio 02", d.AssignedRoom);
        Assert.Equal(dev, d.Id);
    }

    [Fact]
    public async Task Csv_import_matches_by_stable_address_when_id_is_missing_and_reports_unmatched_rows()
    {
        await SeedAsync("AABBCCDDEE01", (0, -60));
        var csv = "Address,CustomName\r\nAABBCCDDEE01,Matched\r\n999999999999,Nobody\r\n";

        var result = await _admin.ImportCsvAsync(csv);

        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.Unmatched);
        await using var db = _factory.CreateDbContext();
        Assert.Equal("Matched", (await db.Devices.SingleAsync()).CustomName);
    }

    [Fact]
    public async Task Csv_import_rejects_a_file_with_no_id_or_address_column()
    {
        var result = await _admin.ImportCsvAsync("CustomName\r\nX\r\n");
        Assert.Equal(0, result.Updated);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task Merge_moves_identifiers_and_observations_recomputes_stats_and_writes_an_audit_event()
    {
        var keep = await SeedAsync("AAAAAAAAAAAA", (0, -60), (10, -70));
        var absorb = await SeedAsync("BBBBBBBBBBBB", (5, -50));

        await _admin.MergeAsync(sourceId: absorb, targetId: keep, reason: "same toy, address changed");

        await using var db = _factory.CreateDbContext();
        var d = await db.Devices.Include(x => x.Identifiers).SingleAsync();
        Assert.Equal(keep, d.Id);
        Assert.Equal(2, d.Identifiers.Count);
        Assert.Equal(3, d.ObservationCount);
        Assert.Equal((short)-70, d.MinRssi);
        Assert.Equal((short)-50, d.MaxRssi);
        Assert.Equal(T0.AddSeconds(10), d.LastSeenUtc);
        Assert.Equal((short)-70, d.LastRssi);
        Assert.Equal(3, await db.Observations.CountAsync(o => o.DeviceId == keep));
        var audit = await db.ApplicationEvents.SingleAsync(e => e.EventType == "DeviceMerged");
        Assert.Contains(absorb.ToString(), audit.Message);
        Assert.Contains("BBBBBBBBBBBB", audit.Message);
    }

    [Fact]
    public async Task Merging_a_device_into_itself_is_refused()
    {
        var a = await SeedAsync("AAAAAAAAAAAA", (0, -60));
        await Assert.ThrowsAsync<ArgumentException>(() => _admin.MergeAsync(a, a, null));
    }

    [Fact]
    public async Task Split_creates_a_new_device_with_the_chosen_identifier_and_its_observations()
    {
        var keep = await SeedAsync("AAAAAAAAAAAA", (0, -60));
        var absorb = await SeedAsync("BBBBBBBBBBBB", (5, -50));
        await _admin.MergeAsync(absorb, keep, null);

        await using (var db = _factory.CreateDbContext())
        {
            var idToSplit = await db.DeviceIdentifiers.SingleAsync(i => i.IdentifierValue == "BBBBBBBBBBBB");
            var newId = await _admin.SplitAsync(keep, new[] { idToSplit.Id }, "merged by mistake");

            await using var db2 = _factory.CreateDbContext();
            Assert.Equal(2, await db2.Devices.CountAsync());
            var split = await db2.Devices.Include(x => x.Identifiers).SingleAsync(x => x.Id == newId);
            Assert.Equal("BBBBBBBBBBBB", Assert.Single(split.Identifiers).IdentifierValue);
            Assert.Equal(1, split.ObservationCount);
            Assert.Equal(1, (await db2.Devices.SingleAsync(x => x.Id == keep)).ObservationCount);
            Assert.Single(await db2.ApplicationEvents.Where(e => e.EventType == "DeviceSplit").ToListAsync());
        }
    }

    [Fact]
    public async Task History_is_downsampled_into_buckets_with_min_max_avg()
    {
        var dev = await SeedAsync("AAAAAAAAAAAA", (0, -60), (1, -70), (2, -80), (70, -50));
        var q = new HistoryQueries(_factory);

        var points = await q.GetRssiHistoryAsync(dev, T0.AddMinutes(-1), T0.AddMinutes(10), bucketSeconds: 60);

        Assert.Equal(2, points.Count);
        Assert.Equal(3, points[0].Count);
        Assert.Equal(-70.0, points[0].Average);
        Assert.Equal(-80, points[0].Min);
        Assert.Equal(-60, points[0].Max);
        Assert.Equal(1, points[1].Count);
    }

    [Fact]
    public async Task History_summary_reports_the_longest_silent_interval_between_observations()
    {
        var dev = await SeedAsync("AAAAAAAAAAAA", (0, -60), (2, -60), (100, -60), (101, -60));
        var q = new HistoryQueries(_factory);

        var s = await q.GetSummaryAsync(dev, T0.AddMinutes(-1), T0.AddMinutes(10));

        Assert.Equal(4, s.Count);
        Assert.Equal(TimeSpan.FromSeconds(98), s.LongestGap);
    }
}
