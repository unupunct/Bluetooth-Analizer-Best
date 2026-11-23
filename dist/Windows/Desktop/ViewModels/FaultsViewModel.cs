using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using BluetoothFleetMonitor.Application.Diagnostics;
using BluetoothFleetMonitor.Database;
using BluetoothFleetMonitor.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BluetoothFleetMonitor.Desktop.ViewModels;

public sealed class FaultRow
{
    public int Rank { get; init; }
    public DeviceDiagnosis D { get; init; } = null!;
    public string Name => D.Name;
    public string Category => D.Category;
    public string Room => D.Room ?? "";
    public string Workstation => D.Workstation ?? "";
    public string HealthText => D.Health is { } h ? $"{h:0}" : "-";
    public double HealthValue => D.Health ?? -1;
    public string BandText => D.Band == HealthBand.NotEnoughData ? "Not enough data" : D.Band.ToString();
    public string ConfidenceText => $"{D.Confidence:P0}";
    public int Interruptions => D.Interruptions;
    public string TestsText => D.TestFailureRate is { } r ? $"{r:P0} failed" : "never tested";
    public string TopFinding
    {
        get
        {
            var f = D.Findings.Where(x => x.Severity > 0).OrderByDescending(x => x.Severity).FirstOrDefault();
            if (f is null) return D.Findings.Count > 0 ? "Shared outages only" : "";
            return f.Kind switch
            {
                FindingKind.HardwareSuspected => "Suspect the device itself",
                FindingKind.TestFailures => "Connection tests failing",
                FindingKind.Flapping => $"Flapping ({D.Flaps} quick drops)",
                FindingKind.PresenceInstability => $"Silent {D.Interruptions}x, {D.MissingMinutes:0} min",
                FindingKind.SignalDegradation => $"Signal falling {-(D.TrendDb ?? 0):0} dB",
                FindingKind.SignalInstability => $"Signal swings {D.SignalSpreadDb:0} dB",
                _ => f.Text,
            };
        }
    }
    public Brush BandBrush { get; init; } = Brushes.Gray;
}

public sealed class ComponentRow
{
    public string Name { get; init; } = "";
    public string ScoreText { get; init; } = "";
    public string WeightText { get; init; } = "";
    public string Explanation { get; init; } = "";
    public double Percent { get; init; }
    public Brush Fill { get; init; } = Brushes.Gray;
}

public sealed partial class FaultsViewModel : ObservableObject
{
    private readonly FaultQueries _queries;
    private readonly SelectionService _selection;
    private readonly string _settingsPath;
    private FleetAnalysis? _analysis;
    private bool _loadingFilters;

    public static readonly string[] Windows = { "Last 24 hours", "Last 7 days", "Last 30 days" };
    public static readonly string[] FailureTypes = { "Any", "Presence", "Flapping", "Signal", "Connection tests", "Hardware suspected", "Environmental" };

    public ObservableCollection<FaultRow> Rows { get; } = new();
    public ObservableCollection<ComponentRow> Components { get; } = new();
    public ObservableCollection<string> Findings { get; } = new();
    public ObservableCollection<string> SharedOutages { get; } = new();
    public ObservableCollection<string> Categories { get; } = new() { "All" };
    public ObservableCollection<string> Rooms { get; } = new() { "All" };
    public AnalyzerSettings Settings { get; private set; } = new();

    [ObservableProperty] private string _window = Windows[0];
    [ObservableProperty] private string _category = "All";
    [ObservableProperty] private string _room = "All";
    [ObservableProperty] private string _failureType = "Any";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _onlyProblems;
    [ObservableProperty] private string _summary = "Open this page to analyse the history in the database.";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private FaultRow? _selected;
    [ObservableProperty] private string _selectedTitle = "";
    [ObservableProperty] private string _selectedRecommendation = "";
    [ObservableProperty] private string _selectedHealth = "";
    [ObservableProperty] private Brush _selectedBrush = Brushes.Gray;
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private bool _isDemo;

    // adjustable numbers (applied with the Apply button, saved next to the database)
    [ObservableProperty] private double _weightPresence;
    [ObservableProperty] private double _weightSignal;
    [ObservableProperty] private double _weightRecovery;
    [ObservableProperty] private double _weightTrend;
    [ObservableProperty] private double _weightTests;
    [ObservableProperty] private int _minGapSeconds;
    [ObservableProperty] private int _minObservations;
    [ObservableProperty] private double _minConfidence;

    public FaultsViewModel(FaultQueries queries, SelectionService selection, AppPaths paths)
    {
        _queries = queries; _selection = selection;
        _settingsPath = Path.Combine(paths.DataDir, "fault-settings.json");
        try { if (File.Exists(_settingsPath)) Settings = JsonSerializer.Deserialize<AnalyzerSettings>(File.ReadAllText(_settingsPath)) ?? new(); } catch { Settings = new(); }
        PushSettings();
    }

