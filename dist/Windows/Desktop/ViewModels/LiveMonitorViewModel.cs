using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using BluetoothFleetMonitor.Application.Csv;
using BluetoothFleetMonitor.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BluetoothFleetMonitor.Desktop.ViewModels;

public sealed partial class LiveMonitorViewModel : ObservableObject
{
    private readonly ScanController _scan;
    private readonly SelectionService _selection;

    public ICollectionView View { get; }
    public string[] StatusFilters { get; } = { "All", "Detected", "Missing", "Uncertain" };
    public string[] SignalFilters { get; } = { "Any signal", "Excellent / Good", "Fair", "Weak / Very weak" };

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _statusFilter = "All";
    [ObservableProperty] private string _signalFilter = "Any signal";
    [ObservableProperty] private string _roomFilter = "";
    [ObservableProperty] private string _categoryFilter = "";
    [ObservableProperty] private string _manufacturerFilter = "";
    [ObservableProperty] private bool _scanning;
    [ObservableProperty] private string _count = "";
    [ObservableProperty] private LiveDevice? _selected;

    public LiveMonitorViewModel(LiveRegistry registry, ScanController scan, SelectionService selection)
    {
        _scan = scan; _selection = selection;
        View = new ListCollectionView(registry.Devices); // own view: filters here must not leak into the pickers on other pages
        View.Filter = Filter;
        View.SortDescriptions.Add(new SortDescription(nameof(LiveDevice.LastSeenUtc), ListSortDirection.Descending));
        scan.Changed += () => { Scanning = scan.Status.State is Application.Scanning.ScannerState.Running or Application.Scanning.ScannerState.RunningNoAdvertisements; Count = $"{View.Cast<object>().Count()} of {registry.Registered} devices"; };
    }

    partial void OnSearchChanged(string value) => View.Refresh();
    partial void OnStatusFilterChanged(string value) => View.Refresh();
    partial void OnSignalFilterChanged(string value) => View.Refresh();
    partial void OnRoomFilterChanged(string value) => View.Refresh();
    partial void OnCategoryFilterChanged(string value) => View.Refresh();
    partial void OnManufacturerFilterChanged(string value) => View.Refresh();

    private bool Filter(object o)
    {
        var d = (LiveDevice)o;
        if (Search.Length > 0 && !(d.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
                                   || (d.Manufacturer?.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false)
                                   || (d.Room?.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false)
                                   || d.Id.ToString().Contains(Search, StringComparison.OrdinalIgnoreCase))) return false;
        if (StatusFilter != "All" && d.Status != StatusFilter) return false;
        if (RoomFilter.Length > 0 && !(d.Room?.Contains(RoomFilter, StringComparison.OrdinalIgnoreCase) ?? false)) return false;
        if (CategoryFilter.Length > 0 && !d.Category.Contains(CategoryFilter, StringComparison.OrdinalIgnoreCase)) return false;
        if (ManufacturerFilter.Length > 0 && !(d.Manufacturer?.Contains(ManufacturerFilter, StringComparison.OrdinalIgnoreCase) ?? false)) return false;
        return SignalFilter switch
        {
            "Excellent / Good" => d.RssiQuality is "Excellent" or "Good",
            "Fair" => d.RssiQuality == "Fair",
            "Weak / Very weak" => d.RssiQuality is "Weak" or "Very weak",
            _ => true,
        };
    }

    [RelayCommand]
    private async Task ToggleScan()
    {
        if (Scanning) await _scan.StopScanAsync(); else await _scan.StartScanAsync();
    }

    [RelayCommand]
    private void OpenHistory()
    {
        if (Selected is not null) _selection.OpenHistory(Selected.Id);
    }

    [RelayCommand]
    private void ExportView()
    {
        var path = Dialogs.SaveFile("Export current results", $"live-monitor-{DateTime.Now:yyyyMMdd-HHmmss}.csv", "CSV (*.csv)|*.csv");
        if (path is null) return;
        var rows = new List<IReadOnlyList<string?>> { new[] { "Device", "Type", "RssiDbm", "Signal", "Trend", "LastSeen", "Source", "Room", "Status", "Observations" } };
        foreach (var d in View.Cast<LiveDevice>())
            rows.Add(new[] { d.Name, d.Type, d.Rssi?.ToString(), d.RssiQuality, d.Trend, d.LastSeenUtc?.ToString("o"), d.Source, d.Room, d.Status, d.Observations.ToString() });
        File.WriteAllText(path, CsvTable.Write(rows));
    }
}
