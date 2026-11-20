using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using BluetoothFleetMonitor.Application.Scanning;
using BluetoothFleetMonitor.Database;
using BluetoothFleetMonitor.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BluetoothFleetMonitor.Desktop.ViewModels;

public sealed partial class EventLogViewModel : ObservableObject
{
    public ICollectionView View { get; }
    public string[] Severities { get; } = { "All", "Info", "Warning", "Error", "Critical" };

    [ObservableProperty] private string _severity = "All";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _count = "";

    public EventLogViewModel(EventFeed feed)
    {
        View = new ListCollectionView(feed.Items);
        View.Filter = o =>
        {
            var e = (EventRow)o;
            if (Severity != "All" && e.Severity != Severity) return false;
            return Search.Length == 0 || e.Message.Contains(Search, StringComparison.OrdinalIgnoreCase)
                || e.Source.Contains(Search, StringComparison.OrdinalIgnoreCase) || e.Type.Contains(Search, StringComparison.OrdinalIgnoreCase);
        };
        feed.Items.CollectionChanged += (_, _) => Count = $"{View.Cast<object>().Count()} events shown (newest 300 kept in view)";
    }

    partial void OnSeverityChanged(string value) => View.Refresh();
    partial void OnSearchChanged(string value) => View.Refresh();
}

public sealed partial class ScannerViewModel : ObservableObject
{
    private readonly ScanController _scan;
    private readonly SettingsStore _settings;

    public ObservableCollection<string> Gaps { get; } = new();

    [ObservableProperty] private string _state = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _warning;
    [ObservableProperty] private string _adapter = "";
    [ObservableProperty] private string _mode = "";
    [ObservableProperty] private bool _allowActive;
    [ObservableProperty] private string _counters = "";
    [ObservableProperty] private bool _canStart;
    [ObservableProperty] private bool _canStop;
    private bool _loading;

    public ScannerViewModel(ScanController scan, SettingsStore settings)
    {
        _scan = scan; _settings = settings;
        _loading = true;
        AllowActive = settings.Current.ScanMode.Equals("Active", StringComparison.OrdinalIgnoreCase);
        _loading = false;
        scan.Changed += Refresh;
        Refresh();
    }

    private void Refresh()
    {
        var s = _scan.Status;
        State = s.State.ToString();
        Message = s.Message;
        Warning = s.State is ScannerState.RunningNoAdvertisements or ScannerState.Interrupted or ScannerState.AdapterUnavailable or ScannerState.Error;
        Adapter = _scan.AdapterDescription ?? "not detected yet";
        Mode = _scan.Mode == ScanMode.Passive ? "Passive: listens only, sends nothing" : "Active: may send scan requests to nearby devices";
        Counters = $"{_scan.AdvertisementsReceived:N0} received  |  {_scan.Stored:N0} stored  |  queue {_scan.QueueDepth}  |  dropped {_scan.Overflowed:N0}  |  lost to write errors {_scan.Lost:N0}";
        CanStart = s.State is ScannerState.Stopped or ScannerState.Interrupted or ScannerState.AdapterUnavailable or ScannerState.Error;
        CanStop = !CanStart;
        var gaps = _scan.CoverageGaps;
        if (gaps.Count != Gaps.Count)
        {
            Gaps.Clear();
            foreach (var g in gaps.Reverse().Take(50))
                Gaps.Add($"{g.StartUtc.ToLocalTime():dd MMM HH:mm:ss} to {(g.EndUtc is { } e ? e.ToLocalTime().ToString("HH:mm:ss") : "still ongoing")}  -  {g.Reason}");
        }
    }

    partial void OnAllowActiveChanged(bool value)
    {
        if (_loading) return;
        _settings.Current.ScanMode = value ? "Active" : "Passive";
        _settings.Save();
        Refresh();
        _ = RestartIfRunning();
    }

    private async Task RestartIfRunning()
    {
        if (_scan.Status.State is ScannerState.Running or ScannerState.RunningNoAdvertisements)
        {
            await _scan.StopScanAsync();
            await _scan.StartScanAsync();
        }
    }

    [RelayCommand] private Task Start() => _scan.StartScanAsync();
    [RelayCommand] private Task Stop() => _scan.StopScanAsync();
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly AppPaths _paths;

    [ObservableProperty] private int _presenceWindowSeconds;
    [ObservableProperty] private bool _autoStartScan;
    [ObservableProperty] private string _message = "";
    public string DataFolder => _paths.DataDir;
    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public SettingsViewModel(SettingsStore store, AppPaths paths)
    {
        _store = store; _paths = paths;
        _presenceWindowSeconds = store.Current.PresenceWindowSeconds;
        _autoStartScan = store.Current.AutoStartScan;
    }

    partial void OnPresenceWindowSecondsChanged(int value)
    {
        if (value < 10) { Message = "The presence window must be at least 10 seconds."; return; }
        _store.Current.PresenceWindowSeconds = value; _store.Save(); Message = "Saved.";
    }

    partial void OnAutoStartScanChanged(bool value) { _store.Current.AutoStartScan = value; _store.Save(); Message = "Saved."; }

    [RelayCommand]
    private async Task BackupNow()
    {
        try
        {
            var path = await DatabaseMaintenance.BackupAsync(_paths.DbPath, _paths.BackupsDir);
            Message = $"Backup written: {path}";
        }
        catch (Exception ex) { Message = "Backup failed: " + ex.Message; }
    }

    [RelayCommand]
    private void OpenDataFolder() => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_paths.DataDir}\"") { UseShellExecute = true });
}
