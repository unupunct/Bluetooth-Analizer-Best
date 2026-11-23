using System.Collections.ObjectModel;
using System.Windows.Media;
using BluetoothFleetMonitor.Application.Scanning;
using BluetoothFleetMonitor.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace BluetoothFleetMonitor.Desktop.ViewModels;

public sealed class BarItem
{
    public string Label { get; init; } = "";
    public int Count { get; init; }
    public double Percent { get; init; }
    public Brush Fill { get; init; } = Brushes.Gray;
}

public sealed class TopDeviceItem
{
    public string Name { get; init; } = "";
    public string RssiText { get; init; } = "";
    public double Percent { get; init; }
    public string LastSeen { get; init; } = "";
    public Brush Fill { get; init; } = Brushes.Gray;
}

public sealed class LegendItem
{
    public string Name { get; init; } = "";
    public int Count { get; init; }
    public Brush Fill { get; init; } = Brushes.Gray;
}

public sealed partial class DashboardViewModel : ObservableObject
{
    private static readonly SKColor Muted = new(0x8B, 0x93, 0xA1);
    private static readonly SKColor Grid = new(0x2A, 0x2F, 0x3A);
    private static readonly SKColor Accent = new(0x4D, 0xA3, 0xFF);
    private static readonly SKColor Good = new(0x3D, 0xDC, 0x84);
    private static readonly string[] Palette = { "#4DA3FF", "#3DDC84", "#FFB020", "#B28DFF", "#FF7AA8", "#38D6D6", "#8B93A1" };

    private readonly ScanController _scan;
    private readonly LiveRegistry _registry;
    private readonly DashboardMetrics _metrics;
    private string _makerSignature = "";
    private int _tick;

    public ObservableCollection<EventRow> Events { get; }
    public ObservableCollection<BarItem> Histogram { get; } = new();
    public ObservableCollection<TopDeviceItem> TopDevices { get; } = new();
    public ObservableCollection<LegendItem> MakerLegend { get; } = new();

    public SolidColorPaint LegendPaint { get; } = new(new SKColor(0xE6, 0xE8, 0xEC));
    public ISeries[] ActivitySeries { get; }
    public ICartesianAxis[] ActivityXAxes { get; } = { new Axis { IsVisible = false } };
    public ICartesianAxis[] ActivityYAxes { get; }
    public ISeries[] SparkSeries { get; }
    public ICartesianAxis[] SparkXAxes { get; } = { new Axis { IsVisible = false } };
    public ICartesianAxis[] SparkYAxes { get; } = { new Axis { IsVisible = false, MinLimit = 0 } };
    [ObservableProperty] private ISeries[] _makerSeries = Array.Empty<ISeries>();

    [ObservableProperty] private int _registered;
    [ObservableProperty] private int _detected;
    [ObservableProperty] private int _missing;
    [ObservableProperty] private int _uncertain;
    [ObservableProperty] private string _scannerStateText = "";
    [ObservableProperty] private string _scannerMessage = "";
    [ObservableProperty] private bool _scannerWarning;
    [ObservableProperty] private bool _scannerHealthy;
    [ObservableProperty] private string _adapter = "";
    [ObservableProperty] private string _lastUpdate = "never";
    [ObservableProperty] private string _rate = "0";
    [ObservableProperty] private string _pipelineText = "";
    [ObservableProperty] private string _modeText = "";
    [ObservableProperty] private bool _canStart;
    [ObservableProperty] private bool _canStop;
    [ObservableProperty] private bool _isDemo;

    public DashboardViewModel(ScanController scan, LiveRegistry registry, EventFeed feed, DashboardMetrics metrics)
    {
        _scan = scan; _registry = registry; _metrics = metrics; Events = feed.Items;

        ActivitySeries = new ISeries[]
        {
            new LineSeries<int>
            {
                Name = "Devices heard", Values = metrics.Detected, GeometrySize = 0, LineSmoothness = 0.65,
                Stroke = new SolidColorPaint(Accent) { StrokeThickness = 3 },
                Fill = new LinearGradientPaint(new SKColor(Accent.Red, Accent.Green, Accent.Blue, 90), new SKColor(Accent.Red, Accent.Green, Accent.Blue, 0), new SKPoint(0.5f, 0), new SKPoint(0.5f, 1)),
            },
            new LineSeries<double>
            {
                Name = "Advertisements / s", Values = metrics.AdvertisementsPerSecond, GeometrySize = 0, LineSmoothness = 0.65, ScalesYAt = 1,
                Stroke = new SolidColorPaint(Good) { StrokeThickness = 2 }, Fill = null,
            },
        };
        ActivityYAxes = new ICartesianAxis[]
        {
            new Axis { MinLimit = 0, LabelsPaint = new SolidColorPaint(Muted), SeparatorsPaint = new SolidColorPaint(Grid) { StrokeThickness = 1 }, TextSize = 11 },
            new Axis { MinLimit = 0, Position = AxisPosition.End, ShowSeparatorLines = false, LabelsPaint = new SolidColorPaint(Good.WithAlpha(190)), TextSize = 11 },
        };
        SparkSeries = new ISeries[]
        {
            new LineSeries<double>
            {
                Values = metrics.AdvertisementsPerSecond, GeometrySize = 0, LineSmoothness = 0.7, Stroke = new SolidColorPaint(Good) { StrokeThickness = 2 },
                Fill = new LinearGradientPaint(new SKColor(Good.Red, Good.Green, Good.Blue, 70), new SKColor(Good.Red, Good.Green, Good.Blue, 0), new SKPoint(0.5f, 0), new SKPoint(0.5f, 1)),
            },
        };

        scan.Changed += Refresh;
        Refresh();
    }

