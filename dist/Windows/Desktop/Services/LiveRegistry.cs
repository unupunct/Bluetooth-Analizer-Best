using System.Collections.ObjectModel;
using BluetoothFleetMonitor.Application.Abstractions;
using BluetoothFleetMonitor.Application.Scanning;
using BluetoothFleetMonitor.Database;
using BluetoothFleetMonitor.Domain;
using Microsoft.EntityFrameworkCore;

namespace BluetoothFleetMonitor.Desktop.Services;

/// <summary>
/// In-memory mirror of the devices for the UI. Everything here runs on the UI thread. The database is only
/// read once at start-up; after that the pipeline pushes whole batches so the UI never queries per advertisement.
/// </summary>
public sealed class LiveRegistry
{
    private readonly Dictionary<Guid, LiveDevice> _index = new();
    public ObservableCollection<LiveDevice> Devices { get; } = new();

    public int Registered => Devices.Count;
    public int Detected { get; private set; }
    public int Missing { get; private set; }
    public int Uncertain { get; private set; }
    public DateTime? LastUpdateUtc { get; private set; }

    public LiveDevice? Find(Guid id) => _index.GetValueOrDefault(id);

    public async Task LoadAsync(IDbContextFactory<FleetDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.Devices.AsNoTracking().OrderByDescending(d => d.LastSeenUtc).ToListAsync();
        foreach (var d in rows)
        {
            var live = GetOrCreate(d.Id);
            Fill(live, d);
        }
    }

    public void Reload(Device d) => Fill(GetOrCreate(d.Id), d);

    /// <summary>Re-reads one device from the database (after an edit or merge).</summary>
    public async Task RefreshDeviceAsync(IDbContextFactory<FleetDbContext> factory, Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        var d = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
        if (d is null) Remove(id); else Reload(d);
    }

    public void Remove(Guid id)
    {
        if (_index.Remove(id, out var live)) Devices.Remove(live);
    }

    private LiveDevice GetOrCreate(Guid id)
    {
        if (_index.TryGetValue(id, out var live)) return live;
        live = new LiveDevice(id);
        _index[id] = live;
        Devices.Add(live);
        return live;
    }

    private static void Fill(LiveDevice live, Device d)
    {
        live.CustomName = d.CustomName;
        live.AdvertisedName = d.AdvertisedName;
        live.Category = d.Category;
        live.Manufacturer = d.Manufacturer;
        live.Room = d.AssignedRoom;
        live.Workstation = d.AssignedWorkstation;
        live.Notes = d.Notes;
        live.Rssi = d.LastRssi;
        live.RssiQuality = d.LastRssi is { } r ? LiveDevice.Describe(r) : "";
        live.LastSeenUtc = d.LastSeenUtc;
        live.FirstSeenUtc = d.FirstSeenUtc;
        live.Observations = d.ObservationCount;
        live.RecomputeName();
    }

    public void ApplyBatch(IReadOnlyList<ProcessedObservation> batch)
    {
        foreach (var o in batch)
        {
            var live = GetOrCreate(o.DeviceId);
            var ad = o.Ad;
            live.Observations++;
            if (live.FirstSeenUtc is null || ad.ObservedUtc < live.FirstSeenUtc) live.FirstSeenUtc = ad.ObservedUtc;
            // Only the newest sighting may set the "current" values.
            if (live.LastSeenUtc is null || ad.ObservedUtc >= live.LastSeenUtc)
            {
                live.LastSeenUtc = ad.ObservedUtc;
                live.Rssi = ad.Rssi;
                if (!string.IsNullOrWhiteSpace(ad.Name) && ad.Name != live.AdvertisedName)
                {
                    live.AdvertisedName = ad.Name;
                    live.RecomputeName();
                }
            }
            live.AddSample(ad.Rssi);
            if (live.Manufacturer is null && ad.Manufacturer.Count > 0)
                live.Manufacturer = Application.Identity.CompanyIds.Describe(ad.Manufacturer[0].CompanyId);
            if (live.Name.Length == 0) live.RecomputeName();
        }
        LastUpdateUtc = DateTime.UtcNow;
    }

    public void RefreshStatuses(DateTime nowUtc, TimeSpan window, ScannerState scanner, IReadOnlyList<CoverageGap> gaps)
    {
        int detected = 0, missing = 0, uncertain = 0;
        foreach (var d in Devices)
        {
            var p = PresenceStatus.Evaluate(d.LastSeenUtc, nowUtc, window, scanner, gaps);
            var text = p switch { DevicePresence.Detected => "Detected", DevicePresence.Missing => "Missing", _ => "Uncertain" };
            if (d.Status != text) d.Status = text;
            d.LastSeenText = Relative(d.LastSeenUtc, nowUtc);
            switch (p) { case DevicePresence.Detected: detected++; break; case DevicePresence.Missing: missing++; break; default: uncertain++; break; }
        }
        Detected = detected; Missing = missing; Uncertain = uncertain;
    }

    public static string Relative(DateTime? t, DateTime now)
    {
        if (t is null) return "never";
        var s = (now - t.Value).TotalSeconds;
        if (s < 0) s = 0;
        return s < 60 ? $"{(int)s} s ago"
             : s < 3600 ? $"{(int)(s / 60)} min ago"
             : s < 86400 ? $"{(int)(s / 3600)} h ago"
             : $"{(int)(s / 86400)} d ago";
    }
}
