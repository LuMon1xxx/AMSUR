namespace Amsur.Infrastructure;

using Microsoft.Data.Sqlite;

// B1: ключ-значение настроек UI (raw SQLite, БЕЗ EF — как остальные сторы).
// Ключи: ui.confirmDangerous (bool, дефолт true).
public sealed class SqliteAppSettingsStore(string connectionString)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS AppSettings(
              Key TEXT PRIMARY KEY,
              Value TEXT NOT NULL);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM AppSettings WHERE Key=$k LIMIT 1;";
        cmd.Parameters.AddWithValue("$k", key);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is string s ? s : null;
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO AppSettings(Key, Value) VALUES($k, $v)
            ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value;
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> GetBoolAsync(string key, bool @default, CancellationToken ct = default)
    {
        var v = await GetAsync(key, ct);
        return v is null ? @default : v == "1";
    }

    public async Task SetBoolAsync(string key, bool value, CancellationToken ct = default) =>
        await SetAsync(key, value ? "1" : "0", ct);

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
