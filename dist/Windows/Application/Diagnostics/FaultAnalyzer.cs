namespace BluetoothFleetMonitor.Application.Diagnostics;

/// <summary>A time range in UTC.</summary>
public readonly record struct TimeRange(DateTime StartUtc, DateTime EndUtc)
{
    public TimeSpan Length => EndUtc > StartUtc ? EndUtc - StartUtc : TimeSpan.Zero;
    public bool Overlaps(TimeRange o) => StartUtc < o.EndUtc && o.StartUtc < EndUtc;
}

/// <summary>Average signal over one bucket (the database aggregates; the analyzer never sees raw 1 Hz data).</summary>
public readonly record struct RssiBucket(DateTime StartUtc, double Average, int Count);

/// <summary>Everything the analyzer needs to know about one device in the analysed window.</summary>
public sealed class DeviceEvidence
{
    public Guid DeviceId { get; init; }
    public string Name { get; init; } = "";
    public string Category { get; init; } = "Unknown";
    public string? Room { get; init; }
    public string? Workstation { get; init; }
    public long Observations { get; init; }
    /// <summary>Intervals with no advertisement from this device that lasted longer than the minimum gap (already cut to the analysed window).</summary>
    public IReadOnlyList<TimeRange> Silences { get; init; } = Array.Empty<TimeRange>();
    public IReadOnlyList<RssiBucket> Buckets { get; init; } = Array.Empty<RssiBucket>();
    public int TestAttempts { get; init; }
    public int TestFailures { get; init; }
    /// <summary>When this device was first ever heard (UTC). Silence before it was first heard is not a fault.</summary>
    public DateTime? FirstSeenUtc { get; init; }
}

/// <summary>Weights and thresholds. Every number is adjustable; none is a validated failure probability.</summary>
public sealed class AnalyzerSettings
{
    public double WeightPresence { get; set; } = 35;
    public double WeightSignal { get; set; } = 25;
    public double WeightRecovery { get; set; } = 15;
    public double WeightTrend { get; set; } = 15;
    public double WeightTests { get; set; } = 10;

    /// <summary>A silence shorter than this is normal advertising behaviour, not an interruption.</summary>
    public int MinGapSeconds { get; set; } = 120;
    /// <summary>A silence shorter than this that ends with the device coming back counts as a flap (quick recovery).</summary>
    public int FlapMaxMinutes { get; set; } = 15;
    /// <summary>Share of the other devices that must be silent at the same time for the silence to count as environmental.</summary>
    public double CorrelationShare { get; set; } = 0.5;
    public int CorrelationMinDevices { get; set; } = 3;
    /// <summary>Fewer observations than this and no strong diagnosis is issued.</summary>
    public int MinObservations { get; set; } = 200;
    /// <summary>Less covered time than this (hours) and no strong diagnosis is issued.</summary>
    public double MinCoveredHours { get; set; } = 2;
    public double MinConfidenceForDiagnosis { get; set; } = 0.5;
    /// <summary>Bands, lower bounds: Healthy, Good, Monitor, Unstable (below that: Critical).</summary>
    public int HealthyFrom { get; set; } = 90;
    public int GoodFrom { get; set; } = 75;
    public int MonitorFrom { get; set; } = 60;
    public int UnstableFrom { get; set; } = 40;
}

public enum HealthBand { NotEnoughData, Healthy, Good, Monitor, Unstable, Critical }

public sealed record ScoreComponent(string Name, double? Score, double Weight, string Explanation)
{
    public bool Used => Score.HasValue;
}

public enum FindingKind { PresenceInstability, SignalInstability, Flapping, SignalDegradation, TestFailures, Environmental, HardwareSuspected }

public sealed record Finding(FindingKind Kind, int Severity, string Text);

public sealed class DeviceDiagnosis
{
    public Guid DeviceId { get; init; }
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public string? Room { get; init; }
    public string? Workstation { get; init; }
    public double? Health { get; init; }
    public HealthBand Band { get; init; }
    public double Confidence { get; init; }
    public int Interruptions { get; init; }
    public int Flaps { get; init; }
    public double MissingMinutes { get; init; }
    public double? SignalSpreadDb { get; init; }
    public double? TrendDb { get; init; }
    public double? TestFailureRate { get; init; }
    public int EnvironmentalSilences { get; init; }
    public IReadOnlyList<ScoreComponent> Components { get; init; } = Array.Empty<ScoreComponent>();
    public IReadOnlyList<Finding> Findings { get; init; } = Array.Empty<Finding>();
    public string Recommendation { get; init; } = "";
    public int Severity => Findings.Count == 0 ? 0 : Findings.Max(f => f.Severity);
}

