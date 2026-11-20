using BluetoothFleetMonitor.Application.Scanning;

namespace BluetoothFleetMonitor.Tests;

public class PresenceStatusTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(120);
    private static readonly IReadOnlyList<CoverageGap> NoGaps = Array.Empty<CoverageGap>();

    [Fact]
    public void Heard_within_the_window_is_Detected()
        => Assert.Equal(DevicePresence.Detected,
            PresenceStatus.Evaluate(Now.AddSeconds(-30), Now, Window, ScannerState.Running, NoGaps));

    [Fact]
    public void Silent_past_the_window_with_a_healthy_scanner_is_Missing_from_recent_scans()
        => Assert.Equal(DevicePresence.Missing,
            PresenceStatus.Evaluate(Now.AddMinutes(-10), Now, Window, ScannerState.Running, NoGaps));

    [Fact]
    public void Silent_but_the_scanner_is_not_running_is_Uncertain_not_Missing()
    {
        foreach (var s in new[] { ScannerState.Stopped, ScannerState.Interrupted, ScannerState.AdapterUnavailable, ScannerState.RunningNoAdvertisements, ScannerState.Error })
            Assert.Equal(DevicePresence.Uncertain, PresenceStatus.Evaluate(Now.AddMinutes(-10), Now, Window, s, NoGaps));
    }

    [Fact]
    public void Silence_that_overlaps_a_scanner_coverage_gap_is_Uncertain()
    {
        var gaps = new[] { new CoverageGap(Now.AddMinutes(-8), Now.AddMinutes(-5), "RadioNotAvailable") };
        Assert.Equal(DevicePresence.Uncertain,
            PresenceStatus.Evaluate(Now.AddMinutes(-10), Now, Window, ScannerState.Running, gaps));
    }

    [Fact]
    public void A_gap_that_ended_before_the_device_was_last_heard_does_not_matter()
    {
        var gaps = new[] { new CoverageGap(Now.AddHours(-3), Now.AddHours(-2), "old") };
        Assert.Equal(DevicePresence.Missing,
            PresenceStatus.Evaluate(Now.AddMinutes(-10), Now, Window, ScannerState.Running, gaps));
    }

    [Fact]
    public void A_still_open_gap_makes_it_Uncertain()
    {
        var gaps = new[] { new CoverageGap(Now.AddMinutes(-1), null, "radio off") };
        Assert.Equal(DevicePresence.Uncertain,
            PresenceStatus.Evaluate(Now.AddMinutes(-10), Now, Window, ScannerState.Running, gaps));
    }

    [Fact]
    public void Never_heard_is_Uncertain()
        => Assert.Equal(DevicePresence.Uncertain,
            PresenceStatus.Evaluate(null, Now, Window, ScannerState.Running, NoGaps));
}
