using BluetoothFleetMonitor.Application.Diagnostics;

namespace BluetoothFleetMonitor.Tests;

public class FaultAnalyzerTests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeRange Day = new(T0, T0.AddHours(24));
    private static readonly IReadOnlyList<TimeRange> Covered = new[] { Day };
    private static readonly AnalyzerSettings S = new();

    private static IReadOnlyList<RssiBucket> Steady(double level = -60, int n = 90, double wobble = 1)
        => Enumerable.Range(0, n).Select(i => new RssiBucket(T0.AddMinutes(i * 15), level + (i % 2 == 0 ? wobble : -wobble), 50)).ToList();

    private static DeviceEvidence Dev(string name, IEnumerable<TimeRange>? silences = null, IReadOnlyList<RssiBucket>? buckets = null, string? room = null,
        long obs = 50_000, int attempts = 0, int failures = 0)
        => new() { DeviceId = Guid.NewGuid(), Name = name, Room = room, Observations = obs, Silences = silences?.ToList() ?? new(), Buckets = buckets ?? Steady(), TestAttempts = attempts, TestFailures = failures };

    private static TimeRange Gap(int startMin, int lenMin) => new(T0.AddMinutes(startMin), T0.AddMinutes(startMin + lenMin));

    private static DeviceDiagnosis One(DeviceEvidence d, IReadOnlyList<TimeRange>? cov = null)
        => FaultAnalyzer.Analyze(new[] { d }, cov ?? Covered, Day, S).Devices.Single();

    [Fact]
    public void A_steady_device_is_healthy_with_good_confidence()
    {
        var r = One(Dev("steady"));
        Assert.Equal(HealthBand.Healthy, r.Band);
        Assert.True(r.Health >= 95);
        Assert.True(r.Confidence >= 0.9);
        Assert.DoesNotContain(r.Findings, f => f.Severity > 0);
    }

    [Fact]
    public void Repeated_dropouts_lower_health_and_are_reported()
    {
        var gaps = Enumerable.Range(0, 10).Select(i => Gap(60 + i * 120, 8)); // ten 8-minute dropouts
        var r = One(Dev("flaky", gaps));
        Assert.Equal(10, r.Interruptions);
        Assert.Equal(10, r.Flaps);
        Assert.True(r.Health < 60, $"health was {r.Health}");
        Assert.Contains(r.Findings, f => f.Kind == FindingKind.Flapping);
        Assert.Contains(r.Findings, f => f.Kind == FindingKind.PresenceInstability);
    }

    [Fact]
    public void Silence_while_the_scanner_was_not_listening_is_not_held_against_the_device()
    {
        var covered = new[] { new TimeRange(T0, T0.AddHours(6)), new TimeRange(T0.AddHours(12), T0.AddHours(24)) };
        var r = One(Dev("quiet", new[] { Gap(6 * 60 + 5, 5 * 60) }), covered); // silent only during the 6h-12h scanner outage
        Assert.Equal(0, r.Interruptions);
        Assert.Equal(0, r.MissingMinutes);
    }

    [Fact]
    public void A_silence_before_the_device_was_first_heard_is_not_a_fault()
    {
        var d = new DeviceEvidence { DeviceId = Guid.NewGuid(), Name = "new", Observations = 50_000, Buckets = Steady(), FirstSeenUtc = T0.AddHours(10), Silences = new[] { Gap(0, 600) } };
        Assert.Equal(0, One(d).Interruptions);
    }

    [Fact]
    public void Silence_shared_by_most_devices_is_environmental_not_a_device_fault()
    {
        var shared = Gap(300, 20);
        var devices = Enumerable.Range(0, 6).Select(i => Dev($"d{i}", new[] { shared })).ToList();
        var res = FaultAnalyzer.Analyze(devices, Covered, Day, S);
        Assert.Single(res.EnvironmentalEvents);
        Assert.All(res.Devices, d => { Assert.Equal(0, d.Interruptions); Assert.Equal(1, d.EnvironmentalSilences); Assert.Equal(HealthBand.Healthy, d.Band); });
    }

    [Fact]
    public void One_device_silent_while_the_others_are_fine_is_that_devices_fault()
    {
        var devices = Enumerable.Range(0, 6).Select(i => Dev($"d{i}", i == 0 ? new[] { Gap(300, 20), Gap(700, 25), Gap(1100, 30) } : null)).ToList();
        var res = FaultAnalyzer.Analyze(devices, Covered, Day, S);
        Assert.Empty(res.EnvironmentalEvents);
        Assert.Equal(3, res.Devices[0].Interruptions);
        Assert.Equal(0, res.Devices[1].Interruptions);
    }

    [Fact]
    public void A_room_wide_silence_is_attributed_to_the_room()
    {
        var shared = Gap(500, 30);
        var living = Enumerable.Range(0, 4).Select(i => Dev($"l{i}", new[] { shared }, room: "Studio A"));
        var others = Enumerable.Range(0, 8).Select(i => Dev($"o{i}", room: "Studio B"));
        var res = FaultAnalyzer.Analyze(living.Concat(others).ToList(), Covered, Day, S);
        Assert.Contains(res.EnvironmentalEvents, e => e.Room == "Studio A");
        Assert.All(res.Devices.Where(d => d.Room == "Studio A"), d => Assert.Equal(0, d.Interruptions));
    }

    [Fact]
    public void Too_little_data_gives_a_score_but_no_strong_diagnosis()
    {
        var r = One(Dev("fresh", obs: 20));
        Assert.Equal(HealthBand.NotEnoughData, r.Band);
        Assert.NotNull(r.Health);
        Assert.Contains("Not enough data", r.Recommendation);
    }

    [Fact]
    public void A_device_never_connection_tested_is_not_penalised_for_it()
    {
        var r = One(Dev("untested"));
        var tests = r.Components.Single(c => c.Name == "Connection tests");
        Assert.False(tests.Used);
        Assert.Contains("not counted as a failure", tests.Explanation);
    }

    [Fact]
    public void Failed_connection_tests_pull_health_down_and_are_a_finding()
    {
        var r = One(Dev("badlink", attempts: 10, failures: 7));
        Assert.Equal(0.7, r.TestFailureRate!.Value, 3);
        Assert.Contains(r.Findings, f => f.Kind == FindingKind.TestFailures && f.Severity == 3);
        Assert.True(r.Health < 95);
    }

    [Fact]
    public void A_falling_signal_is_reported_as_degradation()
    {
        var falling = Enumerable.Range(0, 90).Select(i => new RssiBucket(T0.AddMinutes(i * 15), -55 - i * 0.2, 50)).ToList(); // -55 -> -73
        var r = One(Dev("fading", buckets: falling));
        Assert.True(r.TrendDb < -10);
        Assert.Contains(r.Findings, f => f.Kind == FindingKind.SignalDegradation);
    }

    [Fact]
    public void A_jumpy_signal_scores_worse_than_a_steady_one()
    {
        var jumpy = Enumerable.Range(0, 90).Select(i => new RssiBucket(T0.AddMinutes(i * 15), -60 + (i % 2 == 0 ? 10 : -10), 50)).ToList();
        var steady = One(Dev("a")); var j = One(Dev("b", buckets: jumpy));
        Assert.True(j.SignalSpreadDb > 8);
        Assert.True(j.Health < steady.Health);
        Assert.Contains(j.Findings, f => f.Kind == FindingKind.SignalInstability);
    }

    [Fact]
    public void Problems_on_several_separate_days_flag_suspected_hardware()
    {
        var week = new TimeRange(T0, T0.AddDays(5));
        var gaps = Enumerable.Range(0, 5).SelectMany(day => new[] { new TimeRange(T0.AddDays(day).AddHours(3), T0.AddDays(day).AddHours(3).AddMinutes(20)), new TimeRange(T0.AddDays(day).AddHours(9), T0.AddDays(day).AddHours(9).AddMinutes(25)), new TimeRange(T0.AddDays(day).AddHours(15), T0.AddDays(day).AddHours(15).AddMinutes(30)) }).ToList();
        var dev = Dev("battery", gaps, Steady(n: 400));
        var peers = Enumerable.Range(0, 5).Select(i => Dev($"p{i}", buckets: Steady(n: 400))).ToList();
        var res = FaultAnalyzer.Analyze(peers.Prepend(dev).ToList(), new[] { week }, week, S);
        var d = res.Devices[0];
        Assert.Contains(d.Findings, f => f.Kind == FindingKind.HardwareSuspected);
        Assert.True(d.Health < 75);
    }

    [Fact]
    public void Weights_are_adjustable_and_renormalised_over_the_components_that_have_data()
    {
        var gaps = new[] { Gap(100, 30), Gap(400, 30) };
        var a = One(Dev("w", gaps));
        var heavy = new AnalyzerSettings { WeightPresence = 100, WeightSignal = 1, WeightRecovery = 1, WeightTrend = 1, WeightTests = 1 };
        var b = FaultAnalyzer.Analyze(new[] { Dev("w", gaps) }, Covered, Day, heavy).Devices.Single();
        Assert.True(b.Health < a.Health, "weighting presence more must lower the score of a device that dropped out");
        Assert.Equal(1.0, a.Components.Where(c => c.Used).Sum(c => c.Weight) / a.Components.Where(c => c.Used).Sum(c => c.Weight));
    }

    [Theory]
    [InlineData(95, HealthBand.Healthy)]
    [InlineData(80, HealthBand.Good)]
    [InlineData(65, HealthBand.Monitor)]
    [InlineData(45, HealthBand.Unstable)]
    [InlineData(10, HealthBand.Critical)]
    public void Bands_follow_the_spec_thresholds(double health, HealthBand expected) => Assert.Equal(expected, FaultAnalyzer.Band(health, 1.0, S));
}