public sealed record FleetEvent(TimeRange Range, int DevicesSilent, int DevicesActive, string? Room);

public sealed class FleetAnalysis
{
    public IReadOnlyList<DeviceDiagnosis> Devices { get; init; } = Array.Empty<DeviceDiagnosis>();
    public IReadOnlyList<FleetEvent> EnvironmentalEvents { get; init; } = Array.Empty<FleetEvent>();
    public double CoveredHours { get; init; }
}

/// <summary>
/// Explainable device health analysis. Pure: no database, no clock. Rules that matter:
/// time the scanner was not listening is never held against a device; silences that many devices share are attributed to the
/// environment or the scanner, not to the device; a component with no usable data is left out and the weights renormalised;
/// a device with too little data gets a score but no strong diagnosis.
/// </summary>
public static class FaultAnalyzer
{
    public static FleetAnalysis Analyze(IReadOnlyList<DeviceEvidence> devices, IReadOnlyList<TimeRange> coverage, TimeRange window, AnalyzerSettings s)
    {
        var covered = Merge(coverage.Select(c => Clip(c, window)).Where(c => c.Length > TimeSpan.Zero).ToList());
        var coveredHours = covered.Sum(c => c.Length.TotalHours);

        // 1. Cut every silence to the time the scanner was actually listening and to the time the device existed.
        var cut = new Dictionary<Guid, List<TimeRange>>();
        foreach (var d in devices)
        {
            cut[d.DeviceId] = CutToCoverage(d.Silences, covered, window, d.FirstSeenUtc, s.MinGapSeconds).ToList();
        }

        // 2. Fleet-wide / room-wide silences: many devices quiet at the same time is the environment or the scanner, not the devices.
        var envEvents = new List<FleetEvent>();
        var envFor = new Dictionary<Guid, List<TimeRange>>();
        foreach (var d in devices) envFor[d.DeviceId] = new List<TimeRange>();

        void Correlate(IReadOnlyList<DeviceEvidence> group, string? room)
        {
            if (group.Count < s.CorrelationMinDevices) return;
            var edges = group.SelectMany(d => cut[d.DeviceId]).SelectMany(r => new[] { r.StartUtc, r.EndUtc }).Distinct().OrderBy(t => t).ToList();
            TimeRange? open = null; var peakSilent = 0;
            for (var i = 0; i + 1 < edges.Count; i++)
            {
                var seg = new TimeRange(edges[i], edges[i + 1]);
                var mid = seg.StartUtc + (seg.EndUtc - seg.StartUtc) / 2;
                var silent = group.Count(d => cut[d.DeviceId].Any(r => r.StartUtc <= mid && mid < r.EndUtc));
                var shared = silent >= s.CorrelationMinDevices && silent >= s.CorrelationShare * group.Count;
                if (shared) { open = open is { } o ? new TimeRange(o.StartUtc, seg.EndUtc) : seg; peakSilent = Math.Max(peakSilent, silent); }
                else if (open is { } o2) { Close(o2); }
            }
            if (open is { } last) Close(last);

            void Close(TimeRange r)
            {
                envEvents.Add(new FleetEvent(r, peakSilent, group.Count, room));
                foreach (var d in group) envFor[d.DeviceId].Add(r);
                open = null; peakSilent = 0;
            }
        }

        Correlate(devices, null);
        foreach (var g in devices.Where(d => !string.IsNullOrWhiteSpace(d.Room)).GroupBy(d => d.Room!)) Correlate(g.ToList(), g.Key);

        // 3. Per-device scoring.
        var results = devices.Select(d => AnalyzeDevice(d, cut[d.DeviceId], envFor[d.DeviceId], coveredHours, window, s)).ToList();
        return new FleetAnalysis { Devices = results, EnvironmentalEvents = envEvents.OrderBy(e => e.Range.StartUtc).ToList(), CoveredHours = coveredHours };
    }

