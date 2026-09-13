namespace Amsur.Infrastructure;

using System.Text.Json;
using Amsur.Scheduling.Core;
using Microsoft.Data.Sqlite;

// Персистентность пользовательских профилей качества (S5 P4).
// Отдельный store (IScheduleStore не тронут): таблица QualityProfiles,
// single-active на школу (школа = AcademicYearId строк CUSTOM-профилей).
public sealed class SqliteQualityProfileStore(string connectionString)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS QualityProfiles(
              Id TEXT PRIMARY KEY,
              Name TEXT NOT NULL,
              BaseProfile TEXT NOT NULL,
              WeightsJson TEXT NOT NULL,
              CatalogVersion INTEGER NOT NULL,
              UpdatedAt TEXT NOT NULL,
              IsActive INTEGER NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_qualityprofiles_single_active
              ON QualityProfiles(IsActive) WHERE IsActive=1;
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveCustomAsync(
        string name, string baseProfile, IReadOnlyDictionary<string, long> weights,
        CancellationToken ct = default)
    {
        // Валидация через резолвер (диапазоны + коды) до записи.
        var rs = RuleResolver.Resolve("CUSTOM", weights);
        var json = JsonSerializer.Serialize(rs.Weights);
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await using (var off = conn.CreateCommand())
        {
            off.Transaction = tx;
            off.CommandText = "UPDATE QualityProfiles SET IsActive=0 WHERE IsActive=1;";
            await off.ExecuteNonQueryAsync(ct);
        }
        await using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO QualityProfiles(Id, Name, BaseProfile, WeightsJson, CatalogVersion, UpdatedAt, IsActive)
                VALUES($id, $name, $base, $json, $ver, $ts, 1);
                """;
            ins.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            ins.Parameters.AddWithValue("$name", name);
            ins.Parameters.AddWithValue("$base", baseProfile);
            ins.Parameters.AddWithValue("$json", json);
            ins.Parameters.AddWithValue("$ver", rs.CatalogVersion);
            ins.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
            await ins.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<QualityProfileRecord?> GetActiveAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Name, BaseProfile, WeightsJson, CatalogVersion, UpdatedAt
            FROM QualityProfiles WHERE IsActive=1 LIMIT 1;
            """;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var weights = JsonSerializer.Deserialize<Dictionary<string, long>>(r.GetString(3)) ?? [];
        return new QualityProfileRecord(
            Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2),
            weights, r.GetInt32(4),
            DateTime.Parse(r.GetString(5), null,
                System.Globalization.DateTimeStyles.RoundtripKind),
            true);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA busy_timeout=30000;";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return conn;
    }
}
