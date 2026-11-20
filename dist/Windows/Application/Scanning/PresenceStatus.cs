namespace BluetoothFleetMonitor.Application.Scanning;

public enum DevicePresence { Detected, Missing, Uncertain }

public static class PresenceStatus
{
    /// <summary>
    /// "Missing" only means "not heard recently while the scanner was demonstrably able to hear".
    /// It is never a statement that the device disconnected. If the scanner was stopped, deaf, or had a
    /// coverage gap anywhere between the last sighting and now, we cannot tell, so the answer is Uncertain.
    /// </summary>
    public static DevicePresence Evaluate(DateTime? lastSeenUtc, DateTime nowUtc, TimeSpan window,
        ScannerState scanner, IReadOnlyList<CoverageGap> gaps)
    {
        if (lastSeenUtc is null) return DevicePresence.Uncertain;
        if (nowUtc - lastSeenUtc.Value <= window) return DevicePresence.Detected;
        if (scanner != ScannerState.Running) return DevicePresence.Uncertain;

        var from = lastSeenUtc.Value;
        foreach (var g in gaps)
        {
            var end = g.EndUtc ?? DateTime.MaxValue;
            if (g.StartUtc <= nowUtc && end >= from) return DevicePresence.Uncertain;
        }
        return DevicePresence.Missing;
    }
}
