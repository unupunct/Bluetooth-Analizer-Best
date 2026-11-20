namespace BluetoothFleetMonitor.Application.Scanning;

/// <summary>
/// Pure state machine that turns scanner events into an honest status and a list of coverage gaps.
/// Keeps "the scanner was blind" separate from "the device was silent".
/// </summary>
public sealed class ScannerHealth
{
    public const string DriverHint =
        "The scanner is running but has received no advertisements. This is usually a driver problem, not an empty room: " +
        "on Windows 10 an adapter that uses the generic Microsoft driver (many Realtek USB adapters) starts scanning but hears nothing. " +
        "Install the adapter manufacturer's Bluetooth driver.";

    private readonly TimeSpan _noAdvertisementThreshold;
    private readonly Func<DateTime> _clock;
    private readonly object _lock = new();
    private readonly List<CoverageGap> _gaps = new();

    private bool _running;
    private bool _stoppedByUser = true;
    private DateTime _startedUtc;
    private DateTime? _lastAdvertisementUtc;
    private string? _problem;
    private bool _adapterUnavailable;
    private DateTime _changedUtc;

    public ScannerHealth(TimeSpan noAdvertisementThreshold, Func<DateTime>? clock = null)
    {
        _noAdvertisementThreshold = noAdvertisementThreshold;
        _clock = clock ?? (() => DateTime.UtcNow);
        _changedUtc = _clock();
    }

    public IReadOnlyList<CoverageGap> Gaps { get { lock (_lock) return _gaps.ToList(); } }

    public void Started()
    {
        lock (_lock)
        {
            var now = _clock();
            CloseOpenGap(now);
            _running = true;
            _stoppedByUser = false;
            _adapterUnavailable = false;
            _problem = null;
            _startedUtc = now;
            _changedUtc = now;
        }
    }

    public void AdvertisementReceived()
    {
        lock (_lock) _lastAdvertisementUtc = _clock();
    }

    public void Stopped(bool requestedByUser, string? reason)
    {
        lock (_lock)
        {
            var now = _clock();
            _running = false;
            _stoppedByUser = requestedByUser;
            _problem = reason;
            _changedUtc = now;
            if (!requestedByUser) OpenGap(now, reason ?? "Scanner stopped unexpectedly");
        }
    }

    public void AdapterUnavailable(string reason)
    {
        lock (_lock)
        {
            var now = _clock();
            _running = false;
            _stoppedByUser = false;
            _adapterUnavailable = true;
            _problem = reason;
            _changedUtc = now;
            OpenGap(now, reason);
        }
    }

    public ScannerStatus Current()
    {
        lock (_lock)
        {
            var now = _clock();
            if (_adapterUnavailable)
                return new(ScannerState.AdapterUnavailable, _problem ?? "Bluetooth adapter unavailable", _lastAdvertisementUtc, _changedUtc);
            if (!_running)
                return _stoppedByUser
                    ? new(ScannerState.Stopped, "Scanner stopped", _lastAdvertisementUtc, _changedUtc)
                    : new(ScannerState.Interrupted, $"Scanner was interrupted: {_problem ?? "unknown reason"}", _lastAdvertisementUtc, _changedUtc);

            var reference = _lastAdvertisementUtc ?? _startedUtc;
            if (now - reference > _noAdvertisementThreshold)
                return new(ScannerState.RunningNoAdvertisements, DriverHint, _lastAdvertisementUtc, _changedUtc);

            return new(ScannerState.Running, _lastAdvertisementUtc is null ? "Listening, waiting for the first advertisement" : "Listening", _lastAdvertisementUtc, _changedUtc);
        }
    }

    private void OpenGap(DateTime now, string reason)
    {
        if (_gaps.Count > 0 && _gaps[^1].EndUtc is null) return; // already in a gap
        _gaps.Add(new CoverageGap(now, null, reason));
    }

    private void CloseOpenGap(DateTime now)
    {
        if (_gaps.Count > 0 && _gaps[^1].EndUtc is null)
            _gaps[^1] = _gaps[^1] with { EndUtc = now };
    }
}
