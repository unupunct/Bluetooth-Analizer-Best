using BluetoothFleetMonitor.Application.Csv;
using BluetoothFleetMonitor.Domain;
using Microsoft.EntityFrameworkCore;

namespace BluetoothFleetMonitor.Database;

public sealed record DeviceDetails(string? CustomName, string Category, string? Room, string? Workstation, string? Notes);

public sealed record CsvImportResult(int Updated, int Unmatched, IReadOnlyList<string> Errors);

/// <summary>User-driven changes to devices: edits, CSV import/export, and manual merge/split (always audited).</summary>
public sealed class DeviceAdminService
{
    private static readonly string[] ExportHeader =
    {
        "Id", "CustomName", "Category", "Manufacturer", "AdvertisedName", "Address", "Room", "Workstation", "Notes",
        "FirstSeenUtc", "LastSeenUtc", "LastRssi", "Observations",
    };

    private readonly IDbContextFactory<FleetDbContext> _factory;

    public DeviceAdminService(IDbContextFactory<FleetDbContext> factory) => _factory = factory;

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public async Task UpdateDetailsAsync(Guid deviceId, DeviceDetails d, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var dev = await db.Devices.FindAsync(new object[] { deviceId }, ct) ?? throw new InvalidOperationException("Device not found.");
        Apply(dev, d);
        await db.SaveChangesAsync(ct);
    }

    private static void Apply(Device dev, DeviceDetails d)
    {
        dev.CustomName = Clean(d.CustomName);
        dev.Category = Clean(d.Category) ?? "Unknown";
        dev.AssignedRoom = Clean(d.Room);
        dev.AssignedWorkstation = Clean(d.Workstation);
        dev.Notes = Clean(d.Notes);
        dev.UpdatedAt = DateTime.UtcNow;
    }

    // ---- CSV -------------------------------------------------------------------------------

