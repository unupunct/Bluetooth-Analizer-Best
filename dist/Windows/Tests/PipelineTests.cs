using BluetoothFleetMonitor.Application.Abstractions;
using BluetoothFleetMonitor.Application.Identity;
using BluetoothFleetMonitor.Application.Pipeline;
using BluetoothFleetMonitor.Application.Scanning;
using BluetoothFleetMonitor.Domain;

namespace BluetoothFleetMonitor.Tests;

public class PipelineTests
{
    private sealed class FakeStore : IFleetStore
    {
        public readonly List<IReadOnlyList<ProcessedObservation>> Batches = new();
        public readonly List<ApplicationEvent> Events = new();
        public int FailNextSaves;
        public Task<IReadOnlyList<KnownDevice>> LoadKnownDevicesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KnownDevice>>(Array.Empty<KnownDevice>());
        public Task<int> SaveBatchAsync(Guid scannerId, IReadOnlyList<ProcessedObservation> batch, CancellationToken ct = default)
        {
            if (FailNextSaves > 0) { FailNextSaves--; throw new InvalidOperationException("disk full"); }
            lock (Batches) Batches.Add(batch);
            return Task.FromResult(batch.Count);
        }
        public Task RecordEventAsync(ApplicationEvent evt, CancellationToken ct = default) { lock (Events) Events.Add(evt); return Task.CompletedTask; }
        public Task EnsureScannerAsync(Scanner scanner, CancellationToken ct = default) => Task.CompletedTask;
        public int TotalStored { get { lock (Batches) return Batches.Sum(b => b.Count); } }
    }

    private static BleAdvertisement Ad(ulong address = 0xAABBCCDDEE01, DateTime? observed = null) =>
        new(Guid.NewGuid(), observed ?? DateTime.UtcNow, address, AddressKind.Public, -60, "X",
            Array.Empty<ManufacturerBlock>(), Array.Empty<Guid>(), Array.Empty<byte>());

    private static ObservationPipeline NewPipeline(FakeStore store, int capacity = 1000, int batch = 100, Func<DateTime>? clock = null) =>
        new(store, Guid.NewGuid(), new PipelineOptions(capacity, batch, TimeSpan.FromMilliseconds(30)), clock);

    private static async Task RunUntilAsync(ObservationPipeline p, Func<bool> done, int timeoutMs = 3000)
    {
        using var cts = new CancellationTokenSource();
        var run = p.RunAsync(cts.Token);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(10);
        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Advertisements_are_written_in_batches_no_larger_than_the_batch_size()
    {
        var store = new FakeStore();
        var p = NewPipeline(store, batch: 4);
        for (var i = 0; i < 10; i++) Assert.True(p.TryEnqueue(Ad()));

        await RunUntilAsync(p, () => store.TotalStored == 10);

        Assert.Equal(10, store.TotalStored);
        Assert.All(store.Batches, b => Assert.InRange(b.Count, 1, 4));
    }

    [Fact]
    public async Task Overflow_is_counted_and_recorded_as_an_application_event()
    {
        var store = new FakeStore();
        var p = NewPipeline(store, capacity: 5);
        var accepted = Enumerable.Range(0, 20).Count(_ => p.TryEnqueue(Ad()));

        Assert.Equal(5, accepted);
        Assert.Equal(15, p.Overflowed);

        await RunUntilAsync(p, () => store.TotalStored == 5 && store.Events.Count > 0);

        var evt = store.Events.Single(e => e.EventType == "QueueOverflow");
        Assert.Contains("15", evt.Message);
        Assert.Equal(Severity.Warning, evt.Severity);
    }

    [Fact]
    public async Task Original_timestamp_is_preserved_and_processed_time_is_set_separately()
    {
        var store = new FakeStore();
        var processedAt = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        var p = NewPipeline(store, clock: () => processedAt);
        var observed = processedAt.AddDays(-1);
        p.TryEnqueue(Ad(observed: observed));

        await RunUntilAsync(p, () => store.TotalStored == 1);

        var o = store.Batches.Single().Single();
        Assert.Equal(observed, o.Ad.ObservedUtc);
        Assert.Equal(processedAt, o.ProcessedAtUtc);
    }

    [Fact]
    public async Task Same_new_stable_address_within_one_batch_maps_to_a_single_device()
    {
        var store = new FakeStore();
        var p = NewPipeline(store);
        p.TryEnqueue(Ad(0x111111111111));
        p.TryEnqueue(Ad(0x111111111111));
        p.TryEnqueue(Ad(0x222222222222));

        await RunUntilAsync(p, () => store.TotalStored == 3);

        var all = store.Batches.SelectMany(b => b).ToList();
        Assert.Equal(2, all.Select(o => o.DeviceId).Distinct().Count());
        Assert.Single(all.Where(o => o.IsNewDevice && o.Ad.Address == 0x111111111111));
    }

    [Fact]
    public async Task Cancelling_flushes_everything_already_queued()
    {
        var store = new FakeStore();
        var p = NewPipeline(store);
        for (var i = 0; i < 50; i++) p.TryEnqueue(Ad());

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await p.RunAsync(cts.Token);

        Assert.Equal(50, store.TotalStored);
    }

    [Fact]
    public async Task A_failed_write_is_retried_and_a_permanent_failure_is_recorded_not_silent()
    {
        var store = new FakeStore { FailNextSaves = 1 };
        var p = NewPipeline(store);
        p.TryEnqueue(Ad());
        await RunUntilAsync(p, () => store.TotalStored == 1);
        Assert.Equal(1, store.TotalStored); // retried successfully

        var store2 = new FakeStore { FailNextSaves = 100 };
        var p2 = NewPipeline(store2);
        p2.TryEnqueue(Ad());
        await RunUntilAsync(p2, () => store2.Events.Any(e => e.EventType == "WriteFailed"));
        var evt = Assert.Single(store2.Events, e => e.EventType == "WriteFailed");
        Assert.Equal(Severity.Error, evt.Severity);
        Assert.Contains("1", evt.Message);
    }
}
