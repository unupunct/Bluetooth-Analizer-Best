using BluetoothFleetMonitor.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BluetoothFleetMonitor.Database;

public class FleetDbContext : DbContext
{
    public FleetDbContext(DbContextOptions<FleetDbContext> options) : base(options) { }

    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceIdentifier> DeviceIdentifiers => Set<DeviceIdentifier>();
    public DbSet<Scanner> Scanners => Set<Scanner>();
    public DbSet<Observation> Observations => Set<Observation>();
    public DbSet<PresenceEvent> PresenceEvents => Set<PresenceEvent>();
    public DbSet<ConnectionTest> ConnectionTests => Set<ConnectionTest>();
    public DbSet<InspectionSession> InspectionSessions => Set<InspectionSession>();
    public DbSet<FaultReport> FaultReports => Set<FaultReport>();
    public DbSet<UsbEvent> UsbEvents => Set<UsbEvent>();
    public DbSet<ApplicationEvent> ApplicationEvents => Set<ApplicationEvent>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        // Every timestamp is UTC. Reading back must never yield Kind=Unspecified/Local.
        b.Properties<DateTime>().HaveConversion<UtcConverter>();
        b.Properties<DateTime?>().HaveConversion<NullableUtcConverter>();
    }

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<Device>(e =>
        {
            e.HasKey(x => x.Id);
            e.Ignore(x => x.AverageRssi);
            e.Ignore(x => x.DisplayName);
            e.HasIndex(x => x.Category);
            e.HasIndex(x => x.AssignedRoom);
            e.HasIndex(x => x.LastSeenUtc);
            e.HasIndex(x => x.IdentityFingerprint);
            e.HasMany(x => x.Identifiers).WithOne().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        });

        m.Entity<DeviceIdentifier>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.IdentifierType, x.IdentifierValue });
            e.HasIndex(x => x.DeviceId);
        });

        m.Entity<Scanner>(e => { e.HasKey(x => x.Id); });

        m.Entity<Observation>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.DeviceId);
            e.HasIndex(x => x.ScannerId);
            e.HasIndex(x => x.TimestampUtc);
            e.HasIndex(x => new { x.DeviceId, x.TimestampUtc });
            e.HasIndex(x => x.LocationLabel);
        });

        m.Entity<PresenceEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.DeviceId);
            e.HasIndex(x => x.ScannerId);
            e.HasIndex(x => x.TimestampUtc);
            e.HasIndex(x => x.EventType);
        });

        m.Entity<ConnectionTest>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.DeviceId);
            e.HasIndex(x => x.StartedAtUtc);
        });

        m.Entity<InspectionSession>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ScannerId);
            e.HasIndex(x => x.LocationLabel);
        });

        m.Entity<FaultReport>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.DeviceId);
            e.HasIndex(x => x.CreatedAtUtc);
        });

        m.Entity<UsbEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TimestampUtc);
            e.HasIndex(x => x.EventType);
        });

        m.Entity<ApplicationEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TimestampUtc);
            e.HasIndex(x => x.EventType);
        });
    }

    private sealed class UtcConverter : ValueConverter<DateTime, DateTime>
    {
        public UtcConverter() : base(v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
                                     v => DateTime.SpecifyKind(v, DateTimeKind.Utc)) { }
    }

    private sealed class NullableUtcConverter : ValueConverter<DateTime?, DateTime?>
    {
        public NullableUtcConverter() : base(v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime()) : v,
                                             v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v) { }
    }
}
