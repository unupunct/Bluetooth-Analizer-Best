using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BluetoothFleetMonitor.Application.Abstractions;
using BluetoothFleetMonitor.Application.Pipeline;
using BluetoothFleetMonitor.Application.Scanning;
using BluetoothFleetMonitor.Database;
using BluetoothFleetMonitor.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BluetoothFleetMonitor.Sync;

/// <summary>
/// The local sync endpoint a paired phone uploads to. HTTPS only, with the self-signed certificate the phone pinned at pairing;
/// every upload needs the phone's own access token. Nothing here talks to the internet and nothing is ever sent back except counts.
/// </summary>
public sealed class SyncServer : IAsyncDisposable
{
    private readonly ObservationPipeline _pipeline;
    private readonly IFleetStore _store;
    private readonly IDbContextFactory<FleetDbContext> _factory;
    private readonly PairedPhoneStore _phones;
    private readonly PairingService _pairing;
    private readonly CertificateService _cert;
    private readonly string _serverName;
    private readonly string _version;
    private WebApplication? _app;
    private readonly Dictionary<Guid, DateTime> _lastSkewWarning = new();   // one clock warning per phone per 30 minutes, not one per 500-row batch

    public SyncServer(ObservationPipeline pipeline, IFleetStore store, IDbContextFactory<FleetDbContext> factory, PairedPhoneStore phones,
        PairingService pairing, CertificateService cert, string version, string? serverName = null)
    {
        _pipeline = pipeline; _store = store; _factory = factory; _phones = phones; _pairing = pairing; _cert = cert;
        _version = version; _serverName = serverName ?? Environment.MachineName;
    }

    public bool IsRunning => _app is not null;
    public int Port { get; private set; }
    public string? LastError { get; private set; }
    public string ServerName => _serverName;
    public event Action? StateChanged;

