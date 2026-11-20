using BluetoothFleetMonitor.Application.Scanning;
using BluetoothFleetMonitor.Domain;

namespace BluetoothFleetMonitor.Tests;

public class ScannerHealthTests
{
    private DateTime _now = new(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
    private ScannerHealth NewHealth() => new(TimeSpan.FromSeconds(10), () => _now);

    [Fact]
    public void Running_with_a_recent_advertisement_is_Running()
    {
        var h = NewHealth();
        h.Started();
        _now = _now.AddSeconds(2);
        h.AdvertisementReceived();
        _now = _now.AddSeconds(3);

        Assert.Equal(ScannerState.Running, h.Current().State);
    }

    [Fact]
    public void Running_but_hearing_nothing_reports_it_and_points_at_the_driver()
    {
        var h = NewHealth();
        h.Started();
        _now = _now.AddSeconds(11);

        var s = h.Current();

        Assert.Equal(ScannerState.RunningNoAdvertisements, s.State);
        Assert.Contains("driver", s.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Right_after_start_with_no_data_yet_is_not_yet_a_warning()
    {
        var h = NewHealth();
        h.Started();
        _now = _now.AddSeconds(3);
        Assert.Equal(ScannerState.Running, h.Current().State);
    }

    [Fact]
    public void Advertisements_resuming_clear_the_no_advertisement_state()
    {
        var h = NewHealth();
        h.Started();
        _now = _now.AddSeconds(30);
        Assert.Equal(ScannerState.RunningNoAdvertisements, h.Current().State);

        h.AdvertisementReceived();

        Assert.Equal(ScannerState.Running, h.Current().State);
    }

    [Fact]
    public void Unexpected_stop_is_Interrupted_and_opens_a_coverage_gap_that_closes_on_restart()
    {
        var h = NewHealth();
        h.Started();
        var stoppedAt = _now.AddSeconds(5);
        _now = stoppedAt;
        h.Stopped(requestedByUser: false, reason: "RadioNotAvailable");

        Assert.Equal(ScannerState.Interrupted, h.Current().State);
        var open = Assert.Single(h.Gaps);
        Assert.Equal(stoppedAt, open.StartUtc);
        Assert.Null(open.EndUtc);
        Assert.Equal("RadioNotAvailable", open.Reason);

        _now = _now.AddSeconds(20);
        h.Started();

        var closed = Assert.Single(h.Gaps);
        Assert.Equal(_now, closed.EndUtc);
        Assert.Equal(ScannerState.Running, h.Current().State);
    }

    [Fact]
    public void Stop_requested_by_the_user_is_Stopped_and_is_not_a_coverage_gap()
    {
        var h = NewHealth();
        h.Started();
        h.Stopped(requestedByUser: true, reason: null);

        Assert.Equal(ScannerState.Stopped, h.Current().State);
        Assert.Empty(h.Gaps);
    }

    [Fact]
    public void Adapter_unavailable_is_reported_and_counts_as_a_gap()
    {
        var h = NewHealth();
        h.AdapterUnavailable("Bluetooth is turned off");

        var s = h.Current();
        Assert.Equal(ScannerState.AdapterUnavailable, s.State);
        Assert.Contains("turned off", s.Message);
        Assert.Single(h.Gaps);
    }

    [Theory]
    [InlineData(AddressTypeHint.Public, 0x001122334455UL, AddressKind.Public)]
    [InlineData(AddressTypeHint.Random, 0xC01122334455UL, AddressKind.RandomStatic)]
    [InlineData(AddressTypeHint.Random, 0x601122334455UL, AddressKind.RandomResolvablePrivate)]
    [InlineData(AddressTypeHint.Random, 0x201122334455UL, AddressKind.RandomNonResolvablePrivate)]
    [InlineData(AddressTypeHint.Unspecified, 0xC01122334455UL, AddressKind.Unknown)]
    public void Address_kind_is_derived_from_the_type_and_the_top_two_address_bits(AddressTypeHint hint, ulong address, AddressKind expected)
    {
        Assert.Equal(expected, AddressKindClassifier.Classify(hint, address));
    }
}
