using System.Collections.ObjectModel;
using BluetoothFleetMonitor.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BluetoothFleetMonitor.Desktop.ViewModels;

public sealed record NavItem(string Key, string Title, bool Built, string Note);

public sealed class PlaceholderViewModel
{
    public PlaceholderViewModel(string title, string phase, string description) { Title = title; Phase = phase; Description = description; }
    public string Title { get; }
    public string Phase { get; }
    public string Description { get; }
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly Dictionary<string, object> _pages = new();
    private readonly ScanController _scan;

    public ObservableCollection<NavItem> Items { get; } = new();

    [ObservableProperty] private NavItem? _selected;
    [ObservableProperty] private object? _current;
    [ObservableProperty] private string _statusBar = "";

    public MainViewModel(DashboardViewModel dashboard, LiveMonitorViewModel live, InventoryViewModel inventory,
        HistoryViewModel history, FaultsViewModel faults, EventLogViewModel events, ScannerViewModel scanner, SettingsViewModel settings,
        SelectionService selection, ScanController scan, AppPaths paths)
    {
        _scan = scan;
        void Add(string key, string title, object page, string note = "") { Items.Add(new NavItem(key, title, true, note)); _pages[key] = page; }
        void Todo(string key, string title, string phase, string description)
        {
            Items.Add(new NavItem(key, title, false, phase));
            _pages[key] = new PlaceholderViewModel(title, phase, description);
        }

        Add("dashboard", "Dashboard", dashboard);
        Add("live", "Live Monitor", live);
        Add("inventory", "Device Inventory", inventory);
        Add("faults", "Fault Detector", faults);
        Add("history", "Device History", history);
        Todo("analysis", "Signal Analysis", "Phase 4", "Moving averages, volatility, advertisement intervals and recovery times.");
        Todo("survey", "Signal Survey", "Phase 3", "Walk-around inspection results uploaded from the Android scanner.");
        Add("scanner", "Bluetooth Scanner", scanner);
        Todo("tests", "Connection Tests", "Phase 5", "Controlled, one-device-at-a-time connection tests with an explicit warning. Never automatic, never on every device.");
        Todo("usb", "USB Monitor", "Phase 6", "USB receiver arrival, removal and re-enumeration events.");
        Todo("mobile", "Mobile Devices", "Phase 3", "Paired Android scanners, pairing QR code and token management.");
        Add("events", "Event Log", events);
        Todo("reports", "Reports", "Phase 6", "Exportable diagnostic reports with optional anonymisation of Bluetooth addresses.");
        Add("settings", "Settings", settings);

        selection.OpenHistoryRequested += _ => Navigate("history");
        _statusBarPath = paths.DataDir;
        scan.Changed += UpdateStatusBar;
        UpdateStatusBar();
        Selected = Items[0];
    }

    private readonly string _statusBarPath;

    partial void OnSelectedChanged(NavItem? value)
    {
        if (value is not null && _pages.TryGetValue(value.Key, out var page))
        {
            Current = page;
            if (page is FaultsViewModel fv) _ = fv.RefreshAsync();   // analyse whenever the page is opened
        }
    }

    public void Navigate(string key) => Selected = Items.FirstOrDefault(i => i.Key == key) ?? Selected;

    private void UpdateStatusBar()
    {
        var s = _scan.Status;
        StatusBar = $"Scanner: {s.State}  |  {_scan.Mode} mode  |  {_scan.AdvertisementsPerSecond:0} adv/s  |  stored {_scan.Stored:N0}" +
                    (_scan.Overflowed > 0 ? $"  |  dropped {_scan.Overflowed:N0} (recorded)" : "") +
                    $"  |  data: {_statusBarPath}";
    }
}
