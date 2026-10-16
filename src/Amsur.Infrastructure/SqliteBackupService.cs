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
        // VACUUM INTO падает, если целевой файл уже существует: собираем снапшот во
        // временный файл и затем атомарно подменяем цель (старый бэкап не теряется,
        // пока новый не проверен на целостность).
        string tmp = backupPath + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "VACUUM INTO $path;";
                cmd.Parameters.AddWithValue("$path", tmp);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await CheckFileIntegrityAsync(tmp, ct);
            File.Move(tmp, backupPath, overwrite: true);
        }
        finally
        {
            // Best-effort: под Windows pooled-соединение может держать хэндл,
            // и неудачный Delete не должен маскировать реальную ошибку выше.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
        }
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
        // Safety-копия текущего состояния перед перезаписью. Guid-суффикс — чтобы два
        // restore в одну секунду не падали на коллизии имени (overwrite: false).
        if (File.Exists(dbPath))
        {
            string safety = dbPath + $".pre-restore-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.bak";
            File.Copy(dbPath, safety, overwrite: false);
        }
        SqliteConnection.ClearAllPools();
        File.Copy(backupPath, dbPath, overwrite: true);
        // WAL/SHM старой БД относятся к СТАРОМУ файлу: если их не снести, SQLite
        // может накатить их страницы поверх восстановленной базы.
        DeleteIfExists(dbPath + "-wal");
        DeleteIfExists(dbPath + "-shm");
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await CheckIntegrityAsync(conn, ct);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
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
        // Pooling=False: pooled-соединение держало бы файл открытым и мешало
        // File.Move/Delete сразу после проверки снапшота.
        await using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        await conn.OpenAsync(ct);
        await CheckIntegrityAsync(conn, ct);
    }
}
