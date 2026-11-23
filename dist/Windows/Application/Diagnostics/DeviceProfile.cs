using System.Globalization;
using System.Text.RegularExpressions;
using BluetoothFleetMonitor.Application.Identity;
using BluetoothFleetMonitor.Domain;

namespace BluetoothFleetMonitor.Application.Diagnostics;

/// <summary>Names for the 16-bit service UUIDs people are most likely to meet. Anything else is shown as its own UUID, never guessed.</summary>
public static class ServiceNames
{
    private static readonly Dictionary<ushort, string> Known = new()
    {
        [0x1800] = "Generic Access", [0x1801] = "Generic Attribute", [0x180A] = "Device Information", [0x180D] = "Heart Rate", [0x180F] = "Battery",
        [0x1812] = "Human Interface Device (keyboard, mouse)", [0x181A] = "Environmental Sensing", [0xFEAA] = "Eddystone beacon",
        [0xFD5A] = "Samsung SmartTag", [0xFE2C] = "Google Fast Pair", [0xFD6F] = "Exposure Notification", [0xFEF3] = "Google", [0xFE9F] = "Google",
    };
    private const string BaseSuffix = "-0000-1000-8000-00805f9b34fb";

    public static string Describe(Guid uuid)
    {
        var s = uuid.ToString().ToLowerInvariant();
        if (s.StartsWith("0000", StringComparison.Ordinal) && s.EndsWith(BaseSuffix, StringComparison.Ordinal)
            && ushort.TryParse(s.AsSpan(4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id) && Known.TryGetValue(id, out var name))
            return name;
        return s;
    }
}

public sealed record DeviceProfileInput(string DisplayName, string? AdvertisedName, string Category, string? Room, string? Workstation,
    string? ManufacturerData, string? ServiceUuids, AddressKind AddressKind, bool HasStableAddress, bool HasFingerprint,
    DateTime? FirstSeenUtc, DateTime? LastSeenUtc, long ObservationCount, IReadOnlyDictionary<string, long> ObservationsBySource);

public sealed record ProfileLine(string Label, string Value);
public sealed record DeviceProfile(string Title, string? Maker, IReadOnlyList<ProfileLine> Lines, string? LookupQuery);

/// <summary>Turns what the database knows about a device into the "what is this device" panel. Unknown things are said to be unknown.</summary>
public static class DeviceProfileBuilder
{
    public static DeviceProfile Build(DeviceProfileInput i)
    {
        string? maker = null;
        var first = (i.ManufacturerData ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Split(':')[0];
        if (first is not null && ushort.TryParse(first, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var company)) maker = CompanyIds.Describe(company);

        var services = (i.ServiceUuids ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Guid.TryParse(s, out var g) ? ServiceNames.Describe(g) : s).Distinct().ToList();
        var mfrPrefix = (i.ManufacturerData ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        var lines = new List<ProfileLine>
        {
            new("Name", i.DisplayName),
            new("Maker", maker ?? "Unknown (it does not advertise a manufacturer)"),
            new("Type", string.IsNullOrWhiteSpace(i.Category) ? "Unknown" : i.Category),
            new("Services", services.Count == 0 ? "None announced" : string.Join(", ", services)),
        };
        if (mfrPrefix is not null) lines.Add(new("Manufacturer data", mfrPrefix.Length > 20 ? mfrPrefix[..20] + "..." : mfrPrefix));
        lines.Add(new("Address", i.AddressKind switch
        {
            AddressKind.Public => "Public address (stable)",
            AddressKind.RandomStatic => "Random static address (stable until the device resets)",
            AddressKind.RandomResolvablePrivate => "Rotating private address (changes over time)",
            AddressKind.RandomNonResolvablePrivate => "Rotating private address (cannot be resolved)",
            _ => "Address type unknown (a phone hides it)",
        }));
        lines.Add(new("Identity", i.HasStableAddress ? "Matched by a stable address"
            : i.HasFingerprint ? "Matched by its advertisement fingerprint (less certain than an address)" : "Not enough to identify it with certainty (a name alone is never used)"));
        if (!string.IsNullOrWhiteSpace(i.Room)) lines.Add(new("Room", i.Room!));
        if (!string.IsNullOrWhiteSpace(i.Workstation)) lines.Add(new("Workstation", i.Workstation!));
        if (i.FirstSeenUtc is { } f) lines.Add(new("First heard", f.ToLocalTime().ToString("dd MMM yyyy HH:mm")));
        if (i.LastSeenUtc is { } l) lines.Add(new("Last heard", l.ToLocalTime().ToString("dd MMM yyyy HH:mm")));
        lines.Add(new("Observations", i.ObservationCount.ToString("N0", CultureInfo.InvariantCulture)));
        foreach (var kv in i.ObservationsBySource.OrderByDescending(k => k.Value))
            lines.Add(new($"Heard by {(kv.Key == "Windows" ? "Windows (PC radio)" : kv.Key == "Android" ? "Android (phone)" : kv.Key)}", kv.Value.ToString("N0", CultureInfo.InvariantCulture)));

        return new DeviceProfile(i.DisplayName, maker, lines, LookupLink.Query(maker, i.AdvertisedName));
    }
}

/// <summary>The only thing that leaves the app for "Look up online": a search text made from the maker and the advertised name. Never an address.</summary>
public static class LookupLink
{
    private static readonly Regex GeneratedName = new(@"^Device [0-9a-fA-F]{8}$");
    private static readonly Regex AddressLike = new(@"^([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}$|^[0-9A-Fa-f]{12}$");

    public static string? Query(string? maker, string? advertisedName)
    {
        var knownMaker = !string.IsNullOrWhiteSpace(maker) && !maker!.StartsWith("Company 0x", StringComparison.Ordinal) ? maker.Trim() : null;
        string? name = null;
        if (!string.IsNullOrWhiteSpace(advertisedName) && !GeneratedName.IsMatch(advertisedName.Trim()))
        {
            var words = advertisedName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !AddressLike.IsMatch(w)).ToList();
            if (words.Count > 0) name = string.Join(' ', words);
        }
        if (knownMaker is null && name is null) return null;
        return string.Join(' ', new[] { knownMaker, name, "bluetooth" }.Where(s => s is not null));
    }

    public static string Url(string query) => "https://duckduckgo.com/?q=" + Uri.EscapeDataString(query);
}
