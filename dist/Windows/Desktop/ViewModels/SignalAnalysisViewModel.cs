using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media;
using BluetoothFleetMonitor.Application.Diagnostics;
using BluetoothFleetMonitor.Database;
using BluetoothFleetMonitor.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace BluetoothFleetMonitor.Desktop.ViewModels;

public sealed class SilenceRow
{
    public string When { get; init; } = "";
    public string Length { get; init; } = "";
    public string Levels { get; init; } = "";
}

public sealed class RecoveryRow
{
    public string When { get; init; } = "";
    public string Drop { get; init; } = "";
    public string Recovered { get; init; } = "";
}

/// <summary>
/// The per-device deep-dive: who the device is, the Fault Detector's verdict for it, and the signal analysis. The verdict comes from the
/// same analyzer, window and coverage as the Worst Offenders list, so the two never disagree.
/// </summary>
public sealed partial class SignalAnalysisViewModel : ObservableObject
{
    private static readonly SKColor Muted = new(0x8B, 0x93, 0xA1), Grid = new(0x2A, 0x2F, 0x3A), Blue = new(0x4D, 0xA3, 0xFF), Amber = new(0xFF, 0xB0, 0x20), Red = new(0xFF, 0x5C, 0x5C), Green = new(0x3D, 0xDC, 0x84);

    private readonly SignalQueries _signal;
    private readonly FaultQueries _faults;
    private readonly FaultsViewModel _faultSettings;
    private readonly SelectionService _selection;
    private Guid? _deviceId;
    private string? _lookupQuery;
    private bool _loading;

    public static readonly string[] Windows = { "Last hour", "Last 24 hours", "Last 7 days", "Last 30 days" };

    public ObservableCollection<ProfileLine> ProfileLines { get; } = new();
    public ObservableCollection<ProfileLine> StatLines { get; } = new();
    public ObservableCollection<string> Findings { get; } = new();
    public ObservableCollection<ScannerChoice> Scanners { get; } = new();
    public ObservableCollection<SilenceRow> Silences { get; } = new();
    public ObservableCollection<RecoveryRow> Recoveries { get; } = new();

    public SolidColorPaint LegendPaint { get; } = new(new SKColor(0xE6, 0xE8, 0xEC));
    public SolidColorPaint TooltipPaint { get; } = new(new SKColor(0xE6, 0xE8, 0xEC));
    public ICartesianAxis[] SignalYAxes { get; } = { new Axis { Name = "RSSI (dBm)", NamePaint = new SolidColorPaint(Muted), LabelsPaint = new SolidColorPaint(Muted), SeparatorsPaint = new SolidColorPaint(Grid) { StrokeThickness = 1 }, MinLimit = -100, MaxLimit = -30 } };

    [ObservableProperty] private string _title = "Signal Analysis";
    [ObservableProperty] private bool _hasDevice;
    [ObservableProperty] private bool _isDemo;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _message = "Open a device from the Fault Detector, the Inventory or the Live Monitor with its Analyse button.";
    [ObservableProperty] private string _verdict = "";
    [ObservableProperty] private Brush _verdictBrush = Brushes.Gray;
    [ObservableProperty] private string _recommendation = "";
    [ObservableProperty] private string _window = Windows[1];
    [ObservableProperty] private ScannerChoice? _selectedScanner;
    [ObservableProperty] private bool _canLookUp;
    [ObservableProperty] private ISeries[] _signalSeries = Array.Empty<ISeries>();
    [ObservableProperty] private ICartesianAxis[] _signalXAxes = Array.Empty<ICartesianAxis>();
    [ObservableProperty] private RectangularSection[] _signalSections = Array.Empty<RectangularSection>();
    [ObservableProperty] private ISeries[] _distributionSeries = Array.Empty<ISeries>();
    [ObservableProperty] private ICartesianAxis[] _distributionXAxes = Array.Empty<ICartesianAxis>();
    [ObservableProperty] private ISeries[] _intervalSeries = Array.Empty<ISeries>();
    [ObservableProperty] private ICartesianAxis[] _intervalXAxes = Array.Empty<ICartesianAxis>();
    [ObservableProperty] private string _intervalNote = "";

    public SignalAnalysisViewModel(SignalQueries signal, FaultQueries faults, FaultsViewModel faultSettings, SelectionService selection)
    {
        _signal = signal; _faults = faults; _faultSettings = faultSettings; _selection = selection;
    }

    private TimeSpan Span => Window switch { "Last hour" => TimeSpan.FromHours(1), "Last 7 days" => TimeSpan.FromDays(7), "Last 30 days" => TimeSpan.FromDays(30), _ => TimeSpan.FromHours(24) };

    public Task ShowAsync(Guid deviceId)
    {
        if (_deviceId != deviceId) { _loading = true; SelectedScanner = null; _loading = false; }
        _deviceId = deviceId;
        return RefreshAsync();
    }

