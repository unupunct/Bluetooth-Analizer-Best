using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BluetoothFleetMonitor.Desktop.Services;
using BluetoothFleetMonitor.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QRCoder;

namespace BluetoothFleetMonitor.Desktop.ViewModels;

public sealed class PhoneRow
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string PairedText { get; init; } = "";
    public string LastUploadText { get; init; } = "";
    public string ReceivedText { get; init; } = "";
    public string ClockText { get; init; } = "";
    public Brush ClockBrush { get; init; } = Brushes.Gray;
    public bool Revoked { get; init; }
    public string StatusText => Revoked ? "Access revoked" : "Paired";
}

public sealed partial class MobileDevicesViewModel : ObservableObject
{
    public const string FirewallRuleName = "Bluetooth Fleet Monitor sync";

    private readonly SyncServer _server;
    private readonly PairedPhoneStore _phones;
    private readonly PairingService _pairing;
    private readonly CertificateService _cert;
    private readonly SettingsStore _settings;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _timer;
    private DateTime _inviteExpires;
    private bool _loading;
    private bool _demo;

    public ObservableCollection<PhoneRow> Phones { get; } = new();
    public ObservableCollection<string> Addresses { get; } = new();

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _serverStatus = "";
    [ObservableProperty] private bool _serverOk;
    [ObservableProperty] private string _fingerprintText = "";
    [ObservableProperty] private string? _selectedAddress;
    [ObservableProperty] private int _port = SyncLimits.DefaultPort;
    [ObservableProperty] private string _firewallText = "";
    [ObservableProperty] private bool _firewallOk;
    [ObservableProperty] private bool _hasInvite;
    [ObservableProperty] private string _inviteCode = "";
    [ObservableProperty] private string _inviteLink = "";
    [ObservableProperty] private string _inviteCountdown = "";
    [ObservableProperty] private ImageSource? _qr;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _hasPhones;

    public MobileDevicesViewModel(SyncServer server, PairedPhoneStore phones, PairingService pairing, CertificateService cert, SettingsStore settings, Dispatcher ui)
    {
        _server = server; _phones = phones; _pairing = pairing; _cert = cert; _settings = settings; _ui = ui;
        _loading = true;
        Enabled = settings.Current.SyncEnabled; Port = settings.Current.SyncPort;
        _loading = false;
        FingerprintText = string.Join(' ', Enumerable.Range(0, 8).Select(i => _cert.Fingerprint.Substring(i * 4, 4)));
        foreach (var a in LocalNetwork.Addresses()) Addresses.Add(a);
        SelectedAddress = Addresses.FirstOrDefault();
        _phones.Changed += () => _ui.BeginInvoke(RefreshPhones);
        _server.StateChanged += () => _ui.BeginInvoke(RefreshServer);
        _timer = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        RefreshPhones(); RefreshServer(); _ = RefreshFirewallAsync();
    }

    /// <summary>Called once at start-up: the server only runs if the user turned sync on earlier.</summary>
    public async Task StartIfEnabledAsync() { if (_settings.Current.SyncEnabled) await _server.StartAsync(_settings.Current.SyncPort); RefreshServer(); }

    partial void OnEnabledChanged(bool value)
    {
        if (_loading) return;
        _settings.Current.SyncEnabled = value; _settings.Save();
        _ = value ? _server.StartAsync(Port) : _server.StopAsync();
        if (!value) { HasInvite = false; Qr = null; }
    }

    private void RefreshServer()
    {
        ServerOk = _server.IsRunning;
        ServerStatus = _server.IsRunning ? $"Listening on port {_server.Port} (HTTPS, local network only)"
            : _server.LastError is { } e ? "Could not start: " + e + (e.Contains("access", StringComparison.OrdinalIgnoreCase) || e.Contains("use", StringComparison.OrdinalIgnoreCase) ? " (another program may be using this port)" : "")
            : Enabled ? "Starting..." : "Off. Nothing listens on the network until you turn this on.";
    }

    private void RefreshPhones()
    {
        Phones.Clear();
        foreach (var p in _phones.All().OrderByDescending(p => p.PairedAtUtc))
        {
            var skew = p.LastClockSkewSeconds;
            Phones.Add(new PhoneRow
            {
                Id = p.Id, Name = p.Name, Revoked = p.Revoked,
                PairedText = "Paired " + p.PairedAtUtc.ToLocalTime().ToString("dd MMM yyyy HH:mm"),
                LastUploadText = p.LastUploadUtc is { } t ? "Last upload " + t.ToLocalTime().ToString("dd MMM HH:mm:ss") : "No upload yet",
                ReceivedText = $"{p.ObservationsReceived:N0} observations received",
                ClockText = skew is null ? "Clock: not checked yet" : Math.Abs(skew.Value) <= 2 ? "Clock in step with this PC" : $"Phone clock is {Math.Abs(skew.Value):0.#} s {(skew > 0 ? "behind" : "ahead of")} this PC (times are kept as the phone recorded them)",
                ClockBrush = skew is null || Math.Abs(skew.Value) <= 2 ? (Brush)System.Windows.Application.Current.Resources["Muted"] : (Brush)System.Windows.Application.Current.Resources["Warn"],
            });
        }
        HasPhones = Phones.Count > 0;
    }

    private void Tick()
    {
        if (!HasInvite) return;
        var left = _inviteExpires - DateTime.UtcNow;
        if (left <= TimeSpan.Zero || !_pairing.HasOpenInvite)
        {
            HasInvite = false; Qr = null; InviteCode = ""; InviteLink = "";
            Message = left <= TimeSpan.Zero ? "The pairing code expired. Create a new one." : "Phone paired.";
        }
        else InviteCountdown = $"valid for {left.Minutes}:{left.Seconds:00}";
    }