    public async Task StartAsync(int port, bool loopbackOnly = false)
    {
        if (_app is not null) return;
        try
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.AddServerHeader = false;
                k.Limits.MaxRequestBodySize = SyncLimits.MaxBodyBytes;
                k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
                void Https(ListenOptions o) => o.UseHttps(h => h.ServerCertificate = _cert.Certificate);
                if (loopbackOnly) k.Listen(System.Net.IPAddress.Loopback, port, Https); else k.ListenAnyIP(port, Https);
            });
            var app = builder.Build();
            Map(app);
            await app.StartAsync();
            Port = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
                .Select(a => new Uri(a.Replace("[::]", "localhost").Replace("0.0.0.0", "localhost").Replace("*", "localhost")).Port).First();
            _app = app; LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.InnerException?.Message ?? ex.Message;
            _app = null;
        }
        StateChanged?.Invoke();
    }

    public async Task StopAsync()
    {
        var app = _app; _app = null;
        if (app is not null) { try { await app.StopAsync(); await app.DisposeAsync(); } catch { /* shutting down */ } }
        StateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private void Map(WebApplication app)
    {
        app.MapGet("/api/v1/hello", () => Results.Ok(new HelloResponse(_serverName, _version, DateTime.UtcNow)));

        app.MapPost("/api/v1/pair", async (PairRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.PhoneId) || req.PhoneId.Length > 64 || (req.PhoneName?.Length ?? 0) > 80) return Results.BadRequest(new { error = "invalid request" });
            if (!_pairing.TryConsume(req.Code)) return Results.Json(new { error = "pairing code is wrong, expired or already used" }, statusCode: 403);
            var name = string.IsNullOrWhiteSpace(req.PhoneName) ? "Android phone" : req.PhoneName.Trim();
            var (phone, token) = _phones.Add(name, req.PhoneId);
            await _store.EnsureScannerAsync(new Scanner { Id = phone.Id, Name = name, Platform = "Android", Status = "Paired", CreatedAt = DateTime.UtcNow, LastSeen = DateTime.UtcNow });
            await Event(Severity.Info, "PhonePaired", $"Phone \"{name}\" paired for sync.");
            return Results.Ok(new PairResponse(token, _serverName, phone.Id.ToString(), DateTime.UtcNow));
        });

        app.MapPost("/api/v1/upload", async (HttpRequest http, UploadRequest req, CancellationToken ct) =>
        {
            var auth = http.Headers.Authorization.ToString();
            var phone = auth.StartsWith("Bearer ", StringComparison.Ordinal) ? _phones.FindByToken(auth[7..].Trim()) : null;
            if (phone is null) return Results.Json(new { error = "not paired or access revoked" }, statusCode: 401);
            var obs = req.Observations ?? new();
            if (obs.Count > SyncLimits.MaxObservationsPerUpload) return Results.BadRequest(new { error = $"at most {SyncLimits.MaxObservationsPerUpload} observations per upload" });

            var now = DateTime.UtcNow;
            var skew = (now - DateTimeOffset.FromUnixTimeMilliseconds(req.SentAtMs).UtcDateTime).TotalSeconds;
            var ads = new List<BleAdvertisement>(obs.Count); var rejected = 0;
            foreach (var o in obs)
            {
                var ad = Map(o, phone, now);
                if (ad is null) rejected++; else ads.Add(ad);
            }

            int stored;
            try
            {
                // Sessions first so the observations that refer to them are never orphaned.
                await UpsertSessionsAsync(phone, req.Sessions, ct);
                stored = await _pipeline.IngestAsync(phone.Id, ads, ct);
                await StoreTestsAsync(phone, req.Tests, ct);
            }
            catch (Exception ex)
            {
                await Event(Severity.Error, "SyncUploadFailed", $"Upload from \"{phone.Name}\" could not be stored: {ex.Message}");
                return Results.Json(new { error = "could not store the upload; keep your data and retry" }, statusCode: 500);   // the phone only marks data as sent on a 200
            }

            _phones.RecordUpload(phone.Id, stored, skew);
            await _store.EnsureScannerAsync(new Scanner { Id = phone.Id, Name = phone.Name, Platform = "Android", Status = "Paired", CreatedAt = phone.PairedAtUtc, LastSeen = now });
            if (obs.Count > 0 || (req.Sessions?.Count ?? 0) > 0)
            {
                var warnSkew = false;
                if (Math.Abs(skew) > 5)
                    lock (_lastSkewWarning)
                        if (!_lastSkewWarning.TryGetValue(phone.Id, out var last) || now - last > TimeSpan.FromMinutes(30)) { _lastSkewWarning[phone.Id] = now; warnSkew = true; }
                await Event(warnSkew ? Severity.Warning : Severity.Info, "SyncUpload",
                    $"\"{phone.Name}\": received {obs.Count}, stored {stored}, duplicates {ads.Count - stored}, rejected {rejected}" +
                    (warnSkew ? $". The phone clock differs from this PC by {skew:0.#} s (original times are kept)." : "."));
            }
            return Results.Ok(new UploadResponse(obs.Count, stored, ads.Count - stored, rejected, now, Math.Round(skew, 1)));
        });
    }

    private static BleAdvertisement? Map(ObservationDto o, PairedPhone phone, DateTime now)
    {
        if (!Guid.TryParse(o.Id, out var id)) return null;
        var hex = (o.Address ?? "").Replace(":", "").Replace("-", "");
        if (hex.Length != 12 || !ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address)) return null;
        var at = DateTimeOffset.FromUnixTimeMilliseconds(o.TimestampMs).UtcDateTime;
        if (o.TimestampMs <= 0 || at > now.AddMinutes(10)) return null;                       // a phone with a wrong clock never writes into the future
        if (double.IsNaN(o.Rssi) || o.Rssi is < -127 or > 20) return null;
        var kind = Enum.TryParse<AddressKind>(o.AddressKind, true, out var k) ? k : AddressKind.Unknown;

        var blocks = new List<ManufacturerBlock>();
        foreach (var part in (o.ManufacturerData ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var bits = part.Split(':');
            if (bits.Length == 2 && ushort.TryParse(bits[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var company) && TryHex(bits[1], out var data)) blocks.Add(new ManufacturerBlock(company, data));
        }
        var uuids = (o.ServiceUuids ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null).Where(g => g is not null).Select(g => g!.Value).ToList();
        TryHex(o.AdvertisementData ?? "", out var payload);
        Guid? session = Guid.TryParse(o.SessionId, out var sg) ? sg : null;

        return new BleAdvertisement(id, at, address, kind, (short)Math.Round(o.Rssi), Truncate(o.Name, 80), blocks, uuids, payload ?? Array.Empty<byte>())
        {
            Meta = new ObservationMeta("Android", now, session, Truncate(o.LocationLabel, 120)),
        };
    }

    private async Task UpsertSessionsAsync(PairedPhone phone, List<SessionDto>? sessions, CancellationToken ct)
    {
        if (sessions is null || sessions.Count == 0) return;
        await using var db = await _factory.CreateDbContextAsync(ct);
        foreach (var s in sessions.Take(500))
        {
            if (!Guid.TryParse(s.Id, out var id)) continue;
            var row = await db.InspectionSessions.FindAsync(new object[] { id }, ct);
            var started = DateTimeOffset.FromUnixTimeMilliseconds(s.StartedMs).UtcDateTime;
            DateTime? ended = s.EndedMs is { } e ? DateTimeOffset.FromUnixTimeMilliseconds(e).UtcDateTime : null;
            if (row is null) db.InspectionSessions.Add(new InspectionSession { Id = id, ScannerId = phone.Id, LocationLabel = Truncate(s.LocationLabel, 120), StartedAtUtc = started, EndedAtUtc = ended, Notes = Truncate(s.Notes, 1000) });
            else { row.LocationLabel = Truncate(s.LocationLabel, 120); row.EndedAtUtc = ended; row.Notes = Truncate(s.Notes, 1000); }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task StoreTestsAsync(PairedPhone phone, List<TestDto>? tests, CancellationToken ct)
    {
        if (tests is null || tests.Count == 0) return;
        await using var db = await _factory.CreateDbContextAsync(ct);
        foreach (var t in tests.Take(500))
        {
            var id = new Guid(MD5.HashData(Encoding.UTF8.GetBytes($"{phone.Id}:{t.Id}")));      // same phone test, same id: re-uploads never duplicate
            if (await db.ConnectionTests.AnyAsync(x => x.Id == id, ct)) continue;
            var hex = (t.Address ?? "").Replace(":", "").ToUpperInvariant();
            var deviceId = await db.Observations.Where(o => o.Address == hex).Select(o => (Guid?)o.DeviceId).FirstOrDefaultAsync(ct);
            if (deviceId is null) continue;                                                     // a test on a device the PC has never heard of cannot be attributed
            var ok = string.Equals(t.Outcome, "Success", StringComparison.OrdinalIgnoreCase);
            var started = DateTimeOffset.FromUnixTimeMilliseconds(t.StartedMs).UtcDateTime;
            db.ConnectionTests.Add(new ConnectionTest
            {
                Id = id, DeviceId = deviceId.Value, ScannerId = phone.Id, StartedAtUtc = started, CompletedAtUtc = started.AddMilliseconds(t.DurationMs),
                Attempts = 1, SuccessfulAttempts = ok ? 1 : 0, FailedAttempts = ok ? 0 : 1,
                TimeoutCount = t.Outcome?.Contains("timeout", StringComparison.OrdinalIgnoreCase) == true ? 1 : 0,
                ErrorDetails = ok ? null : Truncate($"{t.Outcome}: {t.Detail}", 500), TestConfiguration = "Android connect-and-test (read-only)",
            });
        }
        await db.SaveChangesAsync(ct);
    }

    private Task Event(Severity severity, string type, string message) =>
        _store.RecordEventAsync(new ApplicationEvent { Source = "Sync", Severity = severity, EventType = type, Message = message, TimestampUtc = DateTime.UtcNow });

    private static string? Truncate(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    private static bool TryHex(string s, out byte[]? bytes)
    {
        bytes = null;
        if (s.Length == 0) { bytes = Array.Empty<byte>(); return true; }
        if (s.Length % 2 != 0 || s.Length > 2048) return false;
        try { bytes = Convert.FromHexString(s); return true; } catch { return false; }
    }
}
