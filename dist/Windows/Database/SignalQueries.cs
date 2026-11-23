using BluetoothFleetMonitor.Application.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace BluetoothFleetMonitor.Database;

/// <summary>One entry of the scanner picker; <see cref="Id"/> null means all scanners.</summary>
public sealed record ScannerChoice(Guid? Id, string Name, long Observations, string? Platform = null);

/// <summary>
/// Loads what the Signal Analysis page needs for one device. All aggregation (buckets, interval histogram, gaps) is done in SQL, so a
/// month of 1 Hz data stays cheap. Interval statistics only make sense for one source, which is why a scanner can be chosen.
/// </summary>
public sealed class SignalQueries
{
    private const int MinGapSeconds = 120;
    private readonly IDbContextFactory<FleetDbContext> _factory;
    private readonly FaultQueries _faults;

    public SignalQueries(IDbContextFactory<FleetDbContext> factory, FaultQueries faults) { _factory = factory; _faults = faults; }

    private sealed class BucketRow { public long Bucket { get; set; } public double Avg { get; set; } public long Min { get; set; } public long Max { get; set; } public long Cnt { get; set; } }
    private sealed class IntervalRow { public long Sec { get; set; } public long Cnt { get; set; } }
    private sealed class MaxRow { public double? Gap { get; set; } }
    private sealed class GapRow { public DateTime StartTs { get; set; } public DateTime EndTs { get; set; } }
    private sealed class EdgeRow { public DateTime? First { get; set; } public DateTime? Last { get; set; } public long Cnt { get; set; } }
    private sealed class ScannerRow { public Guid ScannerId { get; set; } public long Cnt { get; set; } }

    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);

    public static int BucketSecondsFor(TimeRange window) => window.Length <= TimeSpan.FromHours(36) ? 60 : 900;

    public async Task<SignalInput> LoadAsync(Guid deviceId, TimeRange window, Guid? scannerId, CancellationToken ct = default)
    {
        var bucketSeconds = BucketSecondsFor(window);
        var from = window.StartUtc; var to = window.EndUtc;
        await using var db = await _factory.CreateDbContextAsync(ct);
        // The scanner filter is expressed as "ScannerId = x OR no filter" so one parameterised statement serves both cases.
        var all = scannerId is null; var sid = scannerId ?? Guid.Empty;

        var buckets = (await db.Database.SqlQuery<BucketRow>($@"
            SELECT CAST(strftime('%s', TimestampUtc) AS INTEGER) / {bucketSeconds} AS Bucket, AVG(Rssi) AS Avg, MIN(Rssi) AS Min, MAX(Rssi) AS Max, COUNT(*) AS Cnt
            FROM Observations WHERE DeviceId = {deviceId} AND TimestampUtc >= {from} AND TimestampUtc <= {to} AND ({all} OR ScannerId = {sid})
            GROUP BY Bucket ORDER BY Bucket").ToListAsync(ct))
            .Select(r => new SignalBucket(DateTime.UnixEpoch.AddSeconds(r.Bucket * bucketSeconds), r.Avg, (int)r.Min, (int)r.Max, (int)r.Cnt)).ToList();
        if (buckets.Count == 0) return new SignalInput(window, bucketSeconds, buckets, new Dictionary<int, int>(), 0, Array.Empty<TimeRange>());

        var gapsSql = db.Database.SqlQuery<IntervalRow>($@"
            SELECT MIN(CAST(ROUND(Gap) AS INTEGER), 600) AS Sec, COUNT(*) AS Cnt FROM (
              SELECT (julianday(TimestampUtc) - julianday(LAG(TimestampUtc) OVER (ORDER BY TimestampUtc))) * 86400.0 AS Gap
              FROM Observations WHERE DeviceId = {deviceId} AND TimestampUtc >= {from} AND TimestampUtc <= {to} AND ({all} OR ScannerId = {sid}))
            WHERE Gap IS NOT NULL GROUP BY Sec");
        var counts = (await gapsSql.ToListAsync(ct)).ToDictionary(r => (int)r.Sec, r => (int)r.Cnt);
        var max = await db.Database.SqlQuery<MaxRow>($@"
            SELECT MAX(Gap) AS Gap FROM (
              SELECT (julianday(TimestampUtc) - julianday(LAG(TimestampUtc) OVER (ORDER BY TimestampUtc))) * 86400.0 AS Gap
              FROM Observations WHERE DeviceId = {deviceId} AND TimestampUtc >= {from} AND TimestampUtc <= {to} AND ({all} OR ScannerId = {sid}))").SingleAsync(ct);

        // silences: the same rule the Fault Detector uses (gap over the minimum, plus the edges of the window), then cut to scanner coverage
        var silences = (await db.Database.SqlQuery<GapRow>($@"
            SELECT Prev AS StartTs, Ts AS EndTs FROM (
              SELECT TimestampUtc AS Ts, LAG(TimestampUtc) OVER (ORDER BY TimestampUtc) AS Prev
              FROM Observations WHERE DeviceId = {deviceId} AND TimestampUtc >= {from} AND TimestampUtc <= {to} AND ({all} OR ScannerId = {sid}))
            WHERE Prev IS NOT NULL AND (julianday(Ts) - julianday(Prev)) * 86400.0 > {(double)MinGapSeconds}").ToListAsync(ct))
            .Select(g => new TimeRange(Utc(g.StartTs), Utc(g.EndTs))).ToList();
        var edge = await db.Database.SqlQuery<EdgeRow>($@"
            SELECT MIN(TimestampUtc) AS First, MAX(TimestampUtc) AS Last, COUNT(*) AS Cnt FROM Observations
            WHERE DeviceId = {deviceId} AND TimestampUtc >= {from} AND TimestampUtc <= {to} AND ({all} OR ScannerId = {sid})").SingleAsync(ct);
        if (edge.First is { } first && (Utc(first) - from).TotalSeconds > MinGapSeconds) silences.Add(new TimeRange(from, Utc(first)));
        if (edge.Last is { } last && (to - Utc(last)).TotalSeconds > MinGapSeconds) silences.Add(new TimeRange(Utc(last), to));
        var firstSeen = await db.Devices.AsNoTracking().Where(d => d.Id == deviceId).Select(d => d.FirstSeenUtc).FirstOrDefaultAsync(ct);
        var coverage = await _faults.LoadCoverageAsync(window, ct);
        // Coverage is the PC radio's Running state. It says nothing about when a phone was listening (a phone samples in windows and may be off),
        // so for a phone scanner no silence list is produced rather than a wrong one.
        var phone = scannerId is { } chosen && await db.Scanners.AsNoTracking().Where(s => s.Id == chosen).Select(s => s.Platform).FirstOrDefaultAsync(ct) is { } p && p != "Windows";
        var cut = phone ? new List<TimeRange>() : FaultAnalyzer.CutToCoverage(silences.OrderBy(s => s.StartUtc).ToList(), coverage, window, firstSeen, MinGapSeconds).ToList();

        return new SignalInput(window, bucketSeconds, buckets, counts, max.Gap ?? 0, cut);
    }

    public async Task<IReadOnlyList<ScannerChoice>> ScannersAsync(Guid deviceId, TimeRange window, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var from = window.StartUtc; var to = window.EndUtc;
        var rows = await db.Database.SqlQuery<ScannerRow>($@"
            SELECT ScannerId, COUNT(*) AS Cnt FROM Observations WHERE DeviceId = {deviceId} AND TimestampUtc >= {from} AND TimestampUtc <= {to} GROUP BY ScannerId").ToListAsync(ct);
        var names = await db.Scanners.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s, ct);
        var list = new List<ScannerChoice> { new(null, "All scanners", rows.Sum(r => r.Cnt)) };
        list.AddRange(rows.OrderByDescending(r => r.Cnt).Select(r => new ScannerChoice(r.ScannerId, names.TryGetValue(r.ScannerId, out var n) ? n.Name : r.ScannerId.ToString("N")[..8], r.Cnt, names.TryGetValue(r.ScannerId, out var n2) ? n2.Platform : null)));
        return list;
    }

    private sealed class SourceRow { public string Source { get; set; } = ""; public long Cnt { get; set; } }

    /// <summary>The facts for the "what is this device" panel; null when there is no such device.</summary>
    public async Task<DeviceProfileInput?> LoadProfileAsync(Guid deviceId, CancellationToken ct = default)
    {
        var horizon = DateTime.UtcNow.AddDays(-30);   // bounded: the panel describes recent behaviour, and an unbounded scan of months of 1 Hz data is slow
        await using var db = await _factory.CreateDbContextAsync(ct);
        var d = await db.Devices.AsNoTracking().Include(x => x.Identifiers).FirstOrDefaultAsync(x => x.Id == deviceId, ct);
        if (d is null) return null;
        var newest = await db.Observations.AsNoTracking().Where(o => o.DeviceId == deviceId && o.TimestampUtc >= horizon).OrderByDescending(o => o.TimestampUtc).Select(o => new { o.AddressKind }).FirstOrDefaultAsync(ct);
        var mfr = await db.Observations.AsNoTracking().Where(o => o.DeviceId == deviceId && o.TimestampUtc >= horizon && o.ManufacturerData != null).OrderByDescending(o => o.TimestampUtc).Select(o => o.ManufacturerData).FirstOrDefaultAsync(ct);
        var svc = await db.Observations.AsNoTracking().Where(o => o.DeviceId == deviceId && o.TimestampUtc >= horizon && o.ServiceUuids != null).OrderByDescending(o => o.TimestampUtc).Select(o => o.ServiceUuids).FirstOrDefaultAsync(ct);
        var bySource = (await db.Database.SqlQuery<SourceRow>($"SELECT Source, COUNT(*) AS Cnt FROM Observations WHERE DeviceId = {deviceId} AND TimestampUtc >= {horizon} GROUP BY Source").ToListAsync(ct))
            .ToDictionary(r => r.Source, r => r.Cnt);
        return new DeviceProfileInput(d.DisplayName, d.AdvertisedName, d.Category, d.AssignedRoom, d.AssignedWorkstation, mfr, svc,
            newest?.AddressKind ?? Domain.AddressKind.Unknown,
            d.Identifiers.Any(i => i.IdentifierType == Domain.IdentifierTypes.StableAddress), d.Identifiers.Any(i => i.IdentifierType == Domain.IdentifierTypes.Fingerprint),
            d.FirstSeenUtc, d.LastSeenUtc, d.ObservationCount, bySource);
    }
}
