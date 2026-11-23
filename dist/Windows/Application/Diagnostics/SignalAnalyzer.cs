namespace BluetoothFleetMonitor.Application.Diagnostics;

/// <summary>Signal level over one bucket of time. The database aggregates; the analyzer never sees raw 1 Hz data.</summary>
public sealed record SignalBucket(DateTime StartUtc, double Average, int Min, int Max, int Count);

/// <summary>
/// Everything the analysis needs about one device in one window. <see cref="IntervalSecondCounts"/>[s] is the number of gaps between
/// consecutive advertisements whose whole-second length is s (key 600 = 600 s or longer).
/// </summary>
public sealed record SignalInput(TimeRange Window, int BucketSeconds, IReadOnlyList<SignalBucket> Buckets,
    IReadOnlyDictionary<int, int> IntervalSecondCounts, double MaxIntervalSeconds, IReadOnlyList<TimeRange> Silences);

public sealed record SignalStats(int Min, int Max, double Mean, double Median, double StdDev, double Volatility);
public sealed record DistributionBin(int FromDbm, int ToDbm, int Count);
public sealed record StrengthShare(string Label, double Fraction);
public sealed record IntervalBin(string Label, int Count);
public sealed record IntervalStats(double MedianSeconds, double P95Seconds, double MaxSeconds, IReadOnlyList<IntervalBin> Bins);
public sealed record SilenceDetail(TimeRange Range, double? LevelBeforeDbm, double? LevelAfterDbm);
public sealed record RecoveryEvent(DateTime DropStartUtc, double DropDb, TimeSpan? RecoveredAfter);

public sealed class SignalAnalysis
{
    public bool Sufficient { get; init; }
    public string? Note { get; init; }
    public SignalStats? Stats { get; init; }
    public IReadOnlyList<double> MovingAverage { get; init; } = Array.Empty<double>();
    public IReadOnlyList<DistributionBin> Distribution { get; init; } = Array.Empty<DistributionBin>();
    public IReadOnlyList<StrengthShare> Strength { get; init; } = Array.Empty<StrengthShare>();
    public IntervalStats? Intervals { get; init; }
    public IReadOnlyList<SilenceDetail> Silences { get; init; } = Array.Empty<SilenceDetail>();
    public IReadOnlyList<RecoveryEvent> Recoveries { get; init; } = Array.Empty<RecoveryEvent>();
}

/// <summary>
/// Describes one device's signal behaviour. Pure (no database, no clock). RSSI is never turned into a distance; the strength words are
/// only a description of this device's own readings. Silences passed in are expected to be cut to scanner coverage already.
/// </summary>
public static class SignalAnalyzer
{
    public const int MinBuckets = 3;
    public const double DropDb = 10, RecoveredWithinDb = 3;
    private static readonly string[] StrengthLabels = { "Excellent", "Good", "Fair", "Weak", "Very weak" };
    private static readonly (string Label, int FromSecond, int ToSecond)[] IntervalBins =
    {
        ("<1", 0, 1), ("1-2", 1, 2), ("2-5", 2, 5), ("5-10", 5, 10), ("10-30", 10, 30), ("30-120", 30, 120), (">120", 120, int.MaxValue),
    };

    public static string Describe(double rssi) => rssi >= -55 ? "Excellent" : rssi >= -67 ? "Good" : rssi >= -78 ? "Fair" : rssi >= -88 ? "Weak" : "Very weak";

    public static int DefaultMovingAverage(int bucketCount) => Math.Max(3, (int)Math.Round(bucketCount * 0.05));

