using System.Security.Cryptography;
using System.Text;
using BluetoothFleetMonitor.Application.Scanning;
using BluetoothFleetMonitor.Domain;

namespace BluetoothFleetMonitor.Application.Identity;

public sealed record KnownIdentifier(string Type, string Value);

public sealed record KnownDevice(Guid DeviceId, IReadOnlyCollection<KnownIdentifier> Identifiers);

/// <param name="DeviceId">The matched device, or null when this advertisement belongs to a device not seen before.</param>
/// <param name="Fingerprint">Manufacturer-prefix + service-UUID hash, or null when the advertisement carries neither.</param>
/// <param name="Confidence">How sure we are of the match (1.0 stable address, 0.9 repeated rotating address, 0.7 fingerprint).</param>
/// <param name="StableAddress">Set when the address is stable (public / random static); the caller records it as an identifier.</param>
public sealed record IdentityDecision(Guid? DeviceId, string? Fingerprint, double Confidence, string Reason, string? StableAddress);

/// <summary>
/// Decides which device an advertisement belongs to. Rules (spec section 6):
/// a name is never an identity; random private addresses rotate, so they are never trusted alone;
/// when more than one device fits, we create a new device rather than guess (manual merge exists for that).
/// </summary>
public static class IdentityResolver
{
    private const int FingerprintPrefixBytes = 4;

    public static IdentityDecision Resolve(BleAdvertisement ad, IReadOnlyCollection<KnownDevice> known)
    {
        var fingerprint = ComputeFingerprint(ad.Manufacturer, ad.ServiceUuids);
        var address = ad.AddressText;

        if (ad.AddressKind is AddressKind.Public or AddressKind.RandomStatic)
        {
            var match = known.FirstOrDefault(k => k.Identifiers.Any(i => i.Type == IdentifierTypes.StableAddress && i.Value == address));
            return match is not null
                ? new IdentityDecision(match.DeviceId, fingerprint, 1.0, "Stable address match", address)
                : new IdentityDecision(null, fingerprint, 1.0, "New stable address", address);
        }

        // Private or unknown address: it may rotate at any time.
        var seen = known.FirstOrDefault(k => k.Identifiers.Any(i => i.Type == IdentifierTypes.RotatingAddress && i.Value == address));
        if (seen is not null)
            return new IdentityDecision(seen.DeviceId, fingerprint, 0.9, "Previously observed rotating address", null);

        if (fingerprint is null)
            return new IdentityDecision(null, null, 0.5, "No fingerprint (name only is not an identity): new device", null);

        var candidates = known.Where(k => k.Identifiers.Any(i => i.Type == IdentifierTypes.Fingerprint && i.Value == fingerprint)).ToList();
        return candidates.Count switch
        {
            1 => new IdentityDecision(candidates[0].DeviceId, fingerprint, 0.7, "Unique fingerprint match", null),
            0 => new IdentityDecision(null, fingerprint, 0.5, "New device: no fingerprint match", null),
            _ => new IdentityDecision(null, fingerprint, 0.5, $"Ambiguous: {candidates.Count} devices share this fingerprint; not merged", null),
        };
    }

    /// <summary>
    /// Stable across address rotation: company id plus the first bytes of each manufacturer block
    /// (the part that identifies the product, not the volatile counters) plus the sorted service UUIDs.
    /// Returns null when there is nothing but a name to go on.
    /// </summary>
    public static string? ComputeFingerprint(IReadOnlyList<ManufacturerBlock> manufacturer, IReadOnlyList<Guid> serviceUuids)
    {
        if (manufacturer.Count == 0 && serviceUuids.Count == 0) return null;

        var sb = new StringBuilder();
        foreach (var block in manufacturer.OrderBy(b => b.CompanyId))
        {
            sb.Append(block.CompanyId.ToString("X4")).Append(':');
            sb.Append(Convert.ToHexString(block.Data.AsSpan(0, Math.Min(FingerprintPrefixBytes, block.Data.Length)))).Append(';');
        }
        foreach (var u in serviceUuids.Select(u => u.ToString("D")).OrderBy(s => s, StringComparer.Ordinal))
            sb.Append(u).Append(';');

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..32];
    }
}