    private static DeviceDiagnosis AnalyzeDevice(DeviceEvidence d, List<TimeRange> silences, List<TimeRange> env, double coveredHours, TimeRange window, AnalyzerSettings s)
    {
        var own = new List<TimeRange>(); var envOnly = 0;
        foreach (var sil in silences)
        {
            if (env.Any(e => e.Overlaps(sil) && Overlap(e, sil) >= 0.5 * sil.Length.TotalSeconds)) envOnly++;
            else own.Add(sil);
        }
        var days = Math.Max(coveredHours / 24.0, 1.0 / 24);
        var missingMin = own.Sum(r => r.Length.TotalMinutes);
        var coveredMin = Math.Max(coveredHours * 60, 1);
        var missingFraction = Math.Min(1, missingMin / coveredMin);
        var flaps = own.Count(r => r.Length <= TimeSpan.FromMinutes(s.FlapMaxMinutes) && r.EndUtc < window.EndUtc);
        var interruptions = own.Count;

        // confidence: how much coverage and how many observations stand behind the score
        var conf = 0.5 * Math.Min(1, coveredHours / Math.Max(s.MinCoveredHours * 4, 0.1)) + 0.5 * Math.Min(1, d.Observations / (double)Math.Max(s.MinObservations * 2, 1));
        if (d.Observations < s.MinObservations || coveredHours < s.MinCoveredHours) conf = Math.Min(conf, 0.35);

        var comps = new List<ScoreComponent>();

        // presence: how much of the listening time the device was silent, and how often it dropped out
        double? presence = null;
        if (coveredHours > 0 && d.Observations > 0)
        {
            presence = Clamp(100 - 100 * missingFraction * 1.5 - 6 * (interruptions / days));
            comps.Add(new ScoreComponent("Presence stability", presence, s.WeightPresence,
                $"{interruptions} interruption(s) of {s.MinGapSeconds}s or more, silent {missingMin:0} min of {coveredMin:0} min covered ({missingFraction:P1}); score = 100 - 150% x silent share - 6 per interruption per day."));
        }
        else comps.Add(new ScoreComponent("Presence stability", null, s.WeightPresence, "No covered scanning time or no observations: left out."));

        // signal stability: spread of the device's own per-bucket averages
        var avgs = d.Buckets.Where(b => b.Count > 0).Select(b => b.Average).ToList();
        double? spread = null; double? signal = null;
        if (avgs.Count >= 5)
        {
            spread = StdDev(avgs);
            signal = Clamp(100 - 7 * Math.Max(0, spread.Value - 3));
            comps.Add(new ScoreComponent("Signal stability", signal, s.WeightSignal, $"Signal level varies by {spread:0.0} dB (standard deviation of {avgs.Count} buckets); up to 3 dB is normal, each extra dB costs 7 points."));
        }
        else comps.Add(new ScoreComponent("Signal stability", null, s.WeightSignal, "Fewer than 5 signal buckets: left out."));

        // recovery: quick drop-and-return cycles
        double? recovery = null;
        if (coveredHours > 0 && d.Observations > 0)
        {
            recovery = Clamp(100 - 10 * (flaps / days));
            comps.Add(new ScoreComponent("Recovery frequency", recovery, s.WeightRecovery, $"{flaps} quick drop-and-return cycle(s) (under {s.FlapMaxMinutes} min); each per day costs 10 points."));
        }
        else comps.Add(new ScoreComponent("Recovery frequency", null, s.WeightRecovery, "No data: left out."));

        // trend: last third of the window against the first third
        double? trend = null; double? trendScore = null;
        if (avgs.Count >= 9)
        {
            var third = avgs.Count / 3;
            trend = avgs.Skip(avgs.Count - third).Average() - avgs.Take(third).Average();
            trendScore = trend >= -2 ? 100 : Clamp(100 * (1 - (-2 - trend.Value) / 13.0));
            comps.Add(new ScoreComponent("RSSI trend", trendScore, s.WeightTrend, $"Signal moved {trend:+0.0;-0.0} dB between the first and last third of the window; a fall of more than 2 dB costs points, 15 dB or more scores 0."));
        }
        else comps.Add(new ScoreComponent("RSSI trend", null, s.WeightTrend, "Fewer than 9 signal buckets: left out."));

        // connection tests: only confirmed results count
        double? testRate = null; double? testScore = null;
        if (d.TestAttempts >= 3)
        {
            testRate = (double)d.TestFailures / d.TestAttempts;
            testScore = Clamp(100 * (1 - testRate.Value));
            comps.Add(new ScoreComponent("Connection tests", testScore, s.WeightTests, $"{d.TestFailures} of {d.TestAttempts} connection attempts failed ({testRate:P0})."));
        }
        else comps.Add(new ScoreComponent("Connection tests", null, s.WeightTests, d.TestAttempts == 0 ? "Never tested: left out, not counted as a failure." : "Fewer than 3 attempts: left out."));

        var usedWeight = comps.Where(c => c.Used).Sum(c => c.Weight);
        double? health = usedWeight > 0 ? comps.Where(c => c.Used).Sum(c => c.Score!.Value * c.Weight) / usedWeight : null;

        var findings = new List<Finding>();
        if (presence is < 75) findings.Add(new Finding(FindingKind.PresenceInstability, presence < 40 ? 3 : presence < 60 ? 2 : 1,
            $"Went silent {interruptions} time(s) and was missing {missingMin:0} of {coveredMin:0} covered minutes while the scanner was listening."));
        if (flaps >= 3 && recovery is < 85) findings.Add(new Finding(FindingKind.Flapping, flaps >= 8 ? 3 : 2, $"Dropped out and came back {flaps} times within {s.FlapMaxMinutes} minutes each: typical of a weak link, low battery or interference."));
        if (signal is < 70) findings.Add(new Finding(FindingKind.SignalInstability, signal < 40 ? 2 : 1, $"Signal swings by {spread:0.0} dB; a stable device sits within about 3 dB."));
        if (trendScore is < 70) findings.Add(new Finding(FindingKind.SignalDegradation, trendScore < 40 ? 2 : 1, $"Signal fell {-trend:0.0} dB over the window."));
        if (testRate is >= 0.2) findings.Add(new Finding(FindingKind.TestFailures, testRate >= 0.5 ? 3 : 2, $"{d.TestFailures} of {d.TestAttempts} connection tests failed."));
        if (envOnly > 0) findings.Add(new Finding(FindingKind.Environmental, 0, $"{envOnly} silence(s) happened together with many other devices, so they point at the environment or the scanner and were not held against this device."));

        // hardware suspicion: poor in more than one separate day while the device-specific evidence is strong
        var badDays = BadDays(own, window);
        if (conf >= s.MinConfidenceForDiagnosis && badDays >= 2 && (presence is < 80 || testRate is >= 0.3) && envOnly <= interruptions)
            findings.Add(new Finding(FindingKind.HardwareSuspected, 3, $"Problems on {badDays} separate days that other devices did not share: suspect the device itself (battery, antenna, firmware)."));

        // A confirmed pattern (repeated device-only problems on several days, or most connection tests failing) must not hide behind good
        // signal numbers: the weighted score is capped at the top of the Unstable band, and the cap is shown.
        if (health is > 59 && conf >= s.MinConfidenceForDiagnosis && findings.Any(f => f.Kind is FindingKind.HardwareSuspected or FindingKind.TestFailures && f.Severity >= 3))
        {
            comps.Add(new ScoreComponent("Cap", null, 0, $"Weighted score {health:0} capped at 59 (Unstable) because a repeated device-specific problem is confirmed."));
            health = 59;
        }
        var band = Band(health, conf, s);
        var rec = Recommendation(band, findings, conf, s);
        return new DeviceDiagnosis
        {
            DeviceId = d.DeviceId, Name = d.Name, Category = d.Category, Room = d.Room, Workstation = d.Workstation,
            Health = health, Band = band, Confidence = conf, Interruptions = interruptions, Flaps = flaps, MissingMinutes = missingMin,
            SignalSpreadDb = spread, TrendDb = trend, TestFailureRate = testRate, EnvironmentalSilences = envOnly,
            Components = comps, Findings = findings.OrderByDescending(f => f.Severity).ToList(), Recommendation = rec,
        };
    }

