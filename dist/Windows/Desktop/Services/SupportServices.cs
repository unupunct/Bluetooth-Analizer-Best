using System.Collections.ObjectModel;
using System.Windows.Threading;
using BluetoothFleetMonitor.Database;
using BluetoothFleetMonitor.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;

namespace BluetoothFleetMonitor.Desktop.Services;

public sealed record EventRow(long Key, DateTime TimeUtc, string Severity, string Source, string Type, string Message)
{
    public string Time => TimeUtc.ToLocalTime().ToString("HH:mm:ss");
    public string Date => TimeUtc.ToLocalTime().ToString("yyyy-MM-dd");
}

/// <summary>Newest application/scanner events from the database. One poller shared by the Dashboard feed and the Event Log.</summary>
public sealed class EventFeed
{
    private readonly IDbContextFactory<FleetDbContext> _factory;
    private readonly DispatcherTimer _timer;
    private Guid? _topId;

    public ObservableCollection<EventRow> Items { get; } = new();

    public EventFeed(IDbContextFactory<FleetDbContext> factory, Dispatcher ui)
    {
        _factory = factory;
        _timer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public void Start() { _timer.Start(); _ = RefreshAsync(); }

    public async Task RefreshAsync()
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync();
            var newest = await db.ApplicationEvents.AsNoTracking().OrderByDescending(e => e.TimestampUtc).Take(300).ToListAsync();
            if (newest.Count > 0 && newest[0].Id == _topId && newest.Count == Items.Count) return;
            _topId = newest.FirstOrDefault()?.Id;
            Items.Clear();
            long k = 0;
            foreach (var e in newest) Items.Add(new EventRow(k++, e.TimestampUtc, e.Severity.ToString(), e.Source, e.EventType, e.Message));
        }
        catch { /* the feed is informational; a failed refresh must not take the app down */ }
    }
}

public sealed class SelectionService
{
    public Guid? SelectedDeviceId { get; private set; }
    public event Action<Guid>? OpenHistoryRequested;
    public void OpenHistory(Guid id) { SelectedDeviceId = id; OpenHistoryRequested?.Invoke(id); }
    public event Action<Guid>? OpenAnalysisRequested;
    public void OpenAnalysis(Guid id) { SelectedDeviceId = id; OpenAnalysisRequested?.Invoke(id); }
}

public static class Dialogs
{
    public static string? SaveFile(string title, string fileName, string filter)
    {
        var d = new SaveFileDialog { Title = title, FileName = fileName, Filter = filter };
        return d.ShowDialog() == true ? d.FileName : null;
    }

    public static string? OpenFile(string title, string filter)
    {
        var d = new OpenFileDialog { Title = title, Filter = filter };
        return d.ShowDialog() == true ? d.FileName : null;
    }
}