    [RelayCommand]
    private async Task CreateInvite()
    {
        if (!_server.IsRunning) { Enabled = true; await Task.Delay(500); RefreshServer(); }
        if (!_server.IsRunning) { Message = "The sync server is not running, so a phone cannot pair. See the status above."; return; }
        if (string.IsNullOrEmpty(SelectedAddress)) { Message = "No network address found. Connect this PC to the same Wi-Fi or LAN as the phone."; return; }
        var (code, expires) = _pairing.CreateInvite();
        _inviteExpires = expires; InviteCode = code;
        InviteLink = PairingLink.Build(SelectedAddress, _server.Port, _cert.Fingerprint, code, _server.ServerName);
        Qr = MakeQr(InviteLink);
        HasInvite = true; Message = ""; Tick();
    }

    private static ImageSource MakeQr(string text)
    {
        using var gen = new QRCodeGenerator();
        var png = new PngByteQRCode(gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M)).GetGraphic(10);
        var img = new BitmapImage();
        using (var ms = new MemoryStream(png)) { img.BeginInit(); img.CacheOption = BitmapCacheOption.OnLoad; img.StreamSource = ms; img.EndInit(); }
        img.Freeze();
        return img;
    }

    /// <summary>For the labelled demo screenshots: fictional phones and a pairing code that is not valid anywhere.</summary>
    public void LoadDemo()
    {
        _timer.Stop(); _demo = true;
        _loading = true; Enabled = true; _loading = false;
        ServerOk = true; ServerStatus = $"Listening on port {SyncLimits.DefaultPort} (HTTPS, local network only)";
        FirewallOk = true; FirewallText = "Windows Firewall: the rule for phone sync exists (Private networks, local subnet).";
        Addresses.Clear(); Addresses.Add("192.168.1.20"); SelectedAddress = "192.168.1.20";
        var fp = string.Concat(Enumerable.Repeat("D0DE", 16));
        FingerprintText = string.Join(' ', Enumerable.Range(0, 8).Select(i => fp.Substring(i * 4, 4)));
        InviteCode = "48201936"; InviteCountdown = "valid for 4:21";
        InviteLink = PairingLink.Build("192.168.1.20", SyncLimits.DefaultPort, fp, InviteCode, "DEMO-PC");
        Qr = MakeQr(InviteLink); HasInvite = true;
        var muted = (Brush)System.Windows.Application.Current.Resources["Muted"]; var warn = (Brush)System.Windows.Application.Current.Resources["Warn"];
        Phones.Clear();
        Phones.Add(new PhoneRow { Name = "Demo phone A", PairedText = "Paired 20 Nov 2026 09:14", LastUploadText = "Last upload 23 Nov 08:49:48", ReceivedText = "13,567 observations received", ClockText = "Clock in step with this PC", ClockBrush = muted });
        Phones.Add(new PhoneRow { Name = "Demo phone B", PairedText = "Paired 21 Nov 2026 16:40", LastUploadText = "Last upload 23 Nov 07:02:11", ReceivedText = "4,210 observations received", ClockText = "Phone clock is 6.4 s behind this PC (times are kept as the phone recorded them)", ClockBrush = warn });
        HasPhones = true;
        Message = "DEMO DATA: fictional phones and a pairing code that is not valid. Not real data.";
    }

    [RelayCommand] private void CopyLink() { if (InviteLink.Length > 0) { Clipboard.SetText(InviteLink); Message = "Pairing link copied."; } }

    [RelayCommand]
    private void Revoke(PhoneRow? row)
    {
        if (row is null) return;
        if (MessageBox.Show($"Revoke \"{row.Name}\"? It will no longer be able to upload. Data it already sent stays in the database.", "Revoke phone", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _phones.Revoke(row.Id);
    }

    [RelayCommand]
    private async Task AllowFirewall()
    {
        var exe = Environment.ProcessPath ?? "";
        var args = $"advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow protocol=TCP localport={_server.Port} profile=private remoteip=localsubnet program=\"{exe}\" enable=yes";
        if (MessageBox.Show($"Windows will ask for administrator permission to add one inbound firewall rule:\n\n  TCP port {_server.Port}, Private networks only, local subnet only, for this program.\n\nNothing else is changed. Continue?",
                "Allow through Windows Firewall", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("netsh", args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            if (p is not null) await p.WaitForExitAsync();
            Message = p?.ExitCode == 0 ? "Firewall rule added." : "The firewall rule was not added (exit code " + p?.ExitCode + ").";
        }
        catch (Exception ex) { Message = "The firewall rule was not added: " + ex.Message; }
        await RefreshFirewallAsync();
    }

    [RelayCommand]
    private async Task RefreshFirewallAsync()
    {
        if (_demo) return;
        var text = await Task.Run(() =>
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{FirewallRuleName}\"") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
                var o = p!.StandardOutput.ReadToEnd(); p.WaitForExit();
                return o.Contains("Enabled:", StringComparison.OrdinalIgnoreCase) && !o.Contains("No rules match", StringComparison.OrdinalIgnoreCase) ? "ok" : "none";
            }
            catch { return "unknown"; }
        });
        if (_demo) return;   // the demo page keeps its fictional status
        FirewallOk = text == "ok";
        FirewallText = text switch
        {
            "ok" => "Windows Firewall: the rule for phone sync exists (Private networks, local subnet).",
            "none" => "Windows Firewall: no rule yet. Without it the phone usually cannot connect. Press \"Allow in Windows Firewall\" (asks for administrator permission).",
            _ => "Windows Firewall: could not be checked.",
        };
    }
}
