using System.IO;
using System.Text.Json;

namespace BluetoothFleetMonitor.Desktop.Services;

/// <summary>Everything the app writes lives next to the exe under data/, so the folder stays portable between PCs.</summary>
public sealed class AppPaths
{
    public string DataDir { get; }
    public string DbPath => Path.Combine(DataDir, "fleet.db");
    public string LogsDir => Path.Combine(DataDir, "Logs");
    public string SettingsPath => Path.Combine(DataDir, "settings.json");
    public string BackupsDir => Path.Combine(DataDir, "backups");

    public AppPaths(string? dataDirOverride = null)
    {
        DataDir = Path.GetFullPath(dataDirOverride ?? Path.Combine(AppContext.BaseDirectory, "data"));
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
    }
}

public sealed class AppSettings
{
    /// <summary>"Passive" (listen only, default) or "Active" (may send scan requests; explicit opt-in).</summary>
    public string ScanMode { get; set; } = "Passive";
    /// <summary>A device not heard for this long is shown as Missing from recent scans (never "disconnected").</summary>
    public int PresenceWindowSeconds { get; set; } = 120;
    public bool AutoStartScan { get; set; } = true;
    public Guid ScannerId { get; set; } = Guid.NewGuid();
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;

    public AppSettings Current { get; private set; }
    public event Action? Changed;

    public SettingsStore(AppPaths paths)
    {
        _path = paths.SettingsPath;
        Current = Load();
        Save(); // persists the generated ScannerId on first run
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
        }
        catch { /* corrupt settings: fall back to defaults rather than refuse to start */ }
        return new AppSettings();
    }

    public void Save()
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(Current, Json));
        Changed?.Invoke();
    }
}
