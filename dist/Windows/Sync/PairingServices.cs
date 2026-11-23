using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace BluetoothFleetMonitor.Sync;

public sealed class PairedPhone
{
    /// <summary>Also the id of this phone as a scanner in the database.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string PhoneId { get; set; } = "";
    /// <summary>SHA-256 of the access token. The token itself is only ever held by the phone.</summary>
    public string TokenHash { get; set; } = "";
    public DateTime PairedAtUtc { get; set; }
    public DateTime? LastUploadUtc { get; set; }
    /// <summary>Server time minus the phone's clock at the last upload. Positive: the phone is behind.</summary>
    public double? LastClockSkewSeconds { get; set; }
    public long ObservationsReceived { get; set; }
    public bool Revoked { get; set; }
}

/// <summary>Paired phones, kept in a small JSON file next to the database. Tokens are stored only as hashes.</summary>
public sealed class PairedPhoneStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private List<PairedPhone> _phones = new();

    public PairedPhoneStore(string path)
    {
        _path = path;
        try { if (File.Exists(path)) _phones = JsonSerializer.Deserialize<List<PairedPhone>>(File.ReadAllText(path)) ?? new(); } catch { _phones = new(); }
    }

    public event Action? Changed;

    public IReadOnlyList<PairedPhone> All() { lock (_lock) return _phones.Select(Clone).ToList(); }

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public PairedPhone? FindByToken(string token)
    {
        var h = Hash(token);
        lock (_lock)
        {
            foreach (var p in _phones)
                if (!p.Revoked && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(p.TokenHash), Encoding.ASCII.GetBytes(h))) return Clone(p);
            return null;
        }
    }

    /// <summary>Adds a phone and returns it with a fresh random token. The same phone id pairing again replaces its old entry.</summary>
    public (PairedPhone Phone, string Token) Add(string name, string phoneId)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var phone = new PairedPhone { Name = name, PhoneId = phoneId, TokenHash = Hash(token), PairedAtUtc = DateTime.UtcNow };
        lock (_lock)
        {
            var old = _phones.FirstOrDefault(p => p.PhoneId == phoneId);
            if (old is not null) { phone.Id = old.Id; phone.ObservationsReceived = old.ObservationsReceived; _phones.Remove(old); }
            _phones.Add(phone);
            Save();
        }
        Changed?.Invoke();
        return (Clone(phone), token);
    }

    public void RecordUpload(Guid id, int stored, double skewSeconds)
    {
        lock (_lock)
        {
            var p = _phones.FirstOrDefault(x => x.Id == id); if (p is null) return;
            p.LastUploadUtc = DateTime.UtcNow; p.LastClockSkewSeconds = Math.Round(skewSeconds, 1); p.ObservationsReceived += stored;
            Save();
        }
        Changed?.Invoke();
    }

    public bool Revoke(Guid id)
    {
        lock (_lock)
        {
            var p = _phones.FirstOrDefault(x => x.Id == id); if (p is null) return false;
            p.Revoked = true; Save();
        }
        Changed?.Invoke();
        return true;
    }

    private static PairedPhone Clone(PairedPhone p) => new()
    {
        Id = p.Id, Name = p.Name, PhoneId = p.PhoneId, TokenHash = p.TokenHash, PairedAtUtc = p.PairedAtUtc, LastUploadUtc = p.LastUploadUtc,
        LastClockSkewSeconds = p.LastClockSkewSeconds, ObservationsReceived = p.ObservationsReceived, Revoked = p.Revoked,
    };

    private void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_phones, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, true);
    }
}

/// <summary>One-time pairing codes: 8 digits, valid for five minutes, used once, with a lock-out after repeated wrong guesses.</summary>
public sealed class PairingService
{
    private readonly Func<DateTime> _clock;
    private readonly object _lock = new();
    private string? _code;
    private DateTime _expires;
    private int _failures;
    private DateTime _lockedUntil;

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    public const int MaxFailures = 5;

