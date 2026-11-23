using BluetoothFleetMonitor.Application.Identity;
using BluetoothFleetMonitor.Application.Scanning;
using BluetoothFleetMonitor.Domain;

namespace BluetoothFleetMonitor.Tests;

public class IdentityResolverTests
{
    private static readonly Guid UuidA = Guid.Parse("0000fff0-0000-1000-8000-00805f9b34fb");
    private static readonly Guid UuidB = Guid.Parse("0000fff1-0000-1000-8000-00805f9b34fb");

    private static BleAdvertisement Ad(ulong address, AddressKind kind, string? name = null,
        ManufacturerBlock[]? mfr = null, Guid[]? uuids = null) =>
        new(Guid.NewGuid(), DateTime.UtcNow, address, kind, -60, name,
            mfr ?? Array.Empty<ManufacturerBlock>(), uuids ?? Array.Empty<Guid>(), Array.Empty<byte>());

    private static ManufacturerBlock Mfr(ushort company, params byte[] data) => new(company, data);

    private static KnownDevice Known(Guid id, string type, string value) => new(id, new[] { new KnownIdentifier(type, value) });

    [Fact]
    public void Public_address_matches_known_device_with_full_confidence()
    {
        var id = Guid.NewGuid();
        var known = new[] { Known(id, IdentifierTypes.StableAddress, "AABBCCDDEEFF") };

        var d = IdentityResolver.Resolve(Ad(0xAABBCCDDEEFF, AddressKind.Public, "LVS-Lush"), known);

        Assert.Equal(id, d.DeviceId);
        Assert.Equal(1.0, d.Confidence);
    }

    [Fact]
    public void Unknown_public_address_creates_a_new_device_even_if_name_matches_another()
    {
        var existing = Guid.NewGuid();
        var known = new[] { Known(existing, IdentifierTypes.StableAddress, "111111111111") };

        var d = IdentityResolver.Resolve(Ad(0x222222222222, AddressKind.Public, "LVS-Lush"), known);

        Assert.Null(d.DeviceId);
        Assert.Equal("222222222222", d.StableAddress);
    }

    [Fact]
    public void Rotating_random_address_matches_by_fingerprint_when_exactly_one_device_fits()
    {
        var id = Guid.NewGuid();
        var mfr = new[] { Mfr(0x004C, 0x10, 0x05, 0x01, 0x02) };
        var fp = IdentityResolver.ComputeFingerprint(mfr, new[] { UuidA })!;
        var known = new[] { Known(id, IdentifierTypes.Fingerprint, fp) };

        var d = IdentityResolver.Resolve(Ad(0x6A0000000001, AddressKind.RandomResolvablePrivate, null, mfr, new[] { UuidA }), known);

        Assert.Equal(id, d.DeviceId);
        Assert.Equal(0.7, d.Confidence);
    }

    [Fact]
    public void Same_name_alone_never_matches()
    {
        var id = Guid.NewGuid();
        var known = new[] { Known(id, IdentifierTypes.RotatingAddress, "6A0000000009") };

        var d = IdentityResolver.Resolve(Ad(0x6A0000000001, AddressKind.RandomResolvablePrivate, "LVS-Lush"), known);

        Assert.Null(d.DeviceId);
        Assert.Null(d.Fingerprint); // name is not part of the fingerprint
    }

    [Fact]
    public void Two_devices_with_the_same_name_stay_separate()
    {
        var a = IdentityResolver.Resolve(Ad(0xAAAAAAAAAAAA, AddressKind.Public, "LVS-Lush"), Array.Empty<KnownDevice>());
        var known = new[] { Known(Guid.NewGuid(), IdentifierTypes.StableAddress, a.StableAddress!) };

        var b = IdentityResolver.Resolve(Ad(0xBBBBBBBBBBBB, AddressKind.Public, "LVS-Lush"), known);

        Assert.Null(b.DeviceId);
    }

    [Fact]
    public void Ambiguous_fingerprint_creates_a_new_device_instead_of_guessing()
    {
        var mfr = new[] { Mfr(0x0A12, 0x01, 0x02, 0x03, 0x04) };
        var fp = IdentityResolver.ComputeFingerprint(mfr, new[] { UuidA, UuidB })!;
        var known = new[]
        {
            Known(Guid.NewGuid(), IdentifierTypes.Fingerprint, fp),
            Known(Guid.NewGuid(), IdentifierTypes.Fingerprint, fp),
        };

        var d = IdentityResolver.Resolve(Ad(0x6A0000000002, AddressKind.RandomNonResolvablePrivate, null, mfr, new[] { UuidA, UuidB }), known);

        Assert.Null(d.DeviceId);
        Assert.Contains("ambiguous", d.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Previously_seen_rotating_address_matches_with_high_confidence()
    {
        var id = Guid.NewGuid();
        var known = new[] { Known(id, IdentifierTypes.RotatingAddress, "6A0000000003") };

        var d = IdentityResolver.Resolve(Ad(0x6A0000000003, AddressKind.RandomResolvablePrivate), known);

        Assert.Equal(id, d.DeviceId);
        Assert.Equal(0.9, d.Confidence);
    }

    [Fact]
    public void Unknown_address_kind_never_creates_a_stable_identity_but_can_match_an_existing_one()
    {
        var id = Guid.NewGuid();
        var known = new[] { Known(id, IdentifierTypes.StableAddress, "CCCCCCCCCCCC") };

        // A phone cannot say what kind of address it heard, but it is the same over-the-air address the PC recorded as stable.
        var same = IdentityResolver.Resolve(Ad(0xCCCCCCCCCCCC, AddressKind.Unknown), known);
        Assert.Equal(id, same.DeviceId);
        Assert.Null(same.StableAddress);                     // matching never records the unknown-kind address as a new stable identifier

        // An address nobody recorded as stable is not trusted as one.
        var other = IdentityResolver.Resolve(Ad(0xDDDDDDDDDDDD, AddressKind.Unknown), known);
        Assert.Null(other.DeviceId);
        Assert.Null(other.StableAddress);
    }

    [Fact]
    public void Fingerprint_ignores_volatile_payload_bytes_and_service_uuid_order()
    {
        var f1 = IdentityResolver.ComputeFingerprint(new[] { Mfr(0x004C, 1, 2, 3, 4, 99, 100) }, new[] { UuidA, UuidB });
        var f2 = IdentityResolver.ComputeFingerprint(new[] { Mfr(0x004C, 1, 2, 3, 4, 7, 8) }, new[] { UuidB, UuidA });
        Assert.Equal(f1, f2);
    }

    [Fact]
    public void No_manufacturer_data_and_no_uuids_gives_no_fingerprint()
    {
        Assert.Null(IdentityResolver.ComputeFingerprint(Array.Empty<ManufacturerBlock>(), Array.Empty<Guid>()));
    }

    // Cross-platform contract: the Android app must compute exactly these values (see Android FingerprintTest).
    [Fact]
    public void Fingerprint_matches_the_cross_platform_reference_vectors()
    {
        var withUuids = IdentityResolver.ComputeFingerprint(new[] { Mfr(0x004C, 1, 2, 3, 4, 9, 9) }, new[] { UuidB, UuidA });
        Assert.Equal("24407DB34235BDBF1BA91E61BA0EAF7E", withUuids);

        var mfrOnly = IdentityResolver.ComputeFingerprint(new[] { Mfr(0x004C, 1, 2, 3, 4) }, Array.Empty<Guid>());
        Assert.Equal("60804AF1DAFC1C460C2EAEC11C21DC6D", mfrOnly);
    }
}