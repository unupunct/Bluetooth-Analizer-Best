using BluetoothFleetMonitor.Application.Abstractions;
using BluetoothFleetMonitor.Application.Identity;
using BluetoothFleetMonitor.Domain;
using Microsoft.EntityFrameworkCore;

namespace BluetoothFleetMonitor.Database;

public sealed class EfFleetStore : IFleetStore
{
    private readonly IDbContextFactory<FleetDbContext> _factory;

    public EfFleetStore(IDbContextFactory<FleetDbContext> factory) => _factory = factory;

    public async Task<IReadOnlyList<KnownDevice>> LoadKnownDevicesAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.DeviceIdentifiers.AsNoTracking()
            .Select(i => new { i.DeviceId, i.IdentifierType, i.IdentifierValue })
            .ToListAsync(ct);
        var deviceIds = await db.Devices.AsNoTracking().Select(d => d.Id).ToListAsync(ct);

        var byDevice = rows.GroupBy(r => r.DeviceId)
            .ToDictionary(g => g.Key, g => g.Select(r => new KnownIdentifier(r.IdentifierType, r.IdentifierValue)).ToList());
        return deviceIds
            .Select(id => new KnownDevice(id, byDevice.TryGetValue(id, out var l) ? l : new List<KnownIdentifier>()))
            .ToList();
    }

    public async Task<int> SaveBatchAsync(Guid scannerId, IReadOnlyList<ProcessedObservation> batch, CancellationToken ct = default)
    {
        if (batch.Count == 0) return 0;
        await using var db = await _factory.CreateDbContextAsync(ct);

        // Idempotency: skip observation ids already stored, and duplicates inside this batch.
        var ids = batch.Select(b => b.Ad.ObservationId).Distinct().ToList();
        var seen = (await db.Observations.Where(o => ids.Contains(o.Id)).Select(o => o.Id).ToListAsync(ct)).ToHashSet();
        var fresh = batch.Where(b => seen.Add(b.Ad.ObservationId)).ToList();
        if (fresh.Count == 0) return 0;

        var deviceIds = fresh.Select(f => f.DeviceId).Distinct().ToList();
        var devices = await db.Devices.Where(d => deviceIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct);

        foreach (var p in fresh)
        {
            var ad = p.Ad;
            if (!devices.TryGetValue(p.DeviceId, out var dev))
            {
                dev = new Device
                {
                    Id = p.DeviceId,
                    CreatedAt = p.ProcessedAtUtc,
                    UpdatedAt = p.ProcessedAtUtc,
                    Manufacturer = ad.Manufacturer.Count > 0 ? CompanyIds.Describe(ad.Manufacturer[0].CompanyId) : null,
                };
                db.Devices.Add(dev);
                devices[dev.Id] = dev;
            }

            foreach (var n in p.NewIdentifiers)
                db.DeviceIdentifiers.Add(new DeviceIdentifier
                {
                    DeviceId = dev.Id, IdentifierType = n.Type, IdentifierValue = n.Value, Confidence = n.Confidence,
                    FirstSeen = ad.ObservedUtc, LastSeen = ad.ObservedUtc,
                });

            // Statistics. Only the newest observation may set "current" state, so a late upload of
            // old measurements never makes a device look freshly seen or changes its last RSSI.
            if (dev.FirstSeenUtc is null || ad.ObservedUtc < dev.FirstSeenUtc) dev.FirstSeenUtc = ad.ObservedUtc;
            if (dev.LastSeenUtc is null || ad.ObservedUtc >= dev.LastSeenUtc)
            {
                dev.LastSeenUtc = ad.ObservedUtc;
                dev.LastRssi = ad.Rssi;
                if (!string.IsNullOrWhiteSpace(ad.Name)) dev.AdvertisedName = ad.Name;
            }
            dev.MinRssi = dev.MinRssi is null ? ad.Rssi : Math.Min(dev.MinRssi.Value, ad.Rssi);
            dev.MaxRssi = dev.MaxRssi is null ? ad.Rssi : Math.Max(dev.MaxRssi.Value, ad.Rssi);
            dev.RssiSum += ad.Rssi;
            dev.ObservationCount++;
            dev.UpdatedAt = p.ProcessedAtUtc;

            db.Observations.Add(new Observation
            {
                Id = ad.ObservationId,
                DeviceId = dev.Id,
                ScannerId = scannerId,
                TimestampUtc = ad.ObservedUtc,
                ProcessedAtUtc = p.ProcessedAtUtc,
                Rssi = ad.Rssi,
                Address = ad.AddressText,
                AddressKind = ad.AddressKind,
                ManufacturerData = ad.Manufacturer.Count == 0 ? null
                    : string.Join(';', ad.Manufacturer.Select(m => $"{m.CompanyId:X4}:{Convert.ToHexString(m.Data)}")),
                ServiceUuids = ad.ServiceUuids.Count == 0 ? null : string.Join(';', ad.ServiceUuids),
                AdvertisementData = ad.Payload.Length == 0 ? null : Convert.ToHexString(ad.Payload),
                Source = ad.Meta?.Source ?? "Windows",
                UploadedAtUtc = ad.Meta?.UploadedAtUtc,
                InspectionSessionId = ad.Meta?.SessionId,
                LocationLabel = ad.Meta?.LocationLabel,
            });
        }

        await db.SaveChangesAsync(ct); // one transaction
        return fresh.Count;
    }

    public async Task RecordEventAsync(ApplicationEvent evt, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.ApplicationEvents.Add(evt);
        await db.SaveChangesAsync(ct);
    }

    public async Task EnsureScannerAsync(Scanner scanner, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var existing = await db.Scanners.FindAsync(new object[] { scanner.Id }, ct);
        if (existing is null) db.Scanners.Add(scanner);
        else
        {
            existing.Name = scanner.Name;
            existing.HardwareDescription = scanner.HardwareDescription;
            existing.Status = scanner.Status;
            existing.LastSeen = scanner.LastSeen ?? DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }
}
