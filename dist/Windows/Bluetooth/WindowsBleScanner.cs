using BluetoothFleetMonitor.Application.Scanning;
using Microsoft.Extensions.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Storage.Streams;

namespace BluetoothFleetMonitor.Bluetooth;

/// <summary>
/// Passive BLE listener built on BluetoothLEAdvertisementWatcher. Passive mode sends no scan requests and never connects.
/// Reports its real state (including "running but deaf"), and restarts itself after the system interrupts it.
/// </summary>
public sealed class WindowsBleScanner : IBleScanner
{
    private static readonly TimeSpan NoAdvertisementThreshold = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan[] RestartBackoff =
        { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30) };

    private readonly ILogger<WindowsBleScanner>? _log;
    private readonly ScannerHealth _health = new(NoAdvertisementThreshold);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private BluetoothLEAdvertisementWatcher? _watcher;
    private Radio? _radio;
    private CancellationTokenSource? _loop;
    private ScanMode _mode;
    private bool _wantRunning;
    private ScannerStatus _last;

    public WindowsBleScanner(ILogger<WindowsBleScanner>? log = null)
    {
        _log = log;
        _last = _health.Current();
    }

    public ScannerStatus Status => _health.Current();
    public string? AdapterDescription { get; private set; }
    public IReadOnlyList<CoverageGap> CoverageGaps => _health.Gaps;

    public event Action<BleAdvertisement>? Advertisement;
    public event Action<ScannerStatus>? StatusChanged;

    public async Task StartAsync(ScanMode mode, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _mode = mode;
            _wantRunning = true;
            _loop?.Cancel();
            _loop = new CancellationTokenSource();
            await TryStartWatcherAsync();
            _ = Task.Run(() => SupervisorAsync(_loop.Token));
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _wantRunning = false;
            _loop?.Cancel();
            DetachWatcher(requestedByUser: true);
            if (_radio is not null) { _radio.StateChanged -= OnRadioStateChanged; _radio = null; }
            Publish();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    // ---- start / restart -------------------------------------------------------------------

    private async Task<bool> TryStartWatcherAsync()
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter is null)
        {
            _health.AdapterUnavailable("No Bluetooth adapter found");
            Publish();
            return false;
        }
        if (!adapter.IsLowEnergySupported)
        {
            _health.AdapterUnavailable("This Bluetooth adapter does not support Low Energy");
            Publish();
            return false;
        }

        AdapterDescription = await DescribeAdapterAsync(adapter);
        await WatchRadioAsync(adapter);
        if (_radio is { State: not RadioState.On })
        {
            _health.AdapterUnavailable($"Bluetooth radio is {_radio.State}");
            Publish();
            return false;
        }

        DetachWatcher(requestedByUser: true, quiet: true);
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = _mode == ScanMode.Active ? BluetoothLEScanningMode.Active : BluetoothLEScanningMode.Passive,
        };
        watcher.Received += OnReceived;
        watcher.Stopped += OnStopped;
        _watcher = watcher;
        try
        {
            watcher.Start();
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "BluetoothLEAdvertisementWatcher.Start failed");
            _health.Stopped(requestedByUser: false, reason: ex.Message);
            Publish();
            return false;
        }
        _health.Started();
        _log?.LogInformation("BLE scanner started in {Mode} mode on {Adapter}", _mode, AdapterDescription);
        Publish();
        return true;
    }

    private async Task WatchRadioAsync(BluetoothAdapter adapter)
    {
        try
        {
            var radio = await adapter.GetRadioAsync();
            if (radio is null || ReferenceEquals(radio, _radio)) return;
            if (_radio is not null) _radio.StateChanged -= OnRadioStateChanged;
            _radio = radio;
            _radio.StateChanged += OnRadioStateChanged;
        }
        catch (Exception ex) { _log?.LogWarning(ex, "Could not read the Bluetooth radio state"); }
    }

    private static async Task<string?> DescribeAdapterAsync(BluetoothAdapter adapter)
    {
        try
        {
            var info = await DeviceInformation.CreateFromIdAsync(adapter.DeviceId);
            return info?.Name;
        }
        catch { return null; }
    }

    private void DetachWatcher(bool requestedByUser, bool quiet = false)
    {
        var w = _watcher;
        _watcher = null;
        if (w is null) { if (!quiet && requestedByUser) _health.Stopped(true, null); return; }
        w.Received -= OnReceived;
        w.Stopped -= OnStopped;
        try { if (w.Status is BluetoothLEAdvertisementWatcherStatus.Started or BluetoothLEAdvertisementWatcherStatus.Created) w.Stop(); }
        catch (Exception ex) { _log?.LogWarning(ex, "Error stopping watcher"); }
        if (!quiet) _health.Stopped(requestedByUser, null);
    }

    // ---- events ----------------------------------------------------------------------------

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        try
        {
            _health.AdvertisementReceived();

            // -127 dBm is the platform's "no signal strength available" marker; it is not a measurement.
            if (args.RawSignalStrengthInDBm == -127) return;

            // BluetoothAddressType exists from Windows 10 2004 (19041). On older builds the kind is Unknown,
            // which the identity resolver treats as "do not trust this address".
            var hint = AddressTypeHint.Unspecified;
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                hint = args.BluetoothAddressType switch
                {
                    BluetoothAddressType.Public => AddressTypeHint.Public,
                    BluetoothAddressType.Random => AddressTypeHint.Random,
                    _ => AddressTypeHint.Unspecified,
                };
            }
            var adv = args.Advertisement;
            var manufacturer = adv.ManufacturerData
                .Select(m => new ManufacturerBlock(m.CompanyId, ToBytes(m.Data)))
                .ToList();
            var name = string.IsNullOrWhiteSpace(adv.LocalName) ? null : adv.LocalName;

            Advertisement?.Invoke(new BleAdvertisement(
                ObservationId: Guid.NewGuid(),
                ObservedUtc: args.Timestamp.UtcDateTime,
                Address: args.BluetoothAddress,
                AddressKind: AddressKindClassifier.Classify(hint, args.BluetoothAddress),
                Rssi: args.RawSignalStrengthInDBm,
                Name: name,
                Manufacturer: manufacturer,
                ServiceUuids: adv.ServiceUuids.ToList(),
                Payload: SerialiseSections(adv)));
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Error handling an advertisement");
        }
    }

    private void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        // Our own Stop() detaches the handler first, so reaching here means the system stopped it.
        _log?.LogWarning("BLE watcher stopped by the system: {Error}", args.Error);
        _health.Stopped(requestedByUser: false, reason: args.Error.ToString());
        Publish();
    }

    private void OnRadioStateChanged(Radio sender, object args)
    {
        _log?.LogInformation("Bluetooth radio state: {State}", sender.State);
        if (sender.State != RadioState.On && _wantRunning)
        {
            DetachWatcher(requestedByUser: false, quiet: true);
            _health.AdapterUnavailable($"Bluetooth radio is {sender.State}");
            Publish();
        }
        // The supervisor loop restarts the watcher once the radio is back on.
    }

    // ---- supervisor ------------------------------------------------------------------------

    private async Task SupervisorAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { return; }

            var status = _health.Current();
            if (status.State is ScannerState.Interrupted or ScannerState.AdapterUnavailable && _wantRunning)
            {
                var wait = RestartBackoff[Math.Min(failures, RestartBackoff.Length - 1)];
                try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { return; }
                await _gate.WaitAsync(ct);
                try
                {
                    if (!_wantRunning) return;
                    failures = await TryStartWatcherAsync() ? 0 : failures + 1;
                }
                catch (Exception ex) { failures++; _log?.LogError(ex, "Restart attempt failed"); }
                finally { _gate.Release(); }
            }
            else
            {
                failures = 0;
                Publish(); // keeps the "running but deaf" state fresh
            }
        }
    }

    private void Publish()
    {
        var s = _health.Current();
        if (s.State == _last.State && s.Message == _last.Message) return;
        _last = s;
        try { StatusChanged?.Invoke(s); } catch (Exception ex) { _log?.LogError(ex, "StatusChanged handler failed"); }
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static byte[] ToBytes(IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>Rebuilds the raw AD structure: [length][type][data]... for every section received.</summary>
    private static byte[] SerialiseSections(BluetoothLEAdvertisement adv)
    {
        using var ms = new MemoryStream();
        foreach (var s in adv.DataSections)
        {
            var data = ToBytes(s.Data);
            ms.WriteByte((byte)Math.Min(data.Length + 1, 255));
            ms.WriteByte(s.DataType);
            ms.Write(data, 0, data.Length);
        }
        return ms.ToArray();
    }
}
