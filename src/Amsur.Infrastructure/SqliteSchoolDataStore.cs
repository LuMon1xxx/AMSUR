namespace Amsur.Infrastructure;

using Microsoft.Data.Sqlite;

// Персистентность исходных строк нагрузки (P3: ручной ввод + переживание перезапуска).
// Храним ИСТОЧНИК, а не построенные сущности: Id сущностей генерируются импортёром
// заново, а StableKey occurrence детерминированы (E11) — активное расписание
// переживает перезапуск. Своё DTO (без зависимости на Application). Без EF (D-13).
public sealed record StoredLoadRow(
    string ClassName,
    string SubjectName,
    int HoursPerWeek,
    string TeacherName,
    bool SplitSubgroups,
    string? SplitTeacherBName,
    string? RoomName);

public sealed record SchoolDataset(
    Guid AcademicYearId,
    IReadOnlyList<StoredLoadRow> Rows,
    int DaysCount,
    int SlotsPerDay,
    string Source);

public sealed class SqliteSchoolDataStore(string connectionString)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var ddl = """
            CREATE TABLE IF NOT EXISTS SchoolMeta(
              YearId TEXT PRIMARY KEY,
              DaysCount INTEGER NOT NULL,
              SlotsPerDay INTEGER NOT NULL,
              Source TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS LoadRows(
              Id INTEGER PRIMARY KEY AUTOINCREMENT,
              ClassName TEXT NOT NULL,
              SubjectName TEXT NOT NULL,
              HoursPerWeek INTEGER NOT NULL,
              TeacherName TEXT NOT NULL,
              Split INTEGER NOT NULL,
              TeacherB TEXT NULL,
              Room TEXT NULL);
            """;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ddl;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveAsync(
        Guid yearId, IReadOnlyList<StoredLoadRow> rows, int days, int slots, string source,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await using (var clear = conn.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM LoadRows; DELETE FROM SchoolMeta;";
            await clear.ExecuteNonQueryAsync(ct);
        }
        await using (var meta = conn.CreateCommand())
        {
            meta.Transaction = tx;
            meta.CommandText = """
                INSERT INTO SchoolMeta(YearId, DaysCount, SlotsPerDay, Source)
                VALUES($y, $d, $s, $src);
                """;
            meta.Parameters.AddWithValue("$y", yearId.ToString("D"));
            meta.Parameters.AddWithValue("$d", days);
            meta.Parameters.AddWithValue("$s", slots);
            meta.Parameters.AddWithValue("$src", source);
            await meta.ExecuteNonQueryAsync(ct);
        }
        foreach (var r in rows)
        {
            await using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO LoadRows(ClassName, SubjectName, HoursPerWeek, TeacherName, Split, TeacherB, Room)
                VALUES($c, $s, $h, $t, $sp, $tb, $rm);
                """;
            ins.Parameters.AddWithValue("$c", r.ClassName);
            ins.Parameters.AddWithValue("$s", r.SubjectName);
            ins.Parameters.AddWithValue("$h", r.HoursPerWeek);
            ins.Parameters.AddWithValue("$t", r.TeacherName);
            ins.Parameters.AddWithValue("$sp", r.SplitSubgroups ? 1 : 0);
            ins.Parameters.AddWithValue("$tb", (object?)r.SplitTeacherBName ?? DBNull.Value);
            ins.Parameters.AddWithValue("$rm", (object?)r.RoomName ?? DBNull.Value);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<SchoolDataset?> LoadAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        Guid yearId;
        int days, slots;
        string source;
        await using (var meta = conn.CreateCommand())
        {
            meta.CommandText = "SELECT YearId, DaysCount, SlotsPerDay, Source FROM SchoolMeta LIMIT 1;";
            await using var r = await meta.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            if (!Guid.TryParse(r.GetString(0), out yearId)) return null;
            days = r.GetInt32(1);
            slots = r.GetInt32(2);
            source = r.GetString(3);
        }
        var rows = new List<StoredLoadRow>();
        await using (var q = conn.CreateCommand())
        {
            q.CommandText = """
                SELECT ClassName, SubjectName, HoursPerWeek, TeacherName, Split, TeacherB, Room
                FROM LoadRows ORDER BY Id;
                """;
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                rows.Add(new StoredLoadRow(
                    r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3),
                    r.GetInt32(4) != 0,
                    r.IsDBNull(5) ? null : r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetString(6)));
            }
        }
        return new SchoolDataset(yearId, rows, days, slots, source);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