    private void PushSettings()
    {
        _loadingFilters = true;
        WeightPresence = Settings.WeightPresence; WeightSignal = Settings.WeightSignal; WeightRecovery = Settings.WeightRecovery;
        WeightTrend = Settings.WeightTrend; WeightTests = Settings.WeightTests; MinGapSeconds = Settings.MinGapSeconds;
        MinObservations = Settings.MinObservations; MinConfidence = Settings.MinConfidenceForDiagnosis;
        _loadingFilters = false;
    }

    [RelayCommand]
    private async Task ApplySettings()
    {
        Settings.WeightPresence = Math.Max(0, WeightPresence); Settings.WeightSignal = Math.Max(0, WeightSignal);
        Settings.WeightRecovery = Math.Max(0, WeightRecovery); Settings.WeightTrend = Math.Max(0, WeightTrend); Settings.WeightTests = Math.Max(0, WeightTests);
        Settings.MinGapSeconds = Math.Clamp(MinGapSeconds, 10, 3600); Settings.MinObservations = Math.Max(1, MinObservations);
        Settings.MinConfidenceForDiagnosis = Math.Clamp(MinConfidence, 0, 1);
        try { File.WriteAllText(_settingsPath, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true })); } catch { /* not fatal: the numbers still apply this session */ }
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task ResetSettings()
    {
        Settings = new AnalyzerSettings(); PushSettings();
        try { if (File.Exists(_settingsPath)) File.Delete(_settingsPath); } catch { }
        await RefreshAsync();
    }

    private TimeSpan WindowLength => Window switch { "Last 7 days" => TimeSpan.FromDays(7), "Last 30 days" => TimeSpan.FromDays(30), _ => TimeSpan.FromHours(24) };

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (Busy || IsDemo) return;
        Busy = true; Status = "Analysing...";
        try
        {
            var to = DateTime.UtcNow; var range = new TimeRange(to - WindowLength, to);
            var (devices, coverage) = await Task.Run(() => _queries.LoadAsync(range, Settings));
            _analysis = await Task.Run(() => FaultAnalyzer.Analyze(devices, coverage, range, Settings));
            Status = $"Analysed {_analysis.Devices.Count} devices over {Window.ToLowerInvariant()}; the scanner was listening for {_analysis.CoveredHours:0.#} h of that window.";
            ApplyView();
        }
        catch (Exception ex) { Status = "Analysis failed: " + ex.Message; }
        finally { Busy = false; }
    }

    /// <summary>For the labelled demo screenshots: shows an analysis computed from fictional data, with a DEMO DATA label.</summary>
    public void LoadAnalysis(FleetAnalysis analysis, string window)
    {
        _analysis = analysis; IsDemo = true; Window = window;
        Status = "DEMO DATA: fictional devices and outages for documentation. Not real measurements.";
        ApplyView();
    }

    partial void OnWindowChanged(string value) { if (!_loadingFilters && !IsDemo) _ = RefreshAsync(); }
    partial void OnCategoryChanged(string value) => ApplyView();
    partial void OnRoomChanged(string value) => ApplyView();
    partial void OnFailureTypeChanged(string value) => ApplyView();
    partial void OnSearchChanged(string value) => ApplyView();
    partial void OnOnlyProblemsChanged(bool value) => ApplyView();

    partial void OnSelectedChanged(FaultRow? value)
    {
        Components.Clear(); Findings.Clear();
        HasSelection = value is not null;
        if (value is null) { SelectedTitle = ""; SelectedRecommendation = ""; SelectedHealth = ""; return; }
        var d = value.D;
        SelectedTitle = d.Name + (string.IsNullOrWhiteSpace(d.Room) ? "" : $"  |  {d.Room}");
        SelectedHealth = d.Health is { } h ? $"Health {h:0}/100 ({value.BandText}), confidence {d.Confidence:P0}" : $"No score yet, confidence {d.Confidence:P0}";
        SelectedBrush = value.BandBrush;
        SelectedRecommendation = d.Recommendation;
        foreach (var c in d.Components)
            Components.Add(new ComponentRow
            {
                Name = c.Name, ScoreText = c.Used ? $"{c.Score:0}" : c.Weight > 0 ? "left out" : "applied", WeightText = c.Weight > 0 ? $"weight {c.Weight:0.#}" : "",
                Explanation = c.Explanation, Percent = c.Score ?? 0, Fill = c.Used ? Brush(c.Score >= 75 ? "#3DDC84" : c.Score >= 50 ? "#FFB020" : "#FF5C5C") : Brush("#3A3F4B"),
            });
        foreach (var f in d.Findings) Findings.Add((f.Severity == 0 ? "Note: " : f.Severity >= 3 ? "Serious: " : f.Severity == 2 ? "Likely: " : "Minor: ") + f.Text);
        if (d.Findings.Count == 0) Findings.Add("No faults found in this window.");
    }

    private void ApplyView()
    {
        if (_analysis is null) return;
        _loadingFilters = true;
        Fill(Categories, _analysis.Devices.Select(d => d.Category), Category, v => Category = v);
        Fill(Rooms, _analysis.Devices.Select(d => d.Room ?? ""), Room, v => Room = v);
        _loadingFilters = false;

        bool Match(DeviceDiagnosis d)
        {
            if (Category != "All" && d.Category != Category) return false;
            if (Room != "All" && (d.Room ?? "") != Room) return false;
            if (OnlyProblems && d.Findings.All(f => f.Severity == 0)) return false;
            if (Search.Length > 0 && !(d.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) || (d.Room ?? "").Contains(Search, StringComparison.OrdinalIgnoreCase) || (d.Workstation ?? "").Contains(Search, StringComparison.OrdinalIgnoreCase))) return false;
            return FailureType switch
            {
                "Presence" => d.Findings.Any(f => f.Kind == FindingKind.PresenceInstability),
                "Flapping" => d.Findings.Any(f => f.Kind == FindingKind.Flapping),
                "Signal" => d.Findings.Any(f => f.Kind is FindingKind.SignalInstability or FindingKind.SignalDegradation),
                "Connection tests" => d.Findings.Any(f => f.Kind == FindingKind.TestFailures),
                "Hardware suspected" => d.Findings.Any(f => f.Kind == FindingKind.HardwareSuspected),
                "Environmental" => d.EnvironmentalSilences > 0,
                _ => true,
            };
        }

        // Worst Offenders order (spec section 8): connection-test failure rate, then presence interruptions, then signal instability,
        // then severity, then confidence. Devices without enough data for a diagnosis go last.
        var ordered = _analysis.Devices.Where(Match)
            .OrderBy(d => d.Band == HealthBand.NotEnoughData ? 1 : 0)
            .ThenByDescending(d => d.TestFailureRate ?? 0)
            .ThenByDescending(d => d.Interruptions)
            .ThenByDescending(d => d.SignalSpreadDb ?? 0)
            .ThenByDescending(d => d.Severity)
            .ThenByDescending(d => d.Confidence).ToList();

        var keep = Selected?.D.DeviceId;
        Rows.Clear();
        var i = 1;
        foreach (var d in ordered) Rows.Add(new FaultRow { Rank = i++, D = d, BandBrush = Brush(BandColour(d.Band)) });
        Selected = Rows.FirstOrDefault(r => r.D.DeviceId == keep) ?? Rows.FirstOrDefault();

        var diagnosed = _analysis.Devices.Where(d => d.Band != HealthBand.NotEnoughData).ToList();
        Summary = $"{diagnosed.Count(d => d.Band is HealthBand.Unstable or HealthBand.Critical)} need attention, " +
                  $"{diagnosed.Count(d => d.Band == HealthBand.Monitor)} to monitor, {diagnosed.Count(d => d.Band is HealthBand.Healthy or HealthBand.Good)} healthy or good, " +
                  $"{_analysis.Devices.Count - diagnosed.Count} without enough data. Showing {Rows.Count}.";
        SharedOutages.Clear();
        foreach (var e in _analysis.EnvironmentalEvents.OrderByDescending(e => e.Range.StartUtc).Take(8))
            SharedOutages.Add($"{e.Range.StartUtc.ToLocalTime():dd MMM HH:mm}  for {e.Range.Length.TotalMinutes:0} min: {e.DevicesSilent} of {e.DevicesActive} devices{(e.Room is null ? "" : " in " + e.Room)} went silent together (environment or scanner, not charged to the devices)");
        if (SharedOutages.Count == 0) SharedOutages.Add("No shared outages found.");
    }

    private static void Fill(ObservableCollection<string> target, IEnumerable<string> values, string current, Action<string> set)
    {
        var list = values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().OrderBy(v => v).Prepend("All").ToList();
        if (target.SequenceEqual(list)) return;
        target.Clear(); foreach (var v in list) target.Add(v);
        if (!list.Contains(current)) set("All");
    }

    [RelayCommand] private void OpenHistory() { if (Selected is not null) _selection.OpenHistory(Selected.D.DeviceId); }

    public static string BandColour(HealthBand b) => b switch
    {
        HealthBand.Healthy => "#3DDC84", HealthBand.Good => "#8BDC6E", HealthBand.Monitor => "#FFB020",
        HealthBand.Unstable => "#FF8A3D", HealthBand.Critical => "#FF5C5C", _ => "#8B93A1",
    };

    private static Brush Brush(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }
}
