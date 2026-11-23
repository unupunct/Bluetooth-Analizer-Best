using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using BluetoothFleetMonitor.Application.Identity;
using BluetoothFleetMonitor.Application.Pipeline;
using BluetoothFleetMonitor.Application.Scanning;
using BluetoothFleetMonitor.Database;
using BluetoothFleetMonitor.Domain;
using BluetoothFleetMonitor.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BluetoothFleetMonitor.Tests;

public sealed class SyncTests : IAsyncLifetime
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bfm-sync-" + Guid.NewGuid().ToString("N"));
    private sealed class Factory(SqliteConnection c) : IDbContextFactory<FleetDbContext>
    {
        public FleetDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FleetDbContext>().UseSqlite(c).Options);
    }

    private Factory _factory = null!;
    private EfFleetStore _store = null!;
    private ObservationPipeline _pipeline = null!;
    private PairedPhoneStore _phones = null!;
    private PairingService _pairing = null!;
    private CertificateService _cert = null!;
    private SyncServer _server = null!;
    private HttpClient _http = null!;
    private readonly Guid _pcScanner = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _conn.Open();
        _factory = new Factory(_conn);
        await using (var db = _factory.CreateDbContext()) await db.Database.MigrateAsync();
        _store = new EfFleetStore(_factory);
        await _store.EnsureScannerAsync(new Scanner { Id = _pcScanner, Name = "pc", CreatedAt = DateTime.UtcNow });
        _pipeline = new ObservationPipeline(_store, _pcScanner);
        _phones = new PairedPhoneStore(Path.Combine(_dir, "phones.json"));
        _pairing = new PairingService();
        _cert = new CertificateService(_dir);
        _server = new SyncServer(_pipeline, _store, _factory, _phones, _pairing, _cert, "test");
        await _server.StartAsync(0, loopbackOnly: true);
        Assert.True(_server.IsRunning, _server.LastError);
        _http = Client(_cert.Fingerprint);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose(); await _server.DisposeAsync(); _conn.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private HttpClient Client(string pinnedFingerprint)
    {
        var handler = new HttpClientHandler
        {
            // The same rule the phone uses: trust exactly one certificate, identified by its SHA-256 fingerprint.
            ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null && Convert.ToHexString(SHA256.HashData(cert.RawData)) == pinnedFingerprint,
        };
        return new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{_server.Port}") };
    }

    private async Task<(string Token, Guid ScannerId)> PairAsync(string phoneId = "phone-1", string name = "Test phone")
    {
        var (code, _) = _pairing.CreateInvite();
        var r = await _http.PostAsJsonAsync("/api/v1/pair", new PairRequest(code, name, phoneId));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = (await r.Content.ReadFromJsonAsync<PairResponse>())!;
        return (body.Token, Guid.Parse(body.ScannerId));
    }

    private Task<HttpResponseMessage> Upload(string? token, UploadRequest body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/upload") { Content = JsonContent.Create(body) };
        if (token is not null) req.Headers.Authorization = new("Bearer", token);
        return _http.SendAsync(req);
    }

    private static long Ms(DateTime t) => new DateTimeOffset(t, TimeSpan.Zero).ToUnixTimeMilliseconds();
    private static ObservationDto Obs(string address = "AA:BB:CC:DD:EE:01", double rssi = -61, DateTime? at = null, string? id = null, string? manufacturer = "0059:0102030405", string? session = null)
        => new(id ?? Guid.NewGuid().ToString(), address, "Unknown", Ms(at ?? DateTime.UtcNow.AddMinutes(-2)), rssi, "Lamp", manufacturer, null, null, session, "Studio A");
    private static UploadRequest Req(params ObservationDto[] o) => new(Ms(DateTime.UtcNow), null, o.ToList(), null);

    [Fact]
    public async Task Hello_works_for_a_client_that_pins_the_right_certificate_and_fails_for_any_other()
    {
        var ok = await _http.GetAsync("/api/v1/hello");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using var wrong = Client(new string('0', 64));
        await Assert.ThrowsAsync<HttpRequestException>(() => wrong.GetAsync("/api/v1/hello"));
    }

    [Fact]
    public async Task Pairing_needs_the_one_time_code_and_the_code_works_once()
    {
        var (code, _) = _pairing.CreateInvite();
        var bad = await _http.PostAsJsonAsync("/api/v1/pair", new PairRequest("00000000" == code ? "11111111" : "00000000", "p", "ph"));
        Assert.Equal(HttpStatusCode.Forbidden, bad.StatusCode);
        var good = await _http.PostAsJsonAsync("/api/v1/pair", new PairRequest(code, "p", "ph"));
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        var again = await _http.PostAsJsonAsync("/api/v1/pair", new PairRequest(code, "p", "ph2"));
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
    }

    [Fact]
    public async Task Upload_without_a_valid_token_or_after_revocation_is_refused()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Upload(null, Req(Obs()))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Upload("nonsense", Req(Obs()))).StatusCode);
        var (token, id) = await PairAsync();
        Assert.Equal(HttpStatusCode.OK, (await Upload(token, Req(Obs()))).StatusCode);
        _phones.Revoke(id);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Upload(token, Req(Obs()))).StatusCode);
    }

    [Fact]
    public async Task Uploaded_observations_keep_their_original_time_and_are_attributed_to_the_phone()
    {
        var (token, scanner) = await PairAsync();
        var at = DateTime.UtcNow.AddHours(-3);
        var r = await Upload(token, Req(Obs(at: at, session: Guid.NewGuid().ToString())));
        var body = (await r.Content.ReadFromJsonAsync<UploadResponse>())!;
        Assert.Equal(1, body.Stored);

        await using var db = _factory.CreateDbContext();
        var o = await db.Observations.SingleAsync();
        Assert.Equal("Android", o.Source);
        Assert.Equal(scanner, o.ScannerId);
        Assert.InRange((o.TimestampUtc - at).TotalMilliseconds, -2, 2);       // original receive time untouched
        Assert.NotNull(o.UploadedAtUtc);                                       // upload time recorded separately
        Assert.True(o.ProcessedAtUtc >= o.UploadedAtUtc!.Value.AddSeconds(-1));
        Assert.Equal("AABBCCDDEE01", o.Address);
        Assert.Equal("Studio A", o.LocationLabel);
    }

    [Fact]
    public async Task Resending_the_same_observations_never_duplicates_them()
    {
        var (token, _) = await PairAsync();
        var batch = Req(Obs(id: Guid.NewGuid().ToString()), Obs(address: "AA:BB:CC:DD:EE:02"));
        var first = (await (await Upload(token, batch)).Content.ReadFromJsonAsync<UploadResponse>())!;
        var second = (await (await Upload(token, batch)).Content.ReadFromJsonAsync<UploadResponse>())!;
        Assert.Equal(2, first.Stored);
        Assert.Equal(0, second.Stored);
        Assert.Equal(2, second.Duplicates);
        await using var db = _factory.CreateDbContext();
        Assert.Equal(2, await db.Observations.CountAsync());
    }

    [Fact]
    public async Task Garbage_and_future_dated_observations_are_rejected_not_stored()
    {
        var (token, _) = await PairAsync();
        var good = Obs();
        var r = (await (await Upload(token, Req(good, Obs(address: "not-an-address"), Obs(at: DateTime.UtcNow.AddDays(2)), Obs(id: "not-a-guid"), Obs(rssi: 400)))).Content.ReadFromJsonAsync<UploadResponse>())!;
        Assert.Equal(1, r.Stored);
        Assert.Equal(4, r.Rejected);
    }

    [Fact]
    public async Task A_phone_hearing_an_address_the_PC_already_knows_joins_the_same_device()
    {
        // the PC's own radio heard this public address first
        var pcAd = new BleAdvertisement(Guid.NewGuid(), DateTime.UtcNow.AddHours(-1), 0xAABBCCDDEE01UL, AddressKind.Public, -50, "Lamp",
            new[] { new ManufacturerBlock(0x0059, new byte[] { 1, 2, 3, 4, 5 }) }, Array.Empty<Guid>(), Array.Empty<byte>());
        await _pipeline.IngestAsync(_pcScanner, new[] { pcAd });

        var (token, _) = await PairAsync();
        await Upload(token, Req(Obs(address: "AA:BB:CC:DD:EE:01")));

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.Devices.CountAsync());                        // one physical device, heard by two scanners
        Assert.Equal(2, await db.Observations.Select(o => o.ScannerId).Distinct().CountAsync());
    }

    [Fact]
    public async Task Sessions_and_connection_tests_are_stored_once()
    {
        var (token, _) = await PairAsync();
        var sid = Guid.NewGuid();
        var req = new UploadRequest(Ms(DateTime.UtcNow),
            new() { new SessionDto(sid.ToString(), "Studio A", Ms(DateTime.UtcNow.AddHours(-1)), Ms(DateTime.UtcNow), "walk") },
            new() { Obs(session: sid.ToString()) },
            new() { new TestDto("7", "AA:BB:CC:DD:EE:01", Ms(DateTime.UtcNow.AddMinutes(-5)), 4200, "Success", "ok"), new TestDto("8", "AA:BB:CC:DD:EE:01", Ms(DateTime.UtcNow.AddMinutes(-4)), 10000, "Timeout", "no answer") });
        await Upload(token, req); await Upload(token, req);
        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.InspectionSessions.CountAsync());
        Assert.Equal("Studio A", (await db.InspectionSessions.SingleAsync()).LocationLabel);
        var tests = await db.ConnectionTests.OrderBy(t => t.StartedAtUtc).ToListAsync();
        Assert.Equal(2, tests.Count);
        Assert.Equal(1, tests[0].SuccessfulAttempts);
        Assert.Equal(1, tests[1].FailedAttempts);
        Assert.Equal(1, tests[1].TimeoutCount);
    }

    [Fact]
    public async Task The_clock_difference_is_reported_but_never_rewrites_times()
    {
        var (token, _) = await PairAsync();
        var behind = new UploadRequest(Ms(DateTime.UtcNow.AddSeconds(-30)), null, new() { Obs() }, null);
        var r = (await (await Upload(token, behind)).Content.ReadFromJsonAsync<UploadResponse>())!;
        Assert.InRange(r.ClockSkewSeconds, 28, 33);
        Assert.InRange(_phones.All().Single().LastClockSkewSeconds!.Value, 28, 33);
    }

    [Fact]
    public void Pairing_codes_expire_and_burn_after_repeated_wrong_guesses()
    {
        var now = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
        var p = new PairingService(() => now);
        var (code, _) = p.CreateInvite();
        now = now.AddMinutes(6);
        Assert.False(p.TryConsume(code));                                      // expired

        now = now.AddMinutes(1);
        var (code2, _) = p.CreateInvite();
        for (var i = 0; i < PairingService.MaxFailures; i++) Assert.False(p.TryConsume("xxxxxxxx"));
        Assert.False(p.TryConsume(code2));                                     // the invite is burnt: the right code no longer works
    }

    [Fact]
    public void The_pairing_link_carries_the_address_port_fingerprint_and_code()
    {
        var link = PairingLink.Build("192.168.1.20", 47821, "AB12", "12345678", "STUDIO PC");
        Assert.Equal("bfm://pair?h=192.168.1.20&p=47821&f=AB12&c=12345678&n=STUDIO%20PC", link);
    }

    [Fact]
    public void Tokens_are_stored_only_as_hashes()
    {
        var (phone, token) = _phones.Add("x", "id");
        Assert.DoesNotContain(token, File.ReadAllText(Path.Combine(_dir, "phones.json")));
        Assert.Equal(phone.Id, _phones.FindByToken(token)!.Id);
        Assert.Null(_phones.FindByToken(token + "0"));
    }
}
