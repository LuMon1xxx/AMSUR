namespace Amsur.Infrastructure;

using Microsoft.Data.Sqlite;

// Бэкап/восстановление SQLite (EPIC-C C3, порт механики V1):
// VACUUM INTO (атомарный консистентный снапшот) + integrity_check до/после,
// safety-копия перед restore, corrupt отклоняется.
public sealed class SqliteBackupService(string connectionString)
{
    public async Task<string> BackupAsync(string backupPath, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await CheckIntegrityAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "VACUUM INTO $path;";
        cmd.Parameters.AddWithValue("$path", backupPath);
        await cmd.ExecuteNonQueryAsync(ct);
        await CheckFileIntegrityAsync(backupPath, ct);
        return backupPath;
    }

    public async Task RestoreAsync(string backupPath, CancellationToken ct = default)
    {
        if (!File.Exists(backupPath))
            throw new FileNotFoundException("Backup file not found.", backupPath);
        await CheckFileIntegrityAsync(backupPath, ct);

        var builder = new SqliteConnectionStringBuilder(connectionString);
        string dbPath = builder.DataSource;
        // Safety-копия текущего состояния перед перезаписью.
        if (File.Exists(dbPath))
        {
            string safety = dbPath + $".pre-restore-{DateTime.UtcNow:yyyyMMdd-HHmmss}.bak";
            File.Copy(dbPath, safety, overwrite: false);
        }
        SqliteConnection.ClearAllPools();
        File.Copy(backupPath, dbPath, overwrite: true);
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await CheckIntegrityAsync(conn, ct);
    }

    private static async Task CheckIntegrityAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        var result = (await cmd.ExecuteScalarAsync(ct))?.ToString();
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Database integrity check failed: {result}");
    }

    private static async Task CheckFileIntegrityAsync(string path, CancellationToken ct)
    {
        await using var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync(ct);
        await CheckIntegrityAsync(conn, ct);
    }
}
