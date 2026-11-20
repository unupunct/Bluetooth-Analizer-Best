using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BluetoothFleetMonitor.Application.Abstractions;
using BluetoothFleetMonitor.Application.Pipeline;
using BluetoothFleetMonitor.Application.Scanning;
using BluetoothFleetMonitor.Bluetooth;
using BluetoothFleetMonitor.Database;
using BluetoothFleetMonitor.Desktop.Services;
using BluetoothFleetMonitor.Desktop.Shell;
using BluetoothFleetMonitor.Desktop.ViewModels;
using BluetoothFleetMonitor.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BluetoothFleetMonitor.Desktop;

/// <summary>
/// Self-tests that run without a person at the screen:
///   --scan-test  : runs the REAL scanner and pipeline for N seconds into a throw-away database and reports what happened.
///   --verify-ui  : renders every page off-screen from a throw-away database of clearly labelled TEST data and counts binding errors.
/// Neither touches the real database. The UI check never captures the desktop; it renders WPF visuals into a bitmap.
/// </summary>
public static class Headless
{
    private sealed class Counting : IDbContextFactory<FleetDbContext>
    {
        private readonly DbContextOptions<FleetDbContext> _o;
        public Counting(string path) => _o = new DbContextOptionsBuilder<FleetDbContext>().UseSqlite(DatabaseMaintenance.BuildConnectionString(path)).Options;
        public FleetDbContext CreateDbContext() => new(_o);
    }

    // ---------------------------------------------------------------- scan test

    public static async Task<int> ScanTestAsync(AppPaths paths, int seconds, string outFile)
    {
        var dbPath = Path.Combine(paths.DataDir, "scan-test.db");
        foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" }) if (File.Exists(f)) File.Delete(f);

        var factory = new Counting(dbPath);
        await using (var init = factory.CreateDbContext()) await init.Database.MigrateAsync();
        var store = new EfFleetStore(factory);
        var scannerId = Guid.NewGuid();
        await store.EnsureScannerAsync(new Scanner { Id = scannerId, Name = "scan-test", Platform = "Windows", CreatedAt = DateTime.UtcNow, Status = "Starting" });

        var pipeline = new ObservationPipeline(store, scannerId, new PipelineOptions(10_000, 500, TimeSpan.FromMilliseconds(500)));
        await using var scanner = new WindowsBleScanner();
        var states = new List<string>();
        scanner.StatusChanged += s => { lock (states) states.Add($"{DateTime.Now:HH:mm:ss}  {s.State}: {s.Message}"); };
        long received = 0;
        scanner.Advertisement += ad => { Interlocked.Increment(ref received); pipeline.TryEnqueue(ad); };

        using var cts = new CancellationTokenSource();
        var pipelineTask = Task.Run(() => pipeline.RunAsync(cts.Token));
        var sw = Stopwatch.StartNew();
        await scanner.StartAsync(ScanMode.Passive);
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        var finalStatus = scanner.Status;
        await scanner.StopAsync();
        cts.Cancel();
        await pipelineTask;

        var sb = new StringBuilder();
        sb.AppendLine($"Scan test - Passive mode, {seconds} s requested, {sw.Elapsed.TotalSeconds:0.0} s elapsed");
        sb.AppendLine($"Windows: {Environment.OSVersion.VersionString}");
        sb.AppendLine($"Adapter: {scanner.AdapterDescription ?? "(not reported)"}");
        sb.AppendLine($"Final scanner state before stop: {finalStatus.State} - {finalStatus.Message}");
        sb.AppendLine("State changes:"); lock (states) foreach (var s in states) sb.AppendLine("  " + s);
        sb.AppendLine($"Advertisements received: {received:N0}   stored: {pipeline.Stored:N0}   queue-overflow: {pipeline.Overflowed:N0}   lost to write errors: {pipeline.Lost:N0}");

        await using var db = factory.CreateDbContext();
        var devices = await db.Devices.AsNoTracking().ToListAsync();
        sb.AppendLine($"Distinct devices created: {devices.Count}");
        sb.AppendLine($"  with an advertised name: {devices.Count(d => !string.IsNullOrWhiteSpace(d.AdvertisedName))}");
        sb.AppendLine($"  names starting LVS-: {devices.Count(d => d.AdvertisedName?.StartsWith("LVS-", StringComparison.OrdinalIgnoreCase) == true)}");
        var kinds = await db.Observations.AsNoTracking().GroupBy(o => o.AddressKind).Select(g => new { g.Key, N = g.Count() }).ToListAsync();
        sb.AppendLine("Observations by address kind: " + string.Join(", ", kinds.Select(k => $"{k.Key}={k.N}")));
        var ids = await db.DeviceIdentifiers.AsNoTracking().GroupBy(i => i.IdentifierType).Select(g => new { g.Key, N = g.Count() }).ToListAsync();
        sb.AppendLine("Identifiers: " + string.Join(", ", ids.Select(k => $"{k.Key}={k.N}")));
        var rotatingMerged = await db.DeviceIdentifiers.AsNoTracking().Where(i => i.IdentifierType == IdentifierTypes.RotatingAddress)
            .GroupBy(i => i.DeviceId).Where(g => g.Count() > 1).CountAsync();
        sb.AppendLine($"Devices that absorbed more than one rotating address (matched by fingerprint): {rotatingMerged}");
        var maxMs = await db.Observations.AsNoTracking().Select(o => new { o.TimestampUtc, o.ProcessedAtUtc }).Take(5000).ToListAsync();
        if (maxMs.Count > 0) sb.AppendLine($"Observed-to-processed delay (sample): max {maxMs.Max(x => (x.ProcessedAtUtc - x.TimestampUtc).TotalSeconds):0.00} s");
        sb.AppendLine("Top devices by observations (address tails only):");
        foreach (var d in devices.OrderByDescending(d => d.ObservationCount).Take(5))
            sb.AppendLine($"  {d.DisplayName,-28} obs={d.ObservationCount,5}  rssi {d.MinRssi}..{d.MaxRssi}  mfr={d.Manufacturer ?? "-"}");
        var ok = pipeline.Stored > 0;
        sb.AppendLine(ok ? "RESULT: PASS - real advertisements were received, identified and stored."
            : received == 0 ? "RESULT: NO DATA - scanner started but heard nothing (see state above; usually a driver or adapter problem)."
            : "RESULT: FAIL - advertisements arrived but nothing was stored.");
        await File.WriteAllTextAsync(outFile, sb.ToString());

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return ok ? 0 : received == 0 ? 2 : 3;
    }

