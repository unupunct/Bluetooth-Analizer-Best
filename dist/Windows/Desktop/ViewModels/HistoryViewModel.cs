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

public sealed record HistoryPeriod(string Label, TimeSpan Span);

public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly HistoryQueries _queries;
    private readonly SelectionService _selection;

    public LiveRegistry Registry { get; }
    public HistoryPeriod[] Periods { get; } =
    {
        new("Last 15 minutes", TimeSpan.FromMinutes(15)), new("Last hour", TimeSpan.FromHours(1)),
        new("Last 6 hours", TimeSpan.FromHours(6)), new("Last 24 hours", TimeSpan.FromHours(24)),
        new("Last 7 days", TimeSpan.FromDays(7)), new("Last 30 days", TimeSpan.FromDays(30)),
    };

    [ObservableProperty] private LiveDevice? _device;
    [ObservableProperty] private LiveDevice? _compareDevice;
    [ObservableProperty] private HistoryPeriod _period;
    [ObservableProperty] private ISeries[] _series = Array.Empty<ISeries>();
    [ObservableProperty] private ICartesianAxis[] _xAxes = Array.Empty<ICartesianAxis>();
    [ObservableProperty] private ICartesianAxis[] _yAxes = Array.Empty<ICartesianAxis>();
    [ObservableProperty] private string _summary = "Pick a device to see its signal history.";
    [ObservableProperty] private string _note = "";

    public SolidColorPaint LegendPaint { get; } = new(new SKColor(0xE6, 0xE8, 0xEC));
    public SolidColorPaint TooltipPaint { get; } = new(new SKColor(0xE6, 0xE8, 0xEC));

    private static readonly SKColor Muted = new(0x8B, 0x93, 0xA1);
    private static readonly SKColor Grid = new(0x2A, 0x2F, 0x3A);

    public HistoryViewModel(LiveRegistry registry, HistoryQueries queries, SelectionService selection)
    {
        Registry = registry; _queries = queries; _selection = selection;
        _period = Periods[3];
        selection.OpenHistoryRequested += id => { Device = registry.Find(id); };
        YAxes = new ICartesianAxis[]
        {
            new Axis { Name = "RSSI (dBm)", NamePaint = new SolidColorPaint(Muted), LabelsPaint = new SolidColorPaint(Muted),
                       SeparatorsPaint = new SolidColorPaint(Grid) { StrokeThickness = 1 }, MinLimit = -100, MaxLimit = -30 },
        };
    }

    partial void OnDeviceChanged(LiveDevice? value) => _ = RefreshAsync();
    partial void OnCompareDeviceChanged(LiveDevice? value) => _ = RefreshAsync();
    partial void OnPeriodChanged(HistoryPeriod value) => _ = RefreshAsync();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (Device is null) { Series = Array.Empty<ISeries>(); return; }
        var to = DateTime.UtcNow;
        var from = to - Period.Span;
        var bucket = Math.Max(1, (int)Math.Ceiling(Period.Span.TotalSeconds / 400));
        try
        {
            var list = new List<ISeries>();
            var main = await _queries.GetRssiHistoryAsync(Device.Id, from, to, bucket);
            list.Add(Line($"{Device.Name} (avg)", main, bucket, new SKColor(0x4D, 0xA3, 0xFF), 2));
            list.Add(Line($"{Device.Name} min", main.Select(p => p with { Average = p.Min }).ToList(), bucket, new SKColor(0xFF, 0x5C, 0x5C, 0x90), 1));
            list.Add(Line($"{Device.Name} max", main.Select(p => p with { Average = p.Max }).ToList(), bucket, new SKColor(0x3D, 0xDC, 0x84, 0x90), 1));
            if (CompareDevice is not null)
            {
                var other = await _queries.GetRssiHistoryAsync(CompareDevice.Id, from, to, bucket);
                list.Add(Line($"{CompareDevice.Name} (avg)", other, bucket, new SKColor(0xFF, 0xB0, 0x20), 2));
            }
            Series = list.ToArray();

            var fmt = Period.Span <= TimeSpan.FromHours(24) ? "HH:mm" : "dd MMM HH:mm";
            XAxes = new ICartesianAxis[]
            {
                new DateTimeAxis(TimeSpan.FromSeconds(bucket), d => d.ToString(fmt))
                { LabelsPaint = new SolidColorPaint(Muted), SeparatorsPaint = new SolidColorPaint(Grid) { StrokeThickness = 1 } },
            };

            var s = await _queries.GetSummaryAsync(Device.Id, from, to);
            Summary = s.Count == 0
                ? $"No observations of {Device.Name} in this period."
                : $"{s.Count:N0} observations  |  RSSI min {s.Min} / avg {s.Average:0.0} / max {s.Max} dBm  |  longest silent interval {Fmt(s.LongestGap)}  |  first {s.FirstUtc?.ToLocalTime():dd MMM HH:mm:ss}, last {s.LastUtc?.ToLocalTime():dd MMM HH:mm:ss}";
            Note = "Breaks in the line mean no advertisements were heard, which is not proof the device disconnected. RSSI is a relative signal level, not a distance.";
        }
        catch (Exception ex) { Summary = "Could not load history: " + ex.Message; }
    }

    private static string Fmt(TimeSpan t) =>
        t.TotalSeconds < 120 ? $"{t.TotalSeconds:0} s" : t.TotalMinutes < 120 ? $"{t.TotalMinutes:0.0} min" : $"{t.TotalHours:0.0} h";

    private static LineSeries<DateTimePoint> Line(string name, IReadOnlyList<RssiPoint> pts, int bucketSeconds, SKColor color, float width)
    {
        var values = new List<DateTimePoint>();
        for (var i = 0; i < pts.Count; i++)
        {
            values.Add(new DateTimePoint(pts[i].BucketStartUtc.ToLocalTime(), pts[i].Average));
            // A gap of more than 3 buckets is drawn as a break, never joined up as if the device had been heard.
            if (i + 1 < pts.Count && (pts[i + 1].BucketStartUtc - pts[i].BucketStartUtc).TotalSeconds > bucketSeconds * 3)
                values.Add(new DateTimePoint(pts[i].BucketStartUtc.AddSeconds(bucketSeconds).ToLocalTime(), null));
        }
        return new LineSeries<DateTimePoint>
        {
            Name = name, Values = values, Fill = null, GeometrySize = values.Count < 60 ? 4 : 0, LineSmoothness = 0,
            Stroke = new SolidColorPaint(color) { StrokeThickness = width },
            GeometryStroke = new SolidColorPaint(color) { StrokeThickness = width },
        };
    }
}
