using BluetoothFleetMonitor.Application.Identity;
using BluetoothFleetMonitor.Application.Scanning;
using BluetoothFleetMonitor.Domain;

namespace BluetoothFleetMonitor.Application.Abstractions;

/// <summary>An identifier to attach to a device, with how confident the resolver was.</summary>
public sealed record IdentifierRecord(string Type, string Value, double Confidence);

/// <summary>An advertisement after identity resolution, ready to be persisted.</summary>
public sealed record ProcessedObservation(
    BleAdvertisement Ad,
    Guid DeviceId,
    bool IsNewDevice,
    IReadOnlyList<IdentifierRecord> NewIdentifiers,
    DateTime ProcessedAtUtc);

public interface IFleetStore
{
    /// <summary>Devices and their identifiers, used to seed the in-memory identity index.</summary>
    Task<IReadOnlyList<KnownDevice>> LoadKnownDevicesAsync(CancellationToken ct = default);

    /// <summary>Persists a batch atomically. Observations whose id already exists are skipped (idempotent). Returns how many were newly stored.</summary>
    Task<int> SaveBatchAsync(Guid scannerId, IReadOnlyList<ProcessedObservation> batch, CancellationToken ct = default);

    Task RecordEventAsync(ApplicationEvent evt, CancellationToken ct = default);

    Task EnsureScannerAsync(Scanner scanner, CancellationToken ct = default);
}