    public async Task<string> ExportCsvAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var devices = await db.Devices.AsNoTracking().Include(d => d.Identifiers).OrderBy(d => d.CustomName).ThenBy(d => d.AdvertisedName).ToListAsync(ct);
        var rows = new List<IReadOnlyList<string?>> { ExportHeader };
        foreach (var d in devices)
        {
            var address = d.Identifiers.FirstOrDefault(i => i.IdentifierType == IdentifierTypes.StableAddress)?.IdentifierValue;
            rows.Add(new string?[]
            {
                d.Id.ToString(), d.CustomName, d.Category, d.Manufacturer, d.AdvertisedName, address, d.AssignedRoom, d.AssignedWorkstation, d.Notes,
                d.FirstSeenUtc?.ToString("o"), d.LastSeenUtc?.ToString("o"), d.LastRssi?.ToString(), d.ObservationCount.ToString(),
            });
        }
        return CsvTable.Write(rows);
    }

    public async Task<CsvImportResult> ImportCsvAsync(string csv, CancellationToken ct = default)
    {
        var rows = CsvTable.Parse(csv);
        var errors = new List<string>();
        if (rows.Count == 0) return new(0, 0, new[] { "The file is empty." });

        var header = rows[0].Select(h => h.Trim().TrimStart('﻿')).ToArray();
        int Col(string name) => Array.FindIndex(header, h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
        var cId = Col("Id"); var cAddr = Col("Address");
        if (cId < 0 && cAddr < 0)
            return new(0, 0, new[] { "The file needs an 'Id' or an 'Address' column to match rows to devices." });
        int cName = Col("CustomName"), cCat = Col("Category"), cRoom = Col("Room"), cWs = Col("Workstation"), cNotes = Col("Notes");

        await using var db = await _factory.CreateDbContextAsync(ct);
        var devices = await db.Devices.Include(d => d.Identifiers).ToListAsync(ct);
        var byId = devices.ToDictionary(d => d.Id);
        var byAddress = new Dictionary<string, Device>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in devices)
            foreach (var i in d.Identifiers.Where(i => i.IdentifierType == IdentifierTypes.StableAddress))
                byAddress[i.IdentifierValue] = d;

        string? Cell(string[] r, int c) => c >= 0 && c < r.Length ? r[c] : null;
        int updated = 0, unmatched = 0;

        for (var n = 1; n < rows.Count; n++)
        {
            var r = rows[n];
            Device? dev = null;
            if (Guid.TryParse(Cell(r, cId), out var gid)) byId.TryGetValue(gid, out dev);
            if (dev is null && Cell(r, cAddr) is { Length: > 0 } a) byAddress.TryGetValue(a.Trim(), out dev);
            if (dev is null) { unmatched++; continue; }

            // Only columns present in the file are touched, so a partial file cannot wipe other fields.
            if (cName >= 0) dev.CustomName = Clean(Cell(r, cName));
            if (cCat >= 0) dev.Category = Clean(Cell(r, cCat)) ?? "Unknown";
            if (cRoom >= 0) dev.AssignedRoom = Clean(Cell(r, cRoom));
            if (cWs >= 0) dev.AssignedWorkstation = Clean(Cell(r, cWs));
            if (cNotes >= 0) dev.Notes = Clean(Cell(r, cNotes));
            dev.UpdatedAt = DateTime.UtcNow;
            updated++;
        }
        await db.SaveChangesAsync(ct);
        return new(updated, unmatched, errors);
    }

    // ---- merge / split ---------------------------------------------------------------------

    /// <summary>Folds <paramref name="sourceId"/> into <paramref name="targetId"/>. Writes an audit event naming every identifier moved, so it can be undone with a split.</summary>
    public async Task MergeAsync(Guid sourceId, Guid targetId, string? reason, CancellationToken ct = default)
    {
        if (sourceId == targetId) throw new ArgumentException("A device cannot be merged into itself.");
        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var source = await db.Devices.Include(d => d.Identifiers).SingleOrDefaultAsync(d => d.Id == sourceId, ct)
            ?? throw new InvalidOperationException("Source device not found.");
        var target = await db.Devices.SingleOrDefaultAsync(d => d.Id == targetId, ct)
            ?? throw new InvalidOperationException("Target device not found.");

        var moved = string.Join("; ", source.Identifiers.Select(i => $"{i.IdentifierType}:{i.IdentifierValue}"));

        await db.Observations.Where(o => o.DeviceId == sourceId).ExecuteUpdateAsync(s => s.SetProperty(o => o.DeviceId, targetId), ct);
        await db.DeviceIdentifiers.Where(i => i.DeviceId == sourceId).ExecuteUpdateAsync(s => s.SetProperty(i => i.DeviceId, targetId), ct);
        await db.PresenceEvents.Where(e => e.DeviceId == sourceId).ExecuteUpdateAsync(s => s.SetProperty(e => e.DeviceId, targetId), ct);
        await db.ConnectionTests.Where(t => t.DeviceId == sourceId).ExecuteUpdateAsync(s => s.SetProperty(t => t.DeviceId, targetId), ct);
        await db.FaultReports.Where(f => f.DeviceId == sourceId).ExecuteUpdateAsync(s => s.SetProperty(f => f.DeviceId, targetId), ct);

        target.CustomName ??= source.CustomName;
        target.AssignedRoom ??= source.AssignedRoom;
        target.AssignedWorkstation ??= source.AssignedWorkstation;
        if (target.Category == "Unknown") target.Category = source.Category;
        if (!string.IsNullOrWhiteSpace(source.Notes))
            target.Notes = string.IsNullOrWhiteSpace(target.Notes) ? source.Notes : target.Notes + "\n" + source.Notes;
        await db.SaveChangesAsync(ct);

        db.ChangeTracker.Clear();
        await db.Devices.Where(d => d.Id == sourceId).ExecuteDeleteAsync(ct);
        await RecomputeStatsAsync(db, targetId, ct);

        db.ApplicationEvents.Add(new ApplicationEvent
        {
            Source = "DeviceAdmin", Severity = Severity.Info, EventType = "DeviceMerged", TimestampUtc = DateTime.UtcNow,
            Message = $"Device {sourceId} ('{source.DisplayName}') merged into {targetId}. Identifiers moved: {moved}. Reason: {reason ?? "not given"}",
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Moves the chosen identifiers (and the observations heard from those addresses) onto a new device. Returns the new device id.</summary>
    public async Task<Guid> SplitAsync(Guid deviceId, IReadOnlyCollection<Guid> identifierIds, string? reason, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var source = await db.Devices.Include(d => d.Identifiers).SingleOrDefaultAsync(d => d.Id == deviceId, ct)
            ?? throw new InvalidOperationException("Device not found.");
        var chosen = source.Identifiers.Where(i => identifierIds.Contains(i.Id)).ToList();
        if (chosen.Count == 0) throw new ArgumentException("Choose at least one identifier of this device.");
        if (chosen.Count == source.Identifiers.Count) throw new ArgumentException("Cannot move every identifier: nothing would stay on the original device.");

        var now = DateTime.UtcNow;
        var created = new Device
        {
            Category = source.Category, Manufacturer = source.Manufacturer, AdvertisedName = source.AdvertisedName,
            CreatedAt = now, UpdatedAt = now,
        };
        db.Devices.Add(created);
        foreach (var i in chosen) i.DeviceId = created.Id;
        await db.SaveChangesAsync(ct);

        var addresses = chosen.Where(i => i.IdentifierType is IdentifierTypes.StableAddress or IdentifierTypes.RotatingAddress)
            .Select(i => i.IdentifierValue).ToList();
        await db.Observations.Where(o => o.DeviceId == deviceId && addresses.Contains(o.Address))
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.DeviceId, created.Id), ct);

        db.ChangeTracker.Clear();
        await RecomputeStatsAsync(db, deviceId, ct);
        await RecomputeStatsAsync(db, created.Id, ct);

        db.ApplicationEvents.Add(new ApplicationEvent
        {
            Source = "DeviceAdmin", Severity = Severity.Info, EventType = "DeviceSplit", TimestampUtc = now,
            Message = $"Device {deviceId} split: new device {created.Id} took identifiers {string.Join("; ", chosen.Select(i => $"{i.IdentifierType}:{i.IdentifierValue}"))}. Reason: {reason ?? "not given"}",
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return created.Id;
    }

    private static Task<int> RecomputeStatsAsync(FleetDbContext db, Guid id, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE Devices SET
              ObservationCount = (SELECT COUNT(*) FROM Observations WHERE DeviceId = {id}),
              RssiSum = COALESCE((SELECT SUM(Rssi) FROM Observations WHERE DeviceId = {id}), 0),
              MinRssi = (SELECT MIN(Rssi) FROM Observations WHERE DeviceId = {id}),
              MaxRssi = (SELECT MAX(Rssi) FROM Observations WHERE DeviceId = {id}),
              FirstSeenUtc = (SELECT MIN(TimestampUtc) FROM Observations WHERE DeviceId = {id}),
              LastSeenUtc = (SELECT MAX(TimestampUtc) FROM Observations WHERE DeviceId = {id}),
              LastRssi = (SELECT Rssi FROM Observations WHERE DeviceId = {id} ORDER BY TimestampUtc DESC LIMIT 1)
            WHERE Id = {id}", ct);
}
