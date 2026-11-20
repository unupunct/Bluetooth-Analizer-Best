using System.Threading.Channels;
using BluetoothFleetMonitor.Application.Abstractions;
using BluetoothFleetMonitor.Application.Identity;
using BluetoothFleetMonitor.Application.Scanning;
using BluetoothFleetMonitor.Domain;
using Microsoft.Extensions.Logging;

namespace BluetoothFleetMonitor.Application.Pipeline;

public sealed record PipelineOptions(int Capacity = 10_000, int BatchSize = 500, TimeSpan? FlushInterval = null)
{
    public TimeSpan EffectiveFlushInterval => FlushInterval ?? TimeSpan.FromSeconds(1);
}

/// <summary>
/// Scanner thread -> bounded queue -> identity resolution -> batched transactional write.
/// The queue is bounded so a burst cannot exhaust memory; when it is full the advertisement is
/// counted and the loss is recorded as an ApplicationEvent. Nothing is dropped silently.
/// </summary>
public sealed class ObservationPipeline
{
    private const int WriteAttempts = 3;

    private readonly IFleetStore _store;
    private readonly Guid _scannerId;
    private readonly PipelineOptions _options;
    private readonly Func<DateTime> _clock;
    private readonly ILogger? _log;
    private readonly Channel<BleAdvertisement> _channel;
    private readonly Dictionary<Guid, KnownDevice> _known = new();

    private long _overflowed;
    private long _overflowReported;
    private long _stored;
    private long _lost;

