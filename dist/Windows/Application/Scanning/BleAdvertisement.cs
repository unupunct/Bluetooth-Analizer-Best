using BluetoothFleetMonitor.Domain;

namespace BluetoothFleetMonitor.Application.Scanning;

public sealed record ManufacturerBlock(ushort CompanyId, byte[] Data);

/// <summary>Where an observation came from when it did not come from this PC's own radio (a paired phone's upload).</summary>
public sealed record ObservationMeta(string Source, DateTime? UploadedAtUtc, Guid? SessionId, string? LocationLabel);

/// <summary>One advertisement as heard by a scanner. Immutable; timestamps are UTC and never rewritten.</summary>
public sealed record BleAdvertisement(
    Guid ObservationId,
    DateTime ObservedUtc,
    ulong Address,
    AddressKind AddressKind,
    short Rssi,
    string? Name,
    IReadOnlyList<ManufacturerBlock> Manufacturer,
    IReadOnlyList<Guid> ServiceUuids,
    byte[] Payload)
{
    /// <summary>Null for observations heard by this PC's own scanner.</summary>
    public ObservationMeta? Meta { get; init; }

    public string AddressText => Address.ToString("X12");
}