    public static HealthBand Band(double? health, double confidence, AnalyzerSettings s)
    {
        if (health is null || confidence < s.MinConfidenceForDiagnosis) return HealthBand.NotEnoughData;
        var h = health.Value;
        return h >= s.HealthyFrom ? HealthBand.Healthy : h >= s.GoodFrom ? HealthBand.Good : h >= s.MonitorFrom ? HealthBand.Monitor : h >= s.UnstableFrom ? HealthBand.Unstable : HealthBand.Critical;
    }

    private static string Recommendation(HealthBand band, List<Finding> findings, double conf, AnalyzerSettings s)
    {
        if (band == HealthBand.NotEnoughData) return $"Not enough data yet for a strong diagnosis (confidence {conf:P0}, needs {s.MinConfidenceForDiagnosis:P0}). Keep scanning; the score may still move.";
        var kinds = findings.Select(f => f.Kind).ToHashSet();
        if (kinds.Contains(FindingKind.HardwareSuspected) || kinds.Contains(FindingKind.TestFailures))
            return "Likely a device-side problem: replace the battery first, then check firmware, then swap the device. Run a connection test on it from Connection Tests (one device, with the warning).";
        if (kinds.Contains(FindingKind.Flapping) || kinds.Contains(FindingKind.PresenceInstability))
            return "Check battery and distance to the nearest scanner, move the scanner or add one closer, and check for 2.4 GHz interference (Wi-Fi routers, USB 3 hubs, microwaves).";
        if (kinds.Contains(FindingKind.SignalDegradation) || kinds.Contains(FindingKind.SignalInstability))
            return "Signal is weak or moving: check for new obstructions or interference near the device, and the scanner's antenna position. A USB extension cable keeps the dongle away from PC noise.";
        if (kinds.Contains(FindingKind.Environmental)) return "Outages were shared with other devices: look at the scanner, its USB port or the room, not at this device.";
        return "No action needed.";
    }

