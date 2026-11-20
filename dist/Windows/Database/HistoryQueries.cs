using Microsoft.EntityFrameworkCore;

namespace BluetoothFleetMonitor.Database;

public sealed record RssiPoint(DateTime BucketStartUtc, double Average, int Min, int Max, int Count);

public sealed record HistorySummary(int Count, int? Min, int? Max, double? Average, DateTime? FirstUtc, DateTime? LastUtc, TimeSpan LongestGap);

/// <summary>Read-side queries for the history screens. Bucketing happens in SQL so a month of 1 Hz data stays cheap.</summary>
public sealed class HistoryQueries
{
    private readonly IDbContextFactory<FleetDbContext> _factory;

    public HistoryQueries(IDbContextFactory<FleetDbContext> factory) => _factory = factory;

    private sealed class BucketRow
    {
        public long Bucket { get; set; }
        public double Avg { get; set; }
        public long Min { get; set; }
        public long Max { get; set; }
        public long Cnt { get; set; }
    }

    public async Task<IReadOnlyList<RssiPoint>> GetRssiHistoryAsync(Guid deviceId, DateTime fromUtc, DateTime toUtc, int bucketSeconds, CancellationToken ct = default)
    {
        if (bucketSeconds < 1) bucketSeconds = 1;
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.Database.SqlQuery<BucketRow>($@"
            SELECT CAST(strftime('%s', TimestampUtc) AS INTEGER) / {bucketSeconds} AS Bucket,
                   AVG(Rssi) AS Avg, MIN(Rssi) AS Min, MAX(Rssi) AS Max, COUNT(*) AS Cnt
            FROM Observations
            WHERE DeviceId = {deviceId} AND TimestampUtc >= {fromUtc} AND TimestampUtc <= {toUtc}
            GROUP BY Bucket ORDER BY Bucket").ToListAsync(ct);

        return rows.Select(r => new RssiPoint(
            DateTime.UnixEpoch.AddSeconds(r.Bucket * bucketSeconds), r.Avg, (int)r.Min, (int)r.Max, (int)r.Cnt)).ToList();
    }

    private sealed class GapRow { public double? Gap { get; set; } }
    private sealed class StatRow
    {
        public long Cnt { get; set; }
        public long? Min { get; set; }
        public long? Max { get; set; }
        public double? Avg { get; set; }
        public DateTime? First { get; set; }
        public DateTime? Last { get; set; }
    }

    public async Task<HistorySummary> GetSummaryAsync(Guid deviceId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var stat = await db.Database.SqlQuery<StatRow>($@"
            SELECT COUNT(*) AS Cnt, MIN(Rssi) AS Min, MAX(Rssi) AS Max, AVG(Rssi) AS Avg,
                   MIN(TimestampUtc) AS First, MAX(TimestampUtc) AS Last
            FROM Observations
            WHERE DeviceId = {deviceId} AND TimestampUtc >= {fromUtc} AND TimestampUtc <= {toUtc}").SingleAsync(ct);

        var gap = await db.Database.SqlQuery<GapRow>($@"
            SELECT MAX(Gap) AS Gap FROM (
              SELECT (julianday(TimestampUtc) - julianday(LAG(TimestampUtc) OVER (ORDER BY TimestampUtc))) * 86400.0 AS Gap
              FROM Observations
              WHERE DeviceId = {deviceId} AND TimestampUtc >= {fromUtc} AND TimestampUtc <= {toUtc})").SingleAsync(ct);

        // julianday is floating point; round to milliseconds so 98 s reads as 98 s.
        var longest = gap.Gap is { } g ? TimeSpan.FromMilliseconds(Math.Round(g * 1000)) : TimeSpan.Zero;
        return new HistorySummary((int)stat.Cnt, (int?)stat.Min, (int?)stat.Max, stat.Avg,
            stat.First is { } f ? DateTime.SpecifyKind(f, DateTimeKind.Utc) : null,
            stat.Last is { } l ? DateTime.SpecifyKind(l, DateTimeKind.Utc) : null, longest);
    }
}