    public PairingService(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.UtcNow);

    public (string Code, DateTime ExpiresUtc) CreateInvite()
    {
        lock (_lock)
        {
            _code = RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8");
            _expires = _clock() + Lifetime; _failures = 0;
            return (_code, _expires);
        }
    }

    public bool HasOpenInvite { get { lock (_lock) return _code is not null && _clock() < _expires; } }

    /// <summary>True exactly once for the right, unexpired code. Five wrong attempts burn the invite.</summary>
    public bool TryConsume(string? code)
    {
        lock (_lock)
        {
            var now = _clock();
            if (_code is null || now >= _expires || now < _lockedUntil) return false;
            if (code is not null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(code), Encoding.ASCII.GetBytes(_code)))
            {
                _code = null; return true;
            }
            if (++_failures >= MaxFailures) { _code = null; _lockedUntil = now.AddMinutes(1); }
            return false;
        }
    }
}

/// <summary>
/// A self-signed certificate created on first run. The phone does not trust any authority: it pins this certificate's fingerprint,
/// which travels in the pairing QR code, so nobody on the network can pose as this PC.
/// </summary>
public sealed class CertificateService
{
    public X509Certificate2 Certificate { get; }
    public string Fingerprint { get; }

    public CertificateService(string directory)
    {
        var pfx = Path.Combine(directory, "sync-cert.pfx");
        var keyFile = Path.Combine(directory, "sync-cert.key");
        X509Certificate2? cert = null;
        try
        {
            if (File.Exists(pfx) && File.Exists(keyFile))
            {
                cert = X509CertificateLoader.LoadPkcs12FromFile(pfx, File.ReadAllText(keyFile).Trim());
                if (cert.NotAfter < DateTime.UtcNow.AddDays(30)) { cert.Dispose(); cert = null; }
            }
        }
        catch { cert = null; }

        if (cert is null)
        {
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest($"CN=Bluetooth Fleet Monitor ({Environment.MachineName})", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(Environment.MachineName); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback);
            req.CertificateExtensions.Add(san.Build());
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
            using var created = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
            File.WriteAllBytes(pfx, created.Export(X509ContentType.Pkcs12, password));
            File.WriteAllText(keyFile, password);
            cert = X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(pfx), password);
        }
        Certificate = cert;
        Fingerprint = Convert.ToHexString(SHA256.HashData(cert.RawData));
    }
}

public static class LocalNetwork
{
    /// <summary>The PC's usable IPv4 addresses on real network adapters, most likely LAN address first.</summary>
    public static IReadOnlyList<string> Addresses()
    {
        var result = new List<(int Rank, string Ip)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var desc = (nic.Description + " " + nic.Name).ToLowerInvariant();
            var virtualNic = desc.Contains("virtual") || desc.Contains("vmware") || desc.Contains("hyper-v") || desc.Contains("vethernet") || desc.Contains("wsl") || desc.Contains("docker") || desc.Contains("vpn");
            var props = nic.GetIPProperties();
            var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            foreach (var u in props.UnicastAddresses)
            {
                if (u.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ip = u.Address.ToString();
                if (ip.StartsWith("169.254.")) continue;
                result.Add(((hasGateway ? 0 : 2) + (virtualNic ? 4 : 0), ip));
            }
        }
        return result.OrderBy(r => r.Rank).Select(r => r.Ip).Distinct().ToList();
    }
}

public static class PairingLink
{
    /// <summary>bfm://pair?h=host&amp;p=port&amp;f=certificate-fingerprint&amp;c=code&amp;n=server-name</summary>
    public static string Build(string host, int port, string fingerprint, string code, string serverName) =>
        $"bfm://pair?h={Uri.EscapeDataString(host)}&p={port}&f={fingerprint}&c={code}&n={Uri.EscapeDataString(serverName)}";
}
