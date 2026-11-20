using BluetoothFleetMonitor.Domain;

namespace BluetoothFleetMonitor.Application.Scanning;

public enum ScanMode { Passive = 0, Active = 1 }

public enum ScannerState
{
    Stopped,
    Starting,
    Running,
    /// <summary>The watcher says it is running but nothing has been heard. Usually a driver problem, not "no devices nearby".</summary>
    RunningNoAdvertisements,
    /// <summary>Stopped by the system (radio off, adapter removed, resource in use), not by the user.</summary>
    Interrupted,
    AdapterUnavailable,
    Error,
}

public sealed record ScannerStatus(ScannerState State, string Message, DateTime? LastAdvertisementUtc, DateTime ChangedUtc);

/// <summary>A period during which this scanner could not hear anything. Devices must not be blamed for silence inside one.</summary>
public sealed record CoverageGap(DateTime StartUtc, DateTime? EndUtc, string Reason);

public enum AddressTypeHint { Unspecified = 0, Public = 1, Random = 2 }

public static class AddressKindClassifier
{
    /// <summary>For random addresses the top two bits of the 48-bit address say which kind it is (Bluetooth Core spec).</summary>
    public static AddressKind Classify(AddressTypeHint hint, ulong address) => hint switch
    {
        AddressTypeHint.Public => AddressKind.Public,
        AddressTypeHint.Random => ((address >> 46) & 0b11) switch
        {
            0b11 => AddressKind.RandomStatic,
            0b01 => AddressKind.RandomResolvablePrivate,
            0b00 => AddressKind.RandomNonResolvablePrivate,
            _ => AddressKind.Unknown,
        },
        _ => AddressKind.Unknown,
    };
}

public interface IBleScanner : IAsyncDisposable
{
    ScannerStatus Status { get; }
    /// <summary>Description of the adapter in use, when the platform exposes it.</summary>
    string? AdapterDescription { get; }
    /// <summary>Raised on a background thread for every advertisement. Handlers must not block.</summary>
    event Action<BleAdvertisement>? Advertisement;
    event Action<ScannerStatus>? StatusChanged;
    /// <summary>Starts listening. Passive never sends scan requests; Active may, and is an explicit opt-in.</summary>
    Task StartAsync(ScanMode mode, CancellationToken ct = default);
    Task StopAsync();
}
