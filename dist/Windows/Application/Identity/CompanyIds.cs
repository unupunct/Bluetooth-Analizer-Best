namespace BluetoothFleetMonitor.Application.Identity;

/// <summary>Bluetooth SIG company identifiers for the vendors most likely to matter here. Unknown ids are shown as hex, never guessed.</summary>
public static class CompanyIds
{
    private static readonly Dictionary<ushort, string> Names = new()
    {
        [0x0002] = "Intel",
        [0x0006] = "Microsoft",
        [0x000D] = "Texas Instruments",
        [0x000F] = "Broadcom",
        [0x0046] = "MediaTek",
        [0x004C] = "Apple",
        [0x0059] = "Nordic Semiconductor",
        [0x0075] = "Samsung",
        [0x0087] = "Garmin",
        [0x00E0] = "Google",
        [0x0171] = "Amazon",
        [0x01DA] = "Logitech",
    };

    public static string Describe(ushort companyId) =>
        Names.TryGetValue(companyId, out var n) ? n : $"Company 0x{companyId:X4}";
}
