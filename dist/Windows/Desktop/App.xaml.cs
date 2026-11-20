using System.IO;
using System.Windows;
using BluetoothFleetMonitor.Application.Abstractions;
using BluetoothFleetMonitor.Application.Pipeline;
using BluetoothFleetMonitor.Bluetooth;
using BluetoothFleetMonitor.Database;
using BluetoothFleetMonitor.Desktop.Services;
using BluetoothFleetMonitor.Desktop.Shell;
using BluetoothFleetMonitor.Desktop.ViewModels;
using BluetoothFleetMonitor.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace BluetoothFleetMonitor.Desktop;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;

    public static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        var config = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json", optional: true).Build();
        var dataArg = ArgValue(args, "--data");
        var configured = config["Fleet:DataDirectory"];
        var dataDir = dataArg ?? (configured is { Length: > 0 }
            ? (Path.IsPathRooted(configured) ? configured : Path.Combine(AppContext.BaseDirectory, configured))
            : null);
        var paths = new AppPaths(dataDir);

        Log.Logger = new LoggerConfiguration().MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .WriteTo.File(Path.Combine(paths.LogsDir, "app-.log"), rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: config.GetValue("Fleet:LogRetentionDays", 14))
            .CreateLogger();

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Fatal(ex.Exception, "Unhandled UI exception");
            if (args.Contains("--scan-test") || args.Contains("--verify-ui")) { ex.Handled = true; Shutdown(5); return; } // never block an unattended run on a dialog
            MessageBox.Show("Unexpected error: " + ex.Exception.Message + "\n\nDetails are in the log folder:\n" + paths.LogsDir,
                "Bluetooth Fleet Monitor", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Fatal(ex.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error(ex.Exception, "Unobserved task exception"); ex.SetObserved(); };

        try
        {
            if (args.Contains("--scan-test"))
            {
                var seconds = int.TryParse(ArgValue(args, "--seconds"), out var s) ? s : 20;
                var code = await Headless.ScanTestAsync(paths, seconds, ArgValue(args, "--out") ?? Path.Combine(paths.DataDir, "scan-test-report.txt"));
                Shutdown(code);
                return;
            }

            if (args.Contains("--verify-ui"))
            {
                var code = await Headless.VerifyUiAsync(ArgValue(args, "--out") ?? Path.Combine(paths.DataDir, "verify-ui"));
                Shutdown(code);
                return;
            }

            _services = BuildServices(paths, config);
            await PrepareDatabaseAsync(_services);
            await _services.GetRequiredService<LiveRegistry>().LoadAsync(_services.GetRequiredService<IDbContextFactory<FleetDbContext>>());
            await _services.GetRequiredService<ScanController>().InitializeAsync();
            _services.GetRequiredService<EventFeed>().Start();

            var window = _services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Start-up failed");
            MessageBox.Show("Bluetooth Fleet Monitor could not start:\n\n" + ex.Message + "\n\nLog folder: " + paths.LogsDir,
                "Bluetooth Fleet Monitor", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    public static ServiceProvider BuildServices(AppPaths paths, IConfiguration config)
    {
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.AddSerilog(dispose: false));
        sc.AddSingleton(paths);
        sc.AddSingleton<SettingsStore>();
        sc.AddDbContextFactory<FleetDbContext>(o => o.UseSqlite(DatabaseMaintenance.BuildConnectionString(paths.DbPath)));
        sc.AddSingleton<IFleetStore, EfFleetStore>();
        sc.AddSingleton<DeviceAdminService>();
        sc.AddSingleton<HistoryQueries>();
        sc.AddSingleton<WindowsBleScanner>();
        sc.AddSingleton(sp => new ObservationPipeline(
            sp.GetRequiredService<IFleetStore>(), sp.GetRequiredService<SettingsStore>().Current.ScannerId,
            new PipelineOptions(
                config.GetValue("Pipeline:QueueCapacity", 10_000),
                config.GetValue("Pipeline:BatchSize", 500),
                TimeSpan.FromMilliseconds(config.GetValue("Pipeline:FlushIntervalMilliseconds", 1000))),
            logger: sp.GetRequiredService<ILoggerFactory>().CreateLogger<ObservationPipeline>()));
        sc.AddSingleton<LiveRegistry>();
        sc.AddSingleton(_ => Current.Dispatcher);
        sc.AddSingleton<EventFeed>();
        sc.AddSingleton<SelectionService>();
        sc.AddSingleton<ScanController>();
        sc.AddSingleton<DashboardViewModel>();
        sc.AddSingleton<LiveMonitorViewModel>();
        sc.AddSingleton<InventoryViewModel>();
        sc.AddSingleton<HistoryViewModel>();
        sc.AddSingleton<EventLogViewModel>();
        sc.AddSingleton<ScannerViewModel>();
        sc.AddSingleton<SettingsViewModel>();
        sc.AddSingleton<MainViewModel>();
        sc.AddSingleton<MainWindow>();
        return sc.BuildServiceProvider();
    }

    /// <summary>Backs the database up before any migration, migrates, then checks integrity. A failed check is recorded, not hidden.</summary>
    public static async Task PrepareDatabaseAsync(IServiceProvider sp)
    {
        var paths = sp.GetRequiredService<AppPaths>();
        var factory = sp.GetRequiredService<IDbContextFactory<FleetDbContext>>();
        await using var db = await factory.CreateDbContextAsync();

        if (File.Exists(paths.DbPath) && (await db.Database.GetPendingMigrationsAsync()).Any())
        {
            var backup = await DatabaseMaintenance.BackupAsync(paths.DbPath, paths.BackupsDir);
            Log.Information("Database backed up before migration: {Path}", backup);
        }
        await db.Database.MigrateAsync();

        if (!await DatabaseMaintenance.CheckIntegrityAsync(db))
        {
            Log.Error("SQLite integrity check failed");
            db.ApplicationEvents.Add(new ApplicationEvent
            {
                Source = "Database", Severity = Severity.Critical, EventType = "IntegrityCheckFailed", TimestampUtc = DateTime.UtcNow,
                Message = "SQLite integrity check failed at start-up. Back up data/fleet.db and consider restoring from data/backups.",
            });
            await db.SaveChangesAsync();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            // The container owns ScanController and the scanner, which are IAsyncDisposable only.
            // Disposing it asynchronously stops the scanner and lets the pipeline flush what it already accepted.
            _services?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(15));
        }
        catch (Exception ex) { Log.Error(ex, "Error during shutdown"); }
        finally { Log.CloseAndFlush(); }
        base.OnExit(e);
    }
}