    public ObservationPipeline(IFleetStore store, Guid scannerId, PipelineOptions? options = null,
        Func<DateTime>? clock = null, ILogger? logger = null)
    {
        _store = store;
        _scannerId = scannerId;
        _options = options ?? new PipelineOptions();
        _clock = clock ?? (() => DateTime.UtcNow);
        _log = logger;
        _channel = Channel.CreateBounded<BleAdvertisement>(new BoundedChannelOptions(_options.Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait, // TryWrite returns false when full; we count it
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public long Overflowed => Interlocked.Read(ref _overflowed);
    public long Stored => Interlocked.Read(ref _stored);
    /// <summary>Observations lost to a permanent write failure (each loss also produced an ApplicationEvent).</summary>
    public long Lost => Interlocked.Read(ref _lost);
    public int QueueDepth => _channel.Reader.Count;

    /// <summary>Raised after a batch is durably stored. Handlers must not block.</summary>
    public event Action<IReadOnlyList<ProcessedObservation>>? BatchStored;

    /// <summary>Called from the scanner callback. Never blocks; returns false (and counts) when the queue is full.</summary>
    public bool TryEnqueue(BleAdvertisement ad)
    {
        if (_channel.Writer.TryWrite(ad)) return true;
        Interlocked.Increment(ref _overflowed);
        return false;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await ReloadKnownAsync();
        var reader = _channel.Reader;

        while (true)
        {
            try { if (!await reader.WaitToReadAsync(ct)) break; }
            catch (OperationCanceledException) { break; }

            // Let a burst coalesce into one transaction instead of one write per advertisement.
            if (reader.Count < _options.BatchSize)
            {
                try { await Task.Delay(_options.EffectiveFlushInterval, ct); }
                catch (OperationCanceledException) { /* fall through and drain */ }
            }

            await DrainAndFlushAsync(reader);
            await ReportOverflowAsync();
        }

        // Shutdown: write everything that was already accepted.
        _channel.Writer.TryComplete();
        while (reader.Count > 0) await DrainAndFlushAsync(reader);
        await ReportOverflowAsync();
    }

    private async Task DrainAndFlushAsync(ChannelReader<BleAdvertisement> reader)
    {
        var batch = new List<ProcessedObservation>(Math.Min(_options.BatchSize, Math.Max(reader.Count, 1)));
        while (batch.Count < _options.BatchSize && reader.TryRead(out var ad))
            batch.Add(Process(ad));
        if (batch.Count > 0) await FlushAsync(batch);
    }

    private ProcessedObservation Process(BleAdvertisement ad)
    {
        var decision = IdentityResolver.Resolve(ad, _known.Values);
        var isNew = decision.DeviceId is null;
        var deviceId = decision.DeviceId ?? Guid.NewGuid();

        if (!_known.TryGetValue(deviceId, out var device))
        {
            device = new KnownDevice(deviceId, new List<KnownIdentifier>());
            _known[deviceId] = device;
        }

        var ids = (List<KnownIdentifier>)device.Identifiers;
        var added = new List<IdentifierRecord>();
        void Add(string type, string value, double confidence)
        {
            if (ids.Any(i => i.Type == type && i.Value == value)) return;
            ids.Add(new KnownIdentifier(type, value));
            added.Add(new IdentifierRecord(type, value, confidence));
        }

        if (decision.StableAddress is not null) Add(IdentifierTypes.StableAddress, decision.StableAddress, decision.Confidence);
        else Add(IdentifierTypes.RotatingAddress, ad.AddressText, decision.Confidence);
        if (decision.Fingerprint is not null) Add(IdentifierTypes.Fingerprint, decision.Fingerprint, decision.Confidence);

        return new ProcessedObservation(ad, deviceId, isNew, added, _clock());
    }

    private async Task FlushAsync(List<ProcessedObservation> batch)
    {
        for (var attempt = 1; attempt <= WriteAttempts; attempt++)
        {
            try
            {
                var stored = await _store.SaveBatchAsync(_scannerId, batch, CancellationToken.None);
                Interlocked.Add(ref _stored, stored);
                try { BatchStored?.Invoke(batch); } catch (Exception ex) { _log?.LogError(ex, "BatchStored handler failed"); }
                return;
            }
            catch (Exception ex) when (attempt < WriteAttempts)
            {
                _log?.LogWarning(ex, "Batch write failed (attempt {Attempt}/{Max})", attempt, WriteAttempts);
                await Task.Delay(100 * attempt);
            }
            catch (Exception ex)
            {
                Interlocked.Add(ref _lost, batch.Count);
                _log?.LogError(ex, "Batch write failed permanently; {Count} observations lost", batch.Count);
                await RecordAsync(Severity.Error, "WriteFailed",
                    $"Failed to store {batch.Count} observations after {WriteAttempts} attempts: {ex.Message}");
                // The identity cache may now hold devices that were never saved. Rebuild it from the database.
                await ReloadKnownAsync();
                return;
            }
        }
    }

    private async Task ReloadKnownAsync()
    {
        try
        {
            var known = await _store.LoadKnownDevicesAsync(CancellationToken.None);
            _known.Clear();
            foreach (var k in known) _known[k.DeviceId] = new KnownDevice(k.DeviceId, k.Identifiers.ToList());
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Could not load known devices");
            await RecordAsync(Severity.Error, "IdentityIndexLoadFailed", $"Could not load known devices: {ex.Message}");
        }
    }

    private async Task ReportOverflowAsync()
    {
        var total = Interlocked.Read(ref _overflowed);
        var delta = total - _overflowReported;
        if (delta <= 0) return;
        _overflowReported = total;
        await RecordAsync(Severity.Warning, "QueueOverflow",
            $"{delta} advertisements were dropped because the observation queue was full (capacity {_options.Capacity}). Total dropped: {total}.");
    }

    private async Task RecordAsync(Severity severity, string type, string message)
    {
        try
        {
            await _store.RecordEventAsync(new ApplicationEvent
            {
                Source = "ObservationPipeline",
                Severity = severity,
                EventType = type,
                Message = message,
                TimestampUtc = _clock(),
            }, CancellationToken.None);
        }
        catch (Exception ex) { _log?.LogError(ex, "Could not record event {Type}", type); }
    }
}