    partial void OnWindowChanged(string value) { if (!_loading && !IsDemo && _deviceId is not null) _ = RefreshAsync(); }
    partial void OnSelectedScannerChanged(ScannerChoice? value) { if (!_loading && !IsDemo && _deviceId is not null) _ = RefreshAsync(); }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_deviceId is not { } id || Busy || IsDemo) return;
        Busy = true;
        try
        {
            var to = DateTime.UtcNow; var range = new TimeRange(to - Span, to);
            var wantedScanner = SelectedScanner?.Id;
            var profileInput = await Task.Run(() => _signal.LoadProfileAsync(id));
            if (profileInput is null) { HasDevice = false; Message = "That device no longer exists in the database."; return; }
            var scanners = await Task.Run(() => _signal.ScannersAsync(id, range));
            var input = await Task.Run(() => _signal.LoadAsync(id, range, wantedScanner));
            var analysis = SignalAnalyzer.Analyze(input);
            var diag = await Task.Run(() => _faults.DiagnoseAsync(id, range, _faultSettings.Settings));
            _loading = true;
            Apply(input, analysis, DeviceProfileBuilder.Build(profileInput), diag, scanners, wantedScanner);
            _loading = false;
        }
        catch (Exception ex) { Message = "Could not analyse this device: " + ex.Message; }
        finally { _loading = false; Busy = false; }
    }

    /// <summary>For the labelled demo screenshots: shows an analysis computed from fictional data, with a DEMO DATA label.</summary>
    public void LoadDemo(SignalInput input, SignalAnalysis analysis, DeviceProfile profile, DeviceDiagnosis diag, IReadOnlyList<ScannerChoice> scanners)
    {
        IsDemo = true; _loading = true;
        Apply(input, analysis, profile, diag, scanners, null);
        _loading = false;
    }

    private void Apply(SignalInput input, SignalAnalysis a, DeviceProfile profile, DeviceDiagnosis? diag, IReadOnlyList<ScannerChoice> scanners, Guid? scannerId)
    {
        HasDevice = true; Title = profile.Title;
        _lookupQuery = profile.LookupQuery; CanLookUp = _lookupQuery is not null;
        ProfileLines.Clear(); foreach (var l in profile.Lines) ProfileLines.Add(l);

        Scanners.Clear(); foreach (var s in scanners) Scanners.Add(s);
        SelectedScanner = Scanners.FirstOrDefault(s => s.Id == scannerId) ?? Scanners.FirstOrDefault();

        Findings.Clear();
        if (diag is null) { Verdict = "No verdict: this device was not heard in this window."; VerdictBrush = Brush("#8B93A1"); Recommendation = ""; }
        else
        {
            var band = diag.Band == HealthBand.NotEnoughData ? "Not enough data" : diag.Band.ToString();
            Verdict = (diag.Health is { } h ? $"Health {h:0}/100 ({band})" : "No score yet") + $", confidence {diag.Confidence:P0}";
            VerdictBrush = Brush(FaultsViewModel.BandColour(diag.Band));
            Recommendation = diag.Recommendation;
            foreach (var f in diag.Findings) Findings.Add((f.Severity == 0 ? "Note: " : f.Severity >= 3 ? "Serious: " : f.Severity == 2 ? "Likely: " : "Minor: ") + f.Text);
            if (diag.Findings.Count == 0) Findings.Add("No faults found in this window.");
        }

        Message = a.Sufficient ? (scannerId is null ? "" : "Showing only what the selected scanner heard.") : a.Note ?? "";
        StatLines.Clear(); Silences.Clear(); Recoveries.Clear();
        SignalSections = Array.Empty<RectangularSection>(); SignalSeries = Array.Empty<ISeries>(); DistributionSeries = Array.Empty<ISeries>(); IntervalSeries = Array.Empty<ISeries>();
        IntervalNote = "";
        if (a.Intervals is { } iv)
        {
            StatLines.Add(new("Time between advertisements", $"median {Fmt(iv.MedianSeconds)}, 95% under {Fmt(iv.P95Seconds)}, longest {Fmt(iv.MaxSeconds)}"));
            IntervalSeries = new ISeries[] { new ColumnSeries<int> { Name = "Gaps", Values = iv.Bins.Select(b => b.Count).ToArray(), Fill = new SolidColorPaint(Blue), Stroke = null } };
            IntervalXAxes = new ICartesianAxis[] { new Axis { Labels = iv.Bins.Select(b => b.Label + " s").ToList(), LabelsPaint = new SolidColorPaint(Muted), TextSize = 11 } };
            IntervalNote = scannerId is null && scanners.Count > 2 ? "Several scanners heard this device, so these gaps mix sources. Pick one scanner above for exact intervals." : "";
        }
        if (!a.Sufficient) return;

        var st = a.Stats!;
        StatLines.Insert(0, new("Signal", $"min {st.Min}, avg {st.Mean:0.0}, median {st.Median:0.0}, max {st.Max} dBm"));
        StatLines.Insert(1, new("Steadiness", $"spread {st.StdDev:0.0} dB, change from minute to minute {st.Volatility:0.0} dB"));
        StatLines.Insert(2, new("Looks like", string.Join(", ", a.Strength.Where(s => s.Fraction >= 0.01).Select(s => $"{s.Fraction:P0} {s.Label.ToLowerInvariant()}")) + " (a description of this device's own readings, not a distance)"));

        var bucketSeconds = input.BucketSeconds;
        var avg = new List<DateTimePoint>(); var ma = new List<DateTimePoint>();
        var buckets = input.Buckets.Where(b => b.Count > 0).OrderBy(b => b.StartUtc).ToList();
        for (var i = 0; i < buckets.Count; i++)
        {
            var t = buckets[i].StartUtc.ToLocalTime();
            avg.Add(new DateTimePoint(t, buckets[i].Average)); ma.Add(new DateTimePoint(t, a.MovingAverage[i]));
            if (i + 1 < buckets.Count && (buckets[i + 1].StartUtc - buckets[i].StartUtc).TotalSeconds > bucketSeconds * 3)
            { var gap = t.AddSeconds(bucketSeconds); avg.Add(new DateTimePoint(gap, null)); ma.Add(new DateTimePoint(gap, null)); }   // a gap is a break, never joined up
        }
        SignalSeries = new ISeries[]
        {
            Line("Signal (average per bucket)", avg, new SKColor(0x4D, 0xA3, 0xFF, 0xB0), 1.5f),
            Line("Moving average", ma, Amber, 2.5f),
        };
        var fmt = Span <= TimeSpan.FromHours(36) ? "HH:mm" : "dd MMM HH:mm";
        SignalXAxes = new ICartesianAxis[]
        {
            new DateTimeAxis(TimeSpan.FromSeconds(bucketSeconds), d => d.ToString(fmt))
            {
                LabelsPaint = new SolidColorPaint(Muted), SeparatorsPaint = new SolidColorPaint(Grid) { StrokeThickness = 1 },
            },
        };
        SignalSections = a.Silences.Select(s => new RectangularSection { Xi = s.Range.StartUtc.ToLocalTime().Ticks, Xj = s.Range.EndUtc.ToLocalTime().Ticks, Fill = new SolidColorPaint(Red.WithAlpha(45)) }).ToArray();

        DistributionSeries = new ISeries[] { new ColumnSeries<int> { Name = "Buckets", Values = a.Distribution.Select(b => b.Count).ToArray(), Fill = new SolidColorPaint(Green), Stroke = null } };
        DistributionXAxes = new ICartesianAxis[] { new Axis { Labels = a.Distribution.Select(b => $"{b.FromDbm}").ToList(), LabelsPaint = new SolidColorPaint(Muted), TextSize = 11, Name = "dBm (5 dB steps)", NamePaint = new SolidColorPaint(Muted) } };

        foreach (var s in a.Silences)
            Silences.Add(new SilenceRow
            {
                When = s.Range.StartUtc.ToLocalTime().ToString("dd MMM HH:mm"), Length = Fmt(s.Range.Length.TotalSeconds),
                Levels = (s.LevelBeforeDbm is { } b ? $"{b:0} dBm before" : "no signal before") + ", " + (s.LevelAfterDbm is { } af ? $"{af:0} dBm after" : "no signal after"),
            });
        foreach (var r in a.Recoveries)
            Recoveries.Add(new RecoveryRow { When = r.DropStartUtc.ToLocalTime().ToString("dd MMM HH:mm"), Drop = $"fell {r.DropDb:0} dB", Recovered = r.RecoveredAfter is { } t ? $"back to normal after {Fmt(t.TotalSeconds)}" : "had not recovered by the end of the window" });
    }

    private static LineSeries<DateTimePoint> Line(string name, List<DateTimePoint> values, SKColor color, float width) => new()
    {
        Name = name, Values = values, Fill = null, GeometrySize = 0, LineSmoothness = 0,
        Stroke = new SolidColorPaint(color) { StrokeThickness = width },
    };

    private static string Fmt(double seconds) => seconds < 120 ? $"{seconds:0} s" : seconds < 7200 ? $"{seconds / 60:0.#} min" : $"{seconds / 3600:0.#} h";

    private static Brush Brush(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }

    /// <summary>Opens the browser at a search made from the maker and the advertised name. The app itself makes no network call.</summary>
    [RelayCommand]
    private void LookUpOnline()
    {
        if (_lookupQuery is null) return;
        try { Process.Start(new ProcessStartInfo(LookupLink.Url(_lookupQuery)) { UseShellExecute = true }); }
        catch (Exception ex) { Message = "Could not open the browser: " + ex.Message; }
    }

    [RelayCommand] private void OpenHistory() { if (_deviceId is { } id) _selection.OpenHistory(id); }
}