    public static SignalAnalysis Analyze(SignalInput input, int movingAverageBuckets = 0)
    {
        var b = input.Buckets.Where(x => x.Count > 0).OrderBy(x => x.StartUtc).ToList();
        var intervals = AnalyzeIntervals(input);
        if (b.Count < MinBuckets)
            return new SignalAnalysis { Sufficient = false, Note = $"Not enough data: {b.Count} signal bucket(s) in this window, at least {MinBuckets} are needed.", Intervals = intervals };

        var avgs = b.Select(x => x.Average).ToList();
        var totalCount = b.Sum(x => (double)x.Count);
        var mean = b.Sum(x => x.Average * x.Count) / totalCount;
        var diffs = Enumerable.Range(1, avgs.Count - 1).Select(i => avgs[i] - avgs[i - 1]).ToList();
        var stats = new SignalStats(b.Min(x => x.Min), b.Max(x => x.Max), mean, WeightedMedian(b, totalCount), StdDev(avgs), StdDev(diffs));

        var w = movingAverageBuckets > 0 ? movingAverageBuckets : DefaultMovingAverage(b.Count);
        var ma = new List<double>(b.Count);
        for (var i = 0; i < b.Count; i++) { var from = Math.Max(0, i - w + 1); ma.Add(avgs.Skip(from).Take(i - from + 1).Average()); }

        var bins = avgs.GroupBy(a => (int)Math.Floor(a / 5.0) * 5).OrderBy(g => g.Key).Select(g => new DistributionBin(g.Key, g.Key + 5, g.Count())).ToList();
        var shares = StrengthLabels.Select(l => new StrengthShare(l, avgs.Count(a => Describe(a) == l) / (double)avgs.Count)).ToList();

        return new SignalAnalysis
        {
            Sufficient = true, Stats = stats, MovingAverage = ma, Distribution = bins, Strength = shares, Intervals = intervals,
            Silences = input.Silences.Select(s => new SilenceDetail(s,
                b.LastOrDefault(x => x.StartUtc < s.StartUtc)?.Average, b.FirstOrDefault(x => x.StartUtc >= s.EndUtc)?.Average)).ToList(),
            Recoveries = FindRecoveries(b),
        };
    }

    private static double WeightedMedian(List<SignalBucket> b, double total)
    {
        var cum = 0.0;
        foreach (var x in b.OrderBy(x => x.Average)) { cum += x.Count; if (cum > total / 2) return x.Average; }
        return b[^1].Average;
    }

    private static double StdDev(IReadOnlyList<double> v)
    {
        if (v.Count == 0) return 0;
        var m = v.Average();
        return Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / v.Count);
    }

    private static IntervalStats? AnalyzeIntervals(SignalInput input)
    {
        var total = input.IntervalSecondCounts.Values.Sum();
        if (total == 0) return null;
        var sorted = input.IntervalSecondCounts.OrderBy(k => k.Key).ToList();
        double Percentile(double p)
        {
            var target = Math.Max(1, (int)Math.Ceiling(total * p)); var cum = 0;
            foreach (var kv in sorted) { cum += kv.Value; if (cum >= target) return kv.Key; }
            return sorted[^1].Key;
        }
        var bins = IntervalBins.Select(x => new IntervalBin(x.Label, sorted.Where(kv => kv.Key >= x.FromSecond && kv.Key < x.ToSecond).Sum(kv => kv.Value))).ToList();
        return new IntervalStats(Percentile(0.5), Percentile(0.95), input.MaxIntervalSeconds, bins);
    }

    /// <summary>A drop is a bucket 10 dB or more under the device's own baseline (median of the previous 60 minutes); it ends when the signal is back within 3 dB.</summary>
    private static List<RecoveryEvent> FindRecoveries(List<SignalBucket> b)
    {
        var events = new List<RecoveryEvent>();
        DateTime? dropStart = null; double baseline = 0, depth = 0;
        foreach (var x in b)
        {
            if (dropStart is null)
            {
                var prior = b.Where(p => p.StartUtc >= x.StartUtc.AddMinutes(-60) && p.StartUtc < x.StartUtc).Select(p => p.Average).OrderBy(a => a).ToList();
                if (prior.Count < 3) continue;
                baseline = prior.Count % 2 == 1 ? prior[prior.Count / 2] : (prior[prior.Count / 2 - 1] + prior[prior.Count / 2]) / 2;
                if (x.Average <= baseline - DropDb) { dropStart = x.StartUtc; depth = baseline - x.Average; }
            }
            else
            {
                depth = Math.Max(depth, baseline - x.Average);
                if (x.Average >= baseline - RecoveredWithinDb) { events.Add(new RecoveryEvent(dropStart.Value, depth, x.StartUtc - dropStart.Value)); dropStart = null; }
            }
        }
        if (dropStart is not null) events.Add(new RecoveryEvent(dropStart.Value, depth, null));
        return events;
    }
}
