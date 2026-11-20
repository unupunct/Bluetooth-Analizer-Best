using System.Collections.ObjectModel;
using BluetoothFleetMonitor.Application.Scanning;
using BluetoothFleetMonitor.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BluetoothFleetMonitor.Desktop.ViewModels;

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly ScanController _scan;
    private readonly LiveRegistry _registry;

    public ObservableCollection<EventRow> Events { get; }

    [ObservableProperty] private int _registered;
    [ObservableProperty] private int _detected;
    [ObservableProperty] private int _missing;
    [ObservableProperty] private int _uncertain;
    [ObservableProperty] private string _scannerStateText = "";
    [ObservableProperty] private string _scannerMessage = "";
    [ObservableProperty] private bool _scannerWarning;
    [ObservableProperty] private string _adapter = "";
    [ObservableProperty] private string _lastUpdate = "never";
    [ObservableProperty] private string _rate = "";
    [ObservableProperty] private bool _canStart;
    [ObservableProperty] private bool _canStop;

    public DashboardViewModel(ScanController scan, LiveRegistry registry, EventFeed feed)
    {
        _scan = scan; _registry = registry; Events = feed.Items;
        scan.Changed += Refresh;
        Refresh();
    }

    private void Refresh()
    {
        Registered = _registry.Registered;
        Detected = _registry.Detected;
        Missing = _registry.Missing;
        Uncertain = _registry.Uncertain;
        var s = _scan.Status;
        ScannerStateText = s.State switch
        {
            ScannerState.RunningNoAdvertisements => "Running, hearing nothing",
            ScannerState.AdapterUnavailable => "Adapter unavailable",
            _ => s.State.ToString(),
        };
        ScannerMessage = s.Message;
        ScannerWarning = s.State is ScannerState.RunningNoAdvertisements or ScannerState.Interrupted or ScannerState.AdapterUnavailable or ScannerState.Error;
        Adapter = _scan.AdapterDescription ?? "unknown adapter";
        LastUpdate = _registry.LastUpdateUtc is { } t ? t.ToLocalTime().ToString("HH:mm:ss") : "no data yet";
        Rate = $"{_scan.AdvertisementsPerSecond:0} adv/s";
        CanStart = s.State is ScannerState.Stopped or ScannerState.Interrupted or ScannerState.AdapterUnavailable or ScannerState.Error;
        CanStop = !CanStart;
    }

    [RelayCommand] private Task StartScan() => _scan.StartScanAsync();
    [RelayCommand] private Task StopScan() => _scan.StopScanAsync();
}
