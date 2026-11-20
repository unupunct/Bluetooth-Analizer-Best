using System.Windows.Threading;
using BluetoothFleetMonitor.Application.Abstractions;
using BluetoothFleetMonitor.Application.Pipeline;
using BluetoothFleetMonitor.Application.Scanning;
using BluetoothFleetMonitor.Bluetooth;
using BluetoothFleetMonitor.Domain;
using Microsoft.Extensions.Logging;

namespace BluetoothFleetMonitor.Desktop.Services;

/// <summary>Owns the scanner and the pipeline for the lifetime of the app and keeps the UI model current.</summary>
public sealed class ScanController : IAsyncDisposable
{
    private readonly WindowsBleScanner _scanner;
    private readonly ObservationPipeline _pipeline;
    private readonly IFleetStore _store;
    private readonly LiveRegistry _registry;
    private readonly SettingsStore _settings;
    private readonly Dispatcher _ui;
    private readonly ILogger<ScanController> _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _timer;
    private Task? _pipelineTask;
    private ScannerState _lastRecordedState = ScannerState.Stopped;
    private long _advertisements;
    private long _rateBase;
    private DateTime _rateAt = DateTime.UtcNow;

    public ScanController(WindowsBleScanner scanner, ObservationPipeline pipeline, IFleetStore store, LiveRegistry registry,
        SettingsStore settings, Dispatcher ui, ILogger<ScanController> log)
    {
        _scanner = scanner; _pipeline = pipeline; _store = store; _registry = registry; _settings = settings; _ui = ui; _log = log;
        _timer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
    }

    public ScannerStatus Status => _scanner.Status;
    public string? AdapterDescription => _scanner.AdapterDescription;
    public IReadOnlyList<CoverageGap> CoverageGaps => _scanner.CoverageGaps;
    public ScanMode Mode => _settings.Current.ScanMode.Equals("Active", StringComparison.OrdinalIgnoreCase) ? ScanMode.Active : ScanMode.Passive;
    public long AdvertisementsReceived => Interlocked.Read(ref _advertisements);
    public double AdvertisementsPerSecond { get; private set; }
    public long Stored => _pipeline.Stored;
    public long Overflowed => _pipeline.Overflowed;
    public long Lost => _pipeline.Lost;
    public int QueueDepth => _pipeline.QueueDepth;

    /// <summary>Raised on the UI thread about once a second and on every scanner state change.</summary>
    public event Action? Changed;

    public async Task InitializeAsync()
    {
        await _store.EnsureScannerAsync(new Scanner
        {
            Id = _settings.Current.ScannerId, Name = Environment.MachineName, Platform = "Windows",
            CreatedAt = DateTime.UtcNow, Status = "Starting", LastSeen = DateTime.UtcNow,
        });

        _scanner.Advertisement += ad => { Interlocked.Increment(ref _advertisements); _pipeline.TryEnqueue(ad); };
        _scanner.StatusChanged += OnStatusChanged;
        _pipeline.BatchStored += batch => _ui.BeginInvoke(() => _registry.ApplyBatch(batch));

        _pipelineTask = Task.Run(() => _pipeline.RunAsync(_cts.Token));
        _timer.Start();

        if (_settings.Current.AutoStartScan) await StartScanAsync();
    }

    public async Task StartScanAsync()
    {
        _log.LogInformation("Starting scan in {Mode} mode", Mode);
        await _scanner.StartAsync(Mode);
        Changed?.Invoke();
    }

    public async Task StopScanAsync()
    {
        await _scanner.StopAsync();
        Changed?.Invoke();
    }

    private void OnStatusChanged(ScannerStatus s)
    {
        _ui.BeginInvoke(() => Changed?.Invoke());
        if (s.State == _lastRecordedState) return;
        _lastRecordedState = s.State;
        var severity = s.State switch
        {
            ScannerState.Running or ScannerState.Stopped or ScannerState.Starting => Severity.Info,
            ScannerState.RunningNoAdvertisements or ScannerState.Interrupted or ScannerState.AdapterUnavailable => Severity.Warning,
            _ => Severity.Error,
        };
        _ = Task.Run(async () =>
        {
            try
            {
                await _store.RecordEventAsync(new ApplicationEvent
                {
                    Source = "Scanner", Severity = severity, EventType = s.State.ToString(), Message = s.Message, TimestampUtc = DateTime.UtcNow,
                });
            }
            catch (Exception ex) { _log.LogError(ex, "Could not record scanner event"); }
        });
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        var total = AdvertisementsReceived;
        var dt = (now - _rateAt).TotalSeconds;
        if (dt >= 1)
        {
            AdvertisementsPerSecond = (total - _rateBase) / dt;
            _rateBase = total;
            _rateAt = now;
        }
        _registry.RefreshStatuses(now, TimeSpan.FromSeconds(_settings.Current.PresenceWindowSeconds), Status.State, CoverageGaps);
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _timer.Stop();
        await _scanner.DisposeAsync();
        _cts.Cancel();
        if (_pipelineTask is not null)
        {
            try { await _pipelineTask.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception ex) { _log.LogWarning(ex, "Pipeline did not stop cleanly"); }
        }
    }
}
