namespace BluetoothFleetMonitor.Domain;

/// <summary>Kind of Bluetooth address a device advertised from. Random private addresses rotate, so they are never an identity on their own.</summary>
public enum AddressKind
{
    Unknown = 0,
    Public = 1,
    RandomStatic = 2,
    RandomResolvablePrivate = 3,
    RandomNonResolvablePrivate = 4,
}

public enum Severity { Info = 0, Warning = 1, Error = 2, Critical = 3 }

public static class PresenceEventTypes
{
    public const string Missing = "Missing";
    public const string Recovered = "Recovered";
}

public static class IdentifierTypes
{
    /// <summary>A stable address (public or random static). Value = 12 hex digits.</summary>
    public const string StableAddress = "StableAddress";
    /// <summary>Hash of manufacturer data prefix + service UUIDs. Survives address rotation.</summary>
    public const string Fingerprint = "Fingerprint";
    /// <summary>A rotating private address seen for this device. Informational only.</summary>
    public const string RotatingAddress = "RotatingAddress";
}

public class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? CustomName { get; set; }
    public string Category { get; set; } = "Unknown";
    public string? Manufacturer { get; set; }
    public string? AdvertisedName { get; set; }
    public string? IdentityFingerprint { get; set; }
    public string? AssignedRoom { get; set; }
    public string? AssignedWorkstation { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Denormalised statistics, maintained by the batch writer so the inventory stays fast at 500+ devices.
    public DateTime? FirstSeenUtc { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public short? LastRssi { get; set; }
    public short? MinRssi { get; set; }
    public short? MaxRssi { get; set; }
    public long RssiSum { get; set; }
    public long ObservationCount { get; set; }

    public double? AverageRssi => ObservationCount == 0 ? null : (double)RssiSum / ObservationCount;
    public string DisplayName => !string.IsNullOrWhiteSpace(CustomName) ? CustomName!
        : !string.IsNullOrWhiteSpace(AdvertisedName) ? AdvertisedName!
        : $"Device {Id.ToString("N")[..8]}";

    public List<DeviceIdentifier> Identifiers { get; set; } = new();
}

public class DeviceIdentifier
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public string IdentifierType { get; set; } = IdentifierTypes.StableAddress;
    public string IdentifierValue { get; set; } = "";
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public double Confidence { get; set; }
    public bool IsUserConfirmed { get; set; }
}

public class Scanner
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Platform { get; set; } = "Windows";
    public string? HardwareDescription { get; set; }
    public DateTime? LastSeen { get; set; }
    public string Status { get; set; } = "Unknown";
    public DateTime CreatedAt { get; set; }
}

public class Observation
{
    /// <summary>Unique observation id. Supplied by the scanner so uploads are idempotent.</summary>
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public Guid ScannerId { get; set; }
    /// <summary>When the advertisement was actually received (UTC). Never rewritten.</summary>
    public DateTime TimestampUtc { get; set; }
    /// <summary>When it reached the server. Null for observations made on the server itself.</summary>
    public DateTime? UploadedAtUtc { get; set; }
    /// <summary>When the server processed it.</summary>
    public DateTime ProcessedAtUtc { get; set; }
    public short Rssi { get; set; }
    public string Address { get; set; } = "";
    public AddressKind AddressKind { get; set; }
    public string? ManufacturerData { get; set; }
    public string? ServiceUuids { get; set; }
    public string? AdvertisementData { get; set; }
    public Guid? InspectionSessionId { get; set; }
    public string? LocationLabel { get; set; }
    public string Source { get; set; } = "Windows";
}

public class PresenceEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public Guid? ScannerId { get; set; }
    public string EventType { get; set; } = PresenceEventTypes.Missing;
    public DateTime TimestampUtc { get; set; }
    public TimeSpan? Duration { get; set; }
    public double Confidence { get; set; }
    public string? Explanation { get; set; }
}

public class ConnectionTest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public Guid? ScannerId { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public int Attempts { get; set; }
    public int SuccessfulAttempts { get; set; }
    public int FailedAttempts { get; set; }
    public int TimeoutCount { get; set; }
    public string? ErrorDetails { get; set; }
    public string? TestConfiguration { get; set; }
}

public class InspectionSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ScannerId { get; set; }
    public string? LocationLabel { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public string? Notes { get; set; }
}

public class FaultReport
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public string FaultType { get; set; } = "";
    public Severity Severity { get; set; }
    public double Confidence { get; set; }
    public double? HealthScore { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
}

public class UsbEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? HardwareId { get; set; }
    public string? VendorId { get; set; }
    public string? ProductId { get; set; }
    public string? DeviceInstanceId { get; set; }
    public string EventType { get; set; } = "";
    public DateTime TimestampUtc { get; set; }
}

public class ApplicationEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Source { get; set; } = "";
    public Severity Severity { get; set; }
    public string EventType { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTime TimestampUtc { get; set; }
}
