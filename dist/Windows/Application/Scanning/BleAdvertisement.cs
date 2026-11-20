using BluetoothFleetMonitor.Domain;

namespace BluetoothFleetMonitor.Application.Scanning;

public sealed record ManufacturerBlock(ushort CompanyId, byte[] Data);

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
    public string AddressText => Address.ToString("X12");
}
