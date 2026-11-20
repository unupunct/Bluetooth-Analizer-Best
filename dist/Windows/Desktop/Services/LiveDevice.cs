using CommunityToolkit.Mvvm.ComponentModel;

namespace BluetoothFleetMonitor.Desktop.Services;

/// <summary>UI-facing view of a device. Lives on the UI thread; updated in batches from the pipeline.</summary>
public sealed partial class LiveDevice : ObservableObject
{
    private const int SampleCap = 20;
    private readonly Queue<short> _samples = new();

    public Guid Id { get; }
    public LiveDevice(Guid id) => Id = id;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _category = "Unknown";
    [ObservableProperty] private string? _manufacturer;
    [ObservableProperty] private string? _room;
    [ObservableProperty] private string? _workstation;
    [ObservableProperty] private string? _notes;
    [ObservableProperty] private string? _customName;
    [ObservableProperty] private string? _advertisedName;
    [ObservableProperty] private short? _rssi;
    [ObservableProperty] private DateTime? _lastSeenUtc;
    [ObservableProperty] private DateTime? _firstSeenUtc;
    [ObservableProperty] private long _observations;
    [ObservableProperty] private string _trend = "-";
    [ObservableProperty] private string _status = "Uncertain";
    [ObservableProperty] private string _lastSeenText = "";
    [ObservableProperty] private string _rssiQuality = "";
    public string Type => "BLE";
    public string Source => "Windows";
    /// <summary>Phase 4 computes real health scores. Until then the column says so instead of inventing a number.</summary>
    public string Health => "-";

    public void RecomputeName() =>
        Name = !string.IsNullOrWhiteSpace(CustomName) ? CustomName!
             : !string.IsNullOrWhiteSpace(AdvertisedName) ? AdvertisedName!
             : $"Device {Id.ToString("N")[..8]}";

    public void AddSample(short rssi)
    {
        _samples.Enqueue(rssi);
        while (_samples.Count > SampleCap) _samples.Dequeue();
        Trend = ComputeTrend();
        RssiQuality = Describe(rssi);
    }

    /// <summary>Compares the older and newer halves of the recent samples. Needs 8+ samples or says nothing.</summary>
    private string ComputeTrend()
    {
        if (_samples.Count < 8) return "-";
        var arr = _samples.ToArray();
        var half = arr.Length / 2;
        var older = arr.Take(half).Average(x => (double)x);
        var newer = arr.Skip(arr.Length - half).Average(x => (double)x);
        var d = newer - older;
        return d >= 3 ? "Improving" : d <= -3 ? "Degrading" : "Stable";
    }

    /// <summary>A relative label only. RSSI depends on antenna, orientation and obstruction, so it is never a distance.</summary>
    public static string Describe(int rssi) => rssi switch
    {
        >= -55 => "Excellent",
        >= -67 => "Good",
        >= -78 => "Fair",
        >= -88 => "Weak",
        _ => "Very weak",
    };
}
