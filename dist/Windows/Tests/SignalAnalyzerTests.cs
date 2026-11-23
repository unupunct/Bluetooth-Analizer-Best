using BluetoothFleetMonitor.Application.Diagnostics;

namespace BluetoothFleetMonitor.Tests;

public class SignalAnalyzerTests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlyDictionary<int, int> NoIntervals = new Dictionary<int, int>();

    private static SignalBucket B(int minute, double avg, int min = int.MinValue, int max = int.MinValue, int count = 10)
        => new(T0.AddMinutes(minute), avg, min == int.MinValue ? (int)avg - 2 : min, max == int.MinValue ? (int)avg + 2 : max, count);

    private static List<SignalBucket> Series(params double[] averages) => averages.Select((a, i) => B(i, a)).ToList();
    private static List<SignalBucket> Repeat(double level, int n, int startMinute = 0) => Enumerable.Range(0, n).Select(i => B(startMinute + i, level)).ToList();

    private static SignalInput Input(IReadOnlyList<SignalBucket> buckets, IReadOnlyDictionary<int, int>? intervals = null, double maxInterval = 0,
        IReadOnlyList<TimeRange>? silences = null)
        => new(new TimeRange(T0, T0.AddMinutes(Math.Max(buckets.Count, 1))), 60, buckets, intervals ?? NoIntervals, maxInterval, silences ?? Array.Empty<TimeRange>());

    [Fact]
    public void Fewer_than_three_buckets_is_insufficient_with_a_note()
    {
        var a = SignalAnalyzer.Analyze(Input(Series(-60, -61)));
        Assert.False(a.Sufficient);
        Assert.Contains("Not enough data", a.Note);
        Assert.Null(a.Stats);
    }

    [Fact]
    public void Empty_input_is_insufficient_and_does_not_throw()
    {
        var a = SignalAnalyzer.Analyze(Input(new List<SignalBucket>()));
        Assert.False(a.Sufficient);
        Assert.Empty(a.MovingAverage);
        Assert.Empty(a.Recoveries);
    }

    [Fact]
    public void Stats_are_weighted_and_exact_for_min_max()
    {
        var buckets = new List<SignalBucket> { B(0, -60, -62, -58, 10), B(1, -70, -72, -68, 10), B(2, -80, -85, -78, 20) };
        var s = SignalAnalyzer.Analyze(Input(buckets)).Stats!;
        Assert.Equal(-85, s.Min);
        Assert.Equal(-58, s.Max);
        Assert.Equal(-72.5, s.Mean, 3);       // weighted by count: (-600 -700 -1600) / 40
        Assert.Equal(-70, s.Median, 3);       // weighted median of bucket averages
    }

    [Fact]
    public void Moving_average_has_one_value_per_bucket_and_smooths()
    {
        var alt = Enumerable.Range(0, 20).Select(i => i % 2 == 0 ? -60.0 : -80.0).ToArray();
        var a = SignalAnalyzer.Analyze(Input(Series(alt)), movingAverageBuckets: 4);
        Assert.Equal(20, a.MovingAverage.Count);
        foreach (var v in a.MovingAverage.Skip(3)) Assert.Equal(-70, v, 2);
    }

    [Theory]
    [InlineData(40, 3)]
    [InlineData(200, 10)]
    [InlineData(1000, 50)]
    public void DefaultMovingAverage_is_about_five_percent_with_a_floor_of_three(int buckets, int expected)
        => Assert.Equal(expected, SignalAnalyzer.DefaultMovingAverage(buckets));

    [Fact]
    public void Volatility_is_the_standard_deviation_of_bucket_to_bucket_change()
    {
        Assert.Equal(0, SignalAnalyzer.Analyze(Input(Repeat(-60, 30))).Stats!.Volatility, 3);
        var alt = Enumerable.Range(0, 30).Select(i => i % 2 == 0 ? -60.0 : -80.0).ToArray();
        var v = SignalAnalyzer.Analyze(Input(Series(alt))).Stats!.Volatility;
        Assert.InRange(v, 19, 21);          // changes alternate +20 / -20
    }

    [Fact]
    public void Distribution_uses_5_dB_bins_and_strength_shares_sum_to_one()
    {
        var a = SignalAnalyzer.Analyze(Input(Series(-50, -50, -90, -90, -70)));
        Assert.Equal(1.0, a.Strength.Sum(s => s.Fraction), 3);
        Assert.All(a.Distribution, b => { Assert.Equal(0, b.FromDbm % 5); Assert.Equal(b.FromDbm + 5, b.ToDbm); });
        Assert.True(a.Strength.Single(s => s.Label == "Excellent").Fraction > 0);
        Assert.True(a.Strength.Single(s => s.Label == "Very weak").Fraction > 0);
    }

    [Theory]
    [InlineData(-55, "Excellent")]
    [InlineData(-56, "Good")]
    [InlineData(-67, "Good")]
    [InlineData(-68, "Fair")]
    [InlineData(-78, "Fair")]
    [InlineData(-79, "Weak")]
    [InlineData(-88, "Weak")]
    [InlineData(-89, "Very weak")]
    public void Describe_matches_the_dashboard_cutoffs(double rssi, string expected) => Assert.Equal(expected, SignalAnalyzer.Describe(rssi));

    [Fact]
    public void Interval_median_p95_and_bins_come_from_the_second_histogram()
    {
        var counts = new Dictionary<int, int> { [1] = 50, [2] = 30, [5] = 15, [600] = 5 };
        var iv = SignalAnalyzer.Analyze(Input(Repeat(-60, 10), counts, maxInterval: 1234)).Intervals!;
        Assert.InRange(iv.MedianSeconds, 1, 2);
        Assert.True(iv.P95Seconds <= 5.5);
        Assert.Equal(1234, iv.MaxSeconds);
        int Bin(string label) => iv.Bins.Single(b => b.Label == label).Count;
        Assert.Equal(0, Bin("<1"));
        Assert.Equal(50, Bin("1-2"));         // bins are half-open [a,b): s=1 is "1-2", s=2 is "2-5"
        Assert.Equal(30, Bin("2-5"));
        Assert.Equal(15, Bin("5-10"));
        Assert.Equal(5, Bin(">120"));
    }

    [Fact]
    public void Silence_details_carry_the_level_before_and_after()
    {
        var buckets = Repeat(-60, 10).Concat(Repeat(-72, 10, 20)).ToList();
        var silence = new TimeRange(T0.AddMinutes(10), T0.AddMinutes(20));
        var d = SignalAnalyzer.Analyze(Input(buckets, silences: new[] { silence })).Silences.Single();
        Assert.Equal(-60, d.LevelBeforeDbm!.Value, 3);
        Assert.Equal(-72, d.LevelAfterDbm!.Value, 3);

        var atStart = SignalAnalyzer.Analyze(Input(Repeat(-60, 10, 5), silences: new[] { new TimeRange(T0, T0.AddMinutes(5)) })).Silences.Single();
        Assert.Null(atStart.LevelBeforeDbm);
    }

    [Fact]
    public void Recovery_is_measured_from_a_ten_dB_drop_until_within_three_dB_of_baseline()
    {
        var buckets = Repeat(-60, 70).Concat(Repeat(-75, 5, 70)).Concat(Repeat(-61, 10, 75)).ToList();
        var r = SignalAnalyzer.Analyze(Input(buckets)).Recoveries.Single();
        Assert.InRange(r.DropDb, 14, 16);
        Assert.Equal(TimeSpan.FromMinutes(5), r.RecoveredAfter);
    }

    [Fact]
    public void A_drop_that_never_recovers_is_reported_as_unrecovered()
    {
        var buckets = Repeat(-60, 70).Concat(Repeat(-80, 20, 70)).ToList();
        var r = SignalAnalyzer.Analyze(Input(buckets)).Recoveries.Single();
        Assert.Null(r.RecoveredAfter);
    }

    [Fact]
    public void Small_wobble_is_not_a_drop()
    {
        var wobble = Enumerable.Range(0, 100).Select(i => i % 2 == 0 ? -56.0 : -64.0).ToArray();
        Assert.Empty(SignalAnalyzer.Analyze(Input(Series(wobble))).Recoveries);
    }
}