    private static int BadDays(List<TimeRange> own, TimeRange window) =>
        own.Where(r => r.Length >= TimeSpan.FromMinutes(5)).Select(r => (int)Math.Floor((r.StartUtc - window.StartUtc).TotalDays)).Distinct().Count();

    private static double Overlap(TimeRange a, TimeRange b)
    {
        var s = a.StartUtc > b.StartUtc ? a.StartUtc : b.StartUtc; var e = a.EndUtc < b.EndUtc ? a.EndUtc : b.EndUtc;
        return e > s ? (e - s).TotalSeconds : 0;
    }

    /// <summary>
    /// Keeps only the parts of each silence that happened while the scanner was listening and after the device was first heard,
    /// and drops pieces shorter than the minimum gap. Shared by the Fault Detector and the Signal Analysis page.
    /// </summary>
    public static IReadOnlyList<TimeRange> CutToCoverage(IReadOnlyList<TimeRange> silences, IReadOnlyList<TimeRange> coverage, TimeRange window, DateTime? firstSeenUtc, int minGapSeconds)
    {
        var covered = Merge(coverage.Select(c => Clip(c, window)).Where(c => c.Length > TimeSpan.Zero).ToList());
        var list = new List<TimeRange>();
        foreach (var sil in silences)
        {
            var from = firstSeenUtc is { } f && f > sil.StartUtc ? f : sil.StartUtc;
            if (from >= sil.EndUtc) continue;
            foreach (var piece in Intersect(new TimeRange(from, sil.EndUtc), covered))
                if (piece.Length >= TimeSpan.FromSeconds(minGapSeconds)) list.Add(piece);
        }
        return list;
    }

    private static TimeRange Clip(TimeRange r, TimeRange w) => new(r.StartUtc < w.StartUtc ? w.StartUtc : r.StartUtc, r.EndUtc > w.EndUtc ? w.EndUtc : r.EndUtc);

    private static List<TimeRange> Merge(List<TimeRange> list)
    {
        var sorted = list.OrderBy(r => r.StartUtc).ToList(); var res = new List<TimeRange>();
        foreach (var r in sorted)
        {
            if (res.Count > 0 && r.StartUtc <= res[^1].EndUtc) res[^1] = new TimeRange(res[^1].StartUtc, r.EndUtc > res[^1].EndUtc ? r.EndUtc : res[^1].EndUtc);
            else res.Add(r);
        }
        return res;
    }

    private static IEnumerable<TimeRange> Intersect(TimeRange r, List<TimeRange> covered)
    {
        foreach (var c in covered)
        {
            if (c.StartUtc >= r.EndUtc) yield break;
            if (c.EndUtc <= r.StartUtc) continue;
            yield return new TimeRange(c.StartUtc > r.StartUtc ? c.StartUtc : r.StartUtc, c.EndUtc < r.EndUtc ? c.EndUtc : r.EndUtc);
        }
    }

    private static double StdDev(List<double> v) { var m = v.Average(); return Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / v.Count); }
    private static double Clamp(double v) => Math.Max(0, Math.Min(100, v));
}
