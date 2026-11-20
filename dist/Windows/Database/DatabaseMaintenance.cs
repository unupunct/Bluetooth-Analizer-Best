using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BluetoothFleetMonitor.Database;

public static class DatabaseMaintenance
{
    /// <summary>Runs PRAGMA integrity_check. True only when SQLite answers "ok".</summary>
    public static async Task<bool> CheckIntegrityAsync(DbContext db, CancellationToken ct = default)
    {
        var conn = db.Database.GetDbConnection();
        var wasClosed = conn.State != System.Data.ConnectionState.Open;
        if (wasClosed) await conn.OpenAsync(ct);
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check;";
            var result = (string?)await cmd.ExecuteScalarAsync(ct);
            return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
        }
        finally { if (wasClosed) await conn.CloseAsync(); }
    }

    /// <summary>Copies the live database with the SQLite online-backup API (safe while the app is writing). Returns the backup path.</summary>
    public static async Task<string> BackupAsync(string dbPath, string? backupDirectory = null, CancellationToken ct = default)
    {
        if (!File.Exists(dbPath)) throw new FileNotFoundException("Database file not found.", dbPath);
        var dir = backupDirectory ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "backups");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(dbPath)}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.bak.db");

        await using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dest }.ToString());
        await source.OpenAsync(ct);
        await target.OpenAsync(ct);
        source.BackupDatabase(target);
        SqliteConnection.ClearAllPools();
        return dest;
    }

    public static string BuildConnectionString(string dbPath) =>
        new SqliteConnectionStringBuilder { DataSource = dbPath, Cache = SqliteCacheMode.Shared }.ToString();
}

/// <summary>Used only by `dotnet ef` at design time.</summary>
public sealed class FleetDbContextFactory : IDesignTimeDbContextFactory<FleetDbContext>
{
    public FleetDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<FleetDbContext>().UseSqlite("Data Source=design-time.db").Options;
        return new FleetDbContext(options);
    }
}
