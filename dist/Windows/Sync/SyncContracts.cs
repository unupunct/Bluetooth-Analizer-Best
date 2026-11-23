namespace BluetoothFleetMonitor.Sync;

// Wire format of the local sync API (JSON, camelCase). Version 1. The phone sends what it measured; the PC never sends measurements back.

public sealed record HelloResponse(string Server, string Version, DateTime ServerUtc);

public sealed record PairRequest(string Code, string PhoneName, string PhoneId);
public sealed record PairResponse(string Token, string ServerName, string ScannerId, DateTime ServerUtc);

/// <summary>One sampling window from the phone. Rssi is the window average.</summary>
public sealed record ObservationDto(
    string Id, string Address, string? AddressKind, long TimestampMs, double Rssi, string? Name,
    string? ManufacturerData, string? ServiceUuids, string? AdvertisementData, string? SessionId, string? LocationLabel);

public sealed record SessionDto(string Id, string LocationLabel, long StartedMs, long? EndedMs, string? Notes);

public sealed record TestDto(string Id, string Address, long StartedMs, long DurationMs, string Outcome, string? Detail);

public sealed record UploadRequest(long SentAtMs, List<SessionDto>? Sessions, List<ObservationDto>? Observations, List<TestDto>? Tests);

public sealed record UploadResponse(int Received, int Stored, int Duplicates, int Rejected, DateTime ServerUtc, double ClockSkewSeconds);

public static class SyncLimits
{
    public const int MaxObservationsPerUpload = 2000;
    public const int MaxBodyBytes = 4 * 1024 * 1024;
    public const int DefaultPort = 47821;
}
