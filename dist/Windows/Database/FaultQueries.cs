using BluetoothFleetMonitor.Application.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace BluetoothFleetMonitor.Database;

/// <summary>
/// Loads the evidence the fault analyzer needs. Heavy lifting (gaps, bucketing) happens in SQL so a week of data for 170+ devices stays cheap.
/// Scanner coverage comes from the scanner's own state events: only time the scanner was in the Running state counts as listening.
/// </summary>
public sealed class FaultQueries
{
    private readonly IDbContextFactory<FleetDbContext> _factory;
    public FaultQueries(IDbContextFactory<FleetDbContext> factory) => _factory = factory;

    private sealed class EdgeRow { public Guid DeviceId { get; set; } public DateTime First { get; set; } public DateTime Last { get; set; } public long Cnt { get; set; } }
    private sealed class GapRow { public Guid DeviceId { get; set; } public DateTime StartTs { get; set; } public DateTime EndTs { get; set; } }
    private sealed class BucketRow { public Guid DeviceId { get; set; } public long Bucket { get; set; } public double Avg { get; set; } public long Cnt { get; set; } }
    private sealed class TestRow { public Guid DeviceId { get; set; } public long Attempts { get; set; } public long Failed { get; set; } }
    private sealed class EvtRow { public DateTime Ts { get; set; } public string EventType { get; set; } = ""; }
    private sealed class MaxRow { public DateTime? Ts { get; set; } }

    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);

    public async Task<(IReadOnlyList<DeviceEvidence> Devices, IReadOnlyList<TimeRange> Coverage)> LoadAsync(TimeRange window, AnalyzerSettings s, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var from = window.StartUtc; var to = window.EndUtc; var minGap = (double)s.MinGapSeconds;

        var devices = await db.Devices.AsNoTracking().ToListAsync(ct);
        var edges = (await db.Database.SqlQuery<EdgeRow>($@"
            SELECT DeviceId, MIN(TimestampUtc) AS First, MAX(TimestampUtc) AS Last, COUNT(*) AS Cnt
            FROM Observations WHERE TimestampUtc >= {from} AND TimestampUtc <= {to} GROUP BY DeviceId").ToListAsync(ct)).ToDictionary(e => e.DeviceId);

        var gaps = (await db.Database.SqlQuery<GapRow>($@"
            SELECT DeviceId, Prev AS StartTs, Ts AS EndTs FROM (
              SELECT DeviceId, TimestampUtc AS Ts, LAG(TimestampUtc) OVER (PARTITION BY DeviceId ORDER BY TimestampUtc) AS Prev
              FROM Observations WHERE TimestampUtc >= {from} AND TimestampUtc <= {to})
            WHERE Prev IS NOT NULL AND (julianday(Ts) - julianday(Prev)) * 86400.0 > {minGap}").ToListAsync(ct))
            .GroupBy(g => g.DeviceId).ToDictionary(g => g.Key, g => g.Select(x => new TimeRange(Utc(x.StartTs), Utc(x.EndTs))).ToList());

        const int bucketSeconds = 900;
        var buckets = (await db.Database.SqlQuery<BucketRow>($@"
            SELECT DeviceId, CAST(strftime('%s', TimestampUtc) AS INTEGER) / {bucketSeconds} AS Bucket, AVG(Rssi) AS Avg, COUNT(*) AS Cnt
            FROM Observations WHERE TimestampUtc >= {from} AND TimestampUtc <= {to} GROUP BY DeviceId, Bucket ORDER BY DeviceId, Bucket").ToListAsync(ct))
            .GroupBy(b => b.DeviceId).ToDictionary(g => g.Key, g => g.Select(b => new RssiBucket(DateTime.UnixEpoch.AddSeconds(b.Bucket * bucketSeconds), b.Avg, (int)b.Cnt)).ToList());

        var tests = (await db.Database.SqlQuery<TestRow>($@"
            SELECT DeviceId, SUM(Attempts) AS Attempts, SUM(FailedAttempts) AS Failed
            FROM ConnectionTests WHERE StartedAtUtc >= {from} AND StartedAtUtc <= {to} GROUP BY DeviceId").ToListAsync(ct)).ToDictionary(t => t.DeviceId);

        var list = new List<DeviceEvidence>();
        foreach (var d in devices)
        {
            if (!edges.TryGetValue(d.Id, out var e)) continue;                       // not heard at all in the window: nothing to judge
            var sil = gaps.TryGetValue(d.Id, out var g) ? new List<TimeRange>(g) : new List<TimeRange>();
            var first = Utc(e.First); var last = Utc(e.Last);
            if ((first - from).TotalSeconds > minGap) sil.Add(new TimeRange(from, first));    // silent from the start of the window until first heard (cut later if the device did not exist yet)
            if ((to - last).TotalSeconds > minGap) sil.Add(new TimeRange(last, to));          // silent since it was last heard
            tests.TryGetValue(d.Id, out var t);
            list.Add(new DeviceEvidence
            {
                DeviceId = d.Id, Name = d.DisplayName, Category = d.Category, Room = d.AssignedRoom, Workstation = d.AssignedWorkstation,
                Observations = e.Cnt, Silences = sil, Buckets = buckets.TryGetValue(d.Id, out var b) ? b : new List<RssiBucket>(),
                TestAttempts = (int)(t?.Attempts ?? 0), TestFailures = (int)(t?.Failed ?? 0), FirstSeenUtc = d.FirstSeenUtc,
            });
        }
        return (list, await LoadCoverageCoreAsync(db, window, ct));
    }

    /// <summary>Time ranges in the window during which the scanner was in the Running state.</summary>
    public async Task<IReadOnlyList<TimeRange>> LoadCoverageAsync(TimeRange window, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await LoadCoverageCoreAsync(db, window, ct);
    }

    /// <summary>The same fleet analysis the Fault Detector shows, narrowed to one device (null when it was not heard in the window).</summary>
    public async Task<DeviceDiagnosis?> DiagnoseAsync(Guid deviceId, TimeRange window, AnalyzerSettings s, CancellationToken ct = default)
    {
        var (devices, coverage) = await LoadAsync(window, s, ct);
        return FaultAnalyzer.Analyze(devices, coverage, window, s).Devices.FirstOrDefault(d => d.DeviceId == deviceId);
    }

    private static async Task<IReadOnlyList<TimeRange>> LoadCoverageCoreAsync(FleetDbContext db, TimeRange window, CancellationToken ct)
    {
        var from = window.StartUtc; var to = window.EndUtc;
        // state in force at the start of the window, then every change inside it
        var before = await db.Database.SqlQuery<EvtRow>($@"SELECT TimestampUtc AS Ts, EventType FROM ApplicationEvents WHERE Source = 'Scanner' AND TimestampUtc < {from} ORDER BY TimestampUtc DESC LIMIT 1").ToListAsync(ct);
        var inside = await db.Database.SqlQuery<EvtRow>($@"SELECT TimestampUtc AS Ts, EventType FROM ApplicationEvents WHERE Source = 'Scanner' AND TimestampUtc >= {from} AND TimestampUtc <= {to} ORDER BY TimestampUtc").ToListAsync(ct);
        var events = new List<(DateTime Ts, string Type)>();
        if (before.Count > 0) events.Add((from, before[0].EventType));
        events.AddRange(inside.Select(e => (Utc(e.Ts), e.EventType)));

        var ranges = new List<TimeRange>();
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i].Type != "Running") continue;
            var start = events[i].Ts;
            var end = i + 1 < events.Count ? events[i + 1].Ts : to;
            var cleanStop = i + 1 < events.Count && events[i + 1].Type is "Stopped" or "Interrupted" or "RunningNoAdvertisements" or "AdapterUnavailable";
            if (!cleanStop)
            {
                // The app may have crashed or been killed with no Stopped event: do not count the dead time as listening.
                var lastHeard = await db.Database.SqlQuery<MaxRow>($"SELECT MAX(TimestampUtc) AS Ts FROM Observations WHERE TimestampUtc >= {start} AND TimestampUtc <= {end}").SingleAsync(ct);
                if (lastHeard.Ts is { } lh) { var cap = Utc(lh).AddSeconds(120); if (cap < end) end = cap; } else end = start;
            }
            if (end > start) ranges.Add(new TimeRange(start, end));
        }
        return ranges;
    }
}