    /// <summary>For the labelled demo screenshots only: shows a healthy scanner with sample numbers and says so with a visible DEMO DATA pill.</summary>
    public void EnterDemo()
    {
        IsDemo = true;
        ScannerStateText = "Running"; ScannerMessage = ""; ScannerWarning = false; ScannerHealthy = true;
        Adapter = "TP-Link Bluetooth 5.4 USB Adapter (demo)"; ModeText = "Passive: listen only"; Rate = "318";
        PipelineText = "184,220 stored  |  queue 0  |  dropped 0  |  lost 0"; LastUpdate = DateTime.Now.ToString("HH:mm:ss");
        CanStart = false; CanStop = true;
        RefreshBreakdowns();
    }

    /// <summary>Called once per second (and on scanner state changes) by the scan controller.</summary>
    private void Refresh()
    {
        if (IsDemo) { Registered = _registry.Registered; Detected = _registry.Detected; Missing = _registry.Missing; Uncertain = _registry.Uncertain; RefreshBreakdowns(); return; }
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
        ScannerHealthy = s.State == ScannerState.Running;
        Adapter = _scan.AdapterDescription ?? "no adapter detected yet";
        LastUpdate = _registry.LastUpdateUtc is { } t ? t.ToLocalTime().ToString("HH:mm:ss") : "no data yet";
        Rate = $"{_scan.AdvertisementsPerSecond:0}";
        ModeText = _scan.Mode == ScanMode.Passive ? "Passive: listen only" : "Active scan";
        PipelineText = $"{_scan.Stored:N0} stored  |  queue {_scan.QueueDepth}  |  dropped {_scan.Overflowed:N0}  |  lost {_scan.Lost:N0}";
        CanStart = s.State is ScannerState.Stopped or ScannerState.Interrupted or ScannerState.AdapterUnavailable or ScannerState.Error;
        CanStop = !CanStart;

        if (!IsDemo) _metrics.Record(_registry.Detected, _scan.AdvertisementsPerSecond);
        RefreshBreakdowns();
    }

    private void RefreshBreakdowns()
    {
        var heard = _registry.Devices.Where(d => d.Status == "Detected" && d.Rssi is not null).ToList();

        // signal strength distribution
        var buckets = new (string Label, Func<int, bool> Match, string Colour)[]
        {
            ("Excellent", r => r >= -55, "#3DDC84"), ("Good", r => r is < -55 and >= -67, "#7BDC84"), ("Fair", r => r is < -67 and >= -78, "#4DA3FF"),
            ("Weak", r => r is < -78 and >= -88, "#FFB020"), ("Very weak", r => r < -88, "#FF5C5C"),
        };
        var max = Math.Max(1, buckets.Max(b => heard.Count(d => b.Match(d.Rssi!.Value))));
        Histogram.Clear();
        foreach (var b in buckets)
        {
            var n = heard.Count(d => b.Match(d.Rssi!.Value));
            Histogram.Add(new BarItem { Label = b.Label, Count = n, Percent = 100.0 * n / max, Fill = Brush(b.Colour) });
        }

        // strongest right now
        TopDevices.Clear();
        foreach (var d in heard.OrderByDescending(d => d.Rssi).Take(8))
        {
            var rssi = d.Rssi!.Value;
            TopDevices.Add(new TopDeviceItem
            {
                Name = d.Name, RssiText = $"{rssi} dBm", Percent = Math.Clamp((rssi + 100) / 60.0 * 100, 4, 100), LastSeen = d.LastSeenText,
                Fill = Brush(rssi >= -67 ? "#3DDC84" : rssi >= -78 ? "#4DA3FF" : "#FFB020"),
            });
        }

        // by maker (every few seconds is plenty)
        if (_tick++ % 5 != 0 && MakerSeries.Length > 0) return;
        var groups = _registry.Devices.GroupBy(d => string.IsNullOrWhiteSpace(d.Manufacturer) ? "Unknown" : d.Manufacturer!)
            .OrderByDescending(g => g.Count()).ToList();
        var top = groups.Take(6).ToList();
        var other = groups.Skip(6).Sum(g => g.Count());
        var rows = top.Select(g => (g.Key, g.Count())).ToList();
        if (other > 0) rows.Add(("Other", other));
        var signature = string.Join('|', rows.Select(r => $"{r.Item1}:{r.Item2}"));
        if (signature == _makerSignature) return;
        _makerSignature = signature;
        MakerLegend.Clear();
        var series = new List<ISeries>();
        for (var i = 0; i < rows.Count; i++)
        {
            var colour = Palette[Math.Min(i, Palette.Length - 1)];
            MakerLegend.Add(new LegendItem { Name = rows[i].Item1, Count = rows[i].Item2, Fill = Brush(colour) });
            var c = (Color)ColorConverter.ConvertFromString(colour);
            series.Add(new PieSeries<double> { Values = new double[] { rows[i].Item2 }, Name = rows[i].Item1, InnerRadius = 46, Fill = new SolidColorPaint(new SKColor(c.R, c.G, c.B)), Stroke = null });
        }
        MakerSeries = series.ToArray();
    }

    private static Brush Brush(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    [RelayCommand] private Task StartScan() => _scan.StartScanAsync();
    [RelayCommand] private Task StopScan() => _scan.StopScanAsync();
}