    // ---------------------------------------------------------------- UI verification

    private sealed class BindingListener : System.Diagnostics.TraceListener
    {
        public readonly List<string> Errors = new();
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (!string.IsNullOrWhiteSpace(message)) lock (Errors) Errors.Add(message); }
    }

    public static async Task<int> VerifyUiAsync(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var tmp = Path.Combine(Path.GetTempPath(), "bfm-verify-" + Guid.NewGuid().ToString("N"));
        var report = new StringBuilder();
        var failures = 0;
        var listener = new BindingListener();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

        var paths = new AppPaths(tmp);
        var config = new ConfigurationBuilder().Build();
        await using var sp = App.BuildServices(paths, config);
        await App.PrepareDatabaseAsync(sp);

        // TEST DATA ONLY: this database is thrown away and the devices are labelled as such.
        var store = sp.GetRequiredService<IFleetStore>();
        var scannerId = sp.GetRequiredService<SettingsStore>().Current.ScannerId;
        await store.EnsureScannerAsync(new Scanner { Id = scannerId, Name = "verify-ui", Platform = "Windows", CreatedAt = DateTime.UtcNow });
        var now = DateTime.UtcNow;
        var rng = new Random(42);
        var batch = new List<ProcessedObservation>();
        for (var d = 0; d < 12; d++)
        {
            var id = Guid.NewGuid();
            var lastOffset = d < 8 ? 5 : 3600;               // 4 devices are "stale" so Missing/Uncertain rows exist
            for (var i = 0; i < 120; i++)
            {
                var t = now.AddSeconds(-lastOffset - i * 20);
                var ad = new BleAdvertisement(Guid.NewGuid(), t, 0xA0A000000000UL + (ulong)d, AddressKind.Public, (short)(-55 - d * 3 - rng.Next(0, 8)),
                    $"VERIFY-UI TEST DEVICE {d + 1:00}", new[] { new ManufacturerBlock(0x004C, new byte[] { 1, 2, 3, 4 }) }, Array.Empty<Guid>(), Array.Empty<byte>());
                batch.Add(new ProcessedObservation(ad, id, i == 119, i == 119
                    ? new[] { new IdentifierRecord(IdentifierTypes.StableAddress, ad.AddressText, 1.0) } : Array.Empty<IdentifierRecord>(), now));
            }
        }
        batch = batch.OrderBy(b => b.Ad.ObservedUtc).ToList();
        batch[0] = batch[0]; // oldest first so IsNewDevice rows precede later ones is not required by the store
        await store.SaveBatchAsync(scannerId, batch);
        await store.RecordEventAsync(new ApplicationEvent { Source = "verify-ui", Severity = Severity.Warning, EventType = "TestEvent", Message = "TEST EVENT (verify-ui)", TimestampUtc = now });

        var registry = sp.GetRequiredService<LiveRegistry>();
        await registry.LoadAsync(sp.GetRequiredService<IDbContextFactory<FleetDbContext>>());
        registry.RefreshStatuses(now, TimeSpan.FromSeconds(120), ScannerState.Running, Array.Empty<CoverageGap>());
        await sp.GetRequiredService<EventFeed>().RefreshAsync();

        var window = sp.GetRequiredService<MainWindow>();
        window.Left = -20000; window.Top = 0; window.ShowInTaskbar = false;
        window.Show();
        var main = sp.GetRequiredService<MainViewModel>();
        var selection = sp.GetRequiredService<SelectionService>();
        var first = registry.Devices.First();

        report.AppendLine("UI verification (TEST DATA in a throw-away database; nothing here is a real measurement)");
        report.AppendLine($"Devices seeded: {registry.Registered}  Detected={registry.Detected} Missing={registry.Missing} Uncertain={registry.Uncertain}");
        if (registry.Detected != 8 || registry.Uncertain + registry.Missing != 4) { failures++; report.AppendLine("FAIL: status counts are not 8 detected / 4 stale"); }

        foreach (var item in main.Items.ToList())
        {
            var before = listener.Errors.Count;
            if (item.Key == "history") selection.OpenHistory(first.Id);
            else if (item.Key == "inventory") { sp.GetRequiredService<InventoryViewModel>().Selected = first; main.Selected = item; }
            else main.Selected = item;

            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(item.Key == "history" ? 1500 : 250);
            window.UpdateLayout();

            var w = (int)window.ActualWidth; var h = (int)window.ActualHeight;
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(window);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(rtb));
            var file = Path.Combine(outDir, $"{item.Key}.png");
            await using (var fs = File.Create(file)) png.Save(fs);

            var newErrors = listener.Errors.Skip(before).ToList();
            var line = $"{item.Title,-18} rendered {w}x{h}  binding errors: {newErrors.Count}  file: {Path.GetFileName(file)}";
            if (newErrors.Count > 0) { failures++; line += "  <-- FAIL"; }
            report.AppendLine(line);
            foreach (var e in newErrors.Take(3)) report.AppendLine("    " + e.Replace('\n', ' ').Trim());
        }

        var history = sp.GetRequiredService<HistoryViewModel>();
        report.AppendLine($"History chart series: {history.Series.Length}  summary: {history.Summary}");
        if (history.Series.Length == 0) { failures++; report.AppendLine("FAIL: history produced no series"); }
        report.AppendLine(failures == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({failures} problems)");
        await File.WriteAllTextAsync(Path.Combine(outDir, "report.txt"), report.ToString());

        window.Close();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(tmp, true); } catch { /* temp cleanup is best effort */ }
        return failures == 0 ? 0 : 4;
    }
}
