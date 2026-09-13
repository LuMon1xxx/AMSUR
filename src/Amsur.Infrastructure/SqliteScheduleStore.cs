namespace Amsur.Infrastructure;

using Amsur.Scheduling.Core;
using Microsoft.Data.Sqlite;

// SQLite source of truth, БЕЗ EF (D-13): 3 таблицы + явные транзакции.
// P0-scope: только путь приёмки расписания (school-data CRUD — P2).
public sealed class SqliteScheduleStore(string connectionString) : IScheduleStore
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var ddl = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Schedules(
              Id TEXT PRIMARY KEY,
              AcademicYearId TEXT NOT NULL,
              IsActive INTEGER NOT NULL,
              CreatedAt TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Placements(
              Id TEXT PRIMARY KEY,
              ScheduleId TEXT NOT NULL REFERENCES Schedules(Id),
              OccurrenceId TEXT NOT NULL,
              DayIndex INTEGER NOT NULL,
              SlotIndex INTEGER NOT NULL,
              RoomId TEXT NULL);
            CREATE INDEX IF NOT EXISTS ix_placements_schedule ON Placements(ScheduleId);
            CREATE TABLE IF NOT EXISTS Versions(
              Id TEXT PRIMARY KEY,
              ScheduleId TEXT NOT NULL REFERENCES Schedules(Id),
              Number INTEGER NOT NULL,
              IsActive INTEGER NOT NULL,
              Reason TEXT NOT NULL,
              SolverSettingsJson TEXT NOT NULL,
              RuleSetVersion TEXT NOT NULL,
              CreatedAt TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_versions_single_active
              ON Versions(ScheduleId) WHERE IsActive=1;
            """;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ddl;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<string>> RepairAsync(CancellationToken ct = default)
    {
        var log = new List<string>();
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        // Несколько активных версий одного schedule → оставить max(Number).
        await using var dup = conn.CreateCommand();
        dup.Transaction = tx;
        dup.CommandText = """
            SELECT ScheduleId FROM Versions WHERE IsActive=1
            GROUP BY ScheduleId HAVING COUNT(*) > 1;
            """;
        var dupIds = new List<string>();
        await using (var r = await dup.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) dupIds.Add(r.GetString(0));
        foreach (var sid in dupIds)
        {
            await using var fix = conn.CreateCommand();
            fix.Transaction = tx;
            fix.CommandText = """
                UPDATE Versions SET IsActive=0 WHERE ScheduleId=$sid
                  AND Id NOT IN (SELECT Id FROM Versions WHERE ScheduleId=$sid
                                 ORDER BY Number DESC LIMIT 1);
                """;
            fix.Parameters.AddWithValue("$sid", sid);
            int n = await fix.ExecuteNonQueryAsync(ct);
            log.Add($"Repaired {n} duplicate active versions for schedule {sid} (kept max Number).");
        }
        // Несколько активных schedule одного года → оставить самый свежий по Versions.Number.
        await using var years = conn.CreateCommand();
        years.Transaction = tx;
        years.CommandText = """
            SELECT s.AcademicYearId FROM Schedules s
            JOIN Versions v ON v.ScheduleId=s.Id AND v.IsActive=1
            WHERE s.IsActive=1 GROUP BY s.AcademicYearId HAVING COUNT(*) > 1;
            """;
        var yearsDup = new List<string>();
        await using (var r = await years.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) yearsDup.Add(r.GetString(0));
        foreach (var y in yearsDup)
        {
            await using var fix = conn.CreateCommand();
            fix.Transaction = tx;
            fix.CommandText = """
                UPDATE Schedules SET IsActive=0 WHERE AcademicYearId=$y AND IsActive=1
                  AND Id NOT IN (SELECT v.ScheduleId FROM Versions v
                                 JOIN Schedules s ON s.Id=v.ScheduleId
                                 WHERE s.AcademicYearId=$y AND v.IsActive=1
                                 ORDER BY v.Number DESC LIMIT 1);
                UPDATE Versions SET IsActive=0 WHERE IsActive=1 AND ScheduleId IN
                  (SELECT Id FROM Schedules WHERE AcademicYearId=$y AND IsActive=0);
                """;
            fix.Parameters.AddWithValue("$y", y);
            await fix.ExecuteNonQueryAsync(ct);
            log.Add($"Repaired duplicate active schedules for year {y} (kept latest version).");
        }
        await tx.CommitAsync(ct);
        return log;
    }

    public async Task<AcceptedVersion> AcceptAsync(
        Guid academicYearId,
        SchedulingProblem problem,
        IReadOnlyList<PlacedLesson> placements,
        string reason,
        string solverSettings,
        CancellationToken ct = default)
    {
        // INV-01: gate ДО открытия транзакции (быстрый отказ без блокировок).
        var validation = PlacementValidator.Validate(problem, placements);
        if (!validation.IsValid)
            throw new InvalidOperationException(
                $"Refusing to persist schedule: {validation.HardViolations.Count} hard violations.");

        await using var conn = await OpenAsync(ct);
        // BEGIN IMMEDIATE: сериализация конкурентных Accept (R1-митигация).
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, ct);
        try
        {
            // Повторный gate внутри транзакции (защита от состояния гонки входных данных).
            var recheck = PlacementValidator.Validate(problem, placements);
            if (!recheck.IsValid)
                throw new InvalidOperationException(
                    $"Refusing to persist schedule (recheck): {recheck.HardViolations.Count} hard violations.");

            var year = academicYearId.ToString("D");
            var scheduleId = Guid.NewGuid().ToString("D");
            var now = DateTime.UtcNow.ToString("O");

            await using (var deactivate = conn.CreateCommand())
            {
                deactivate.Transaction = tx;
                deactivate.CommandText = """
                    UPDATE Versions SET IsActive=0 WHERE IsActive=1 AND ScheduleId IN
                      (SELECT Id FROM Schedules WHERE AcademicYearId=$y);
                    UPDATE Schedules SET IsActive=0 WHERE AcademicYearId=$y AND IsActive=1;
                    """;
                deactivate.Parameters.AddWithValue("$y", year);
                await deactivate.ExecuteNonQueryAsync(ct);
            }

            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO Schedules(Id, AcademicYearId, IsActive, CreatedAt)
                    VALUES($id, $y, 1, $now);
                    """;
                ins.Parameters.AddWithValue("$id", scheduleId);
                ins.Parameters.AddWithValue("$y", year);
                ins.Parameters.AddWithValue("$now", now);
                await ins.ExecuteNonQueryAsync(ct);
            }

            foreach (var p in placements)
            {
                await using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO Placements(Id, ScheduleId, OccurrenceId, DayIndex, SlotIndex, RoomId)
                    VALUES($id, $sid, $occ, $d, $s, $room);
                    """;
                ins.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                ins.Parameters.AddWithValue("$sid", scheduleId);
                ins.Parameters.AddWithValue("$occ", p.OccurrenceId.ToString("D"));
                ins.Parameters.AddWithValue("$d", p.DayIndex);
                ins.Parameters.AddWithValue("$s", p.SlotIndex);
                ins.Parameters.AddWithValue("$room", (object?)p.RoomId?.ToString("D") ?? DBNull.Value);
                await ins.ExecuteNonQueryAsync(ct);
            }

            int number;
            await using (var max = conn.CreateCommand())
            {
                max.Transaction = tx;
                max.CommandText = """
                    SELECT COALESCE(MAX(v.Number),0) FROM Versions v
                    JOIN Schedules s ON s.Id=v.ScheduleId WHERE s.AcademicYearId=$y;
                    """;
                max.Parameters.AddWithValue("$y", year);
                number = Convert.ToInt32(await max.ExecuteScalarAsync(ct)) + 1;
            }

            var versionId = Guid.NewGuid().ToString("D");
            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO Versions(Id, ScheduleId, Number, IsActive, Reason,
                                        SolverSettingsJson, RuleSetVersion, CreatedAt)
                    VALUES($id, $sid, $n, 1, $reason, $settings, $rules, $now);
                    """;
                ins.Parameters.AddWithValue("$id", versionId);
                ins.Parameters.AddWithValue("$sid", scheduleId);
                ins.Parameters.AddWithValue("$n", number);
                ins.Parameters.AddWithValue("$reason", reason);
                ins.Parameters.AddWithValue("$settings", solverSettings);
                ins.Parameters.AddWithValue("$rules", RuleCatalog.Version.ToString());
                ins.Parameters.AddWithValue("$now", now);
                await ins.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            return new AcceptedVersion(Guid.Parse(versionId), Guid.Parse(scheduleId), number);
        }
        catch
        {
            try { await tx.RollbackAsync(ct); } catch { /* rollback лучше не маскировать исходную ошибку */ }
            throw;
        }
    }

    public async Task<ActiveSchedule?> GetActiveAsync(Guid academicYearId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT v.Id, v.ScheduleId, v.Number, v.Reason, v.SolverSettingsJson, v.RuleSetVersion
            FROM Versions v JOIN Schedules s ON s.Id=v.ScheduleId
            WHERE s.AcademicYearId=$y AND v.IsActive=1 AND s.IsActive=1
            ORDER BY v.Number DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$y", academicYearId.ToString("D"));
        string? vid = null, sid = null, reason = null, settings = null, rules = null;
        int number = 0;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            vid = r.GetString(0); sid = r.GetString(1); number = r.GetInt32(2);
            reason = r.GetString(3); settings = r.GetString(4); rules = r.GetString(5);
        }
        var placements = new List<PlacedLesson>();
        await using var pl = conn.CreateCommand();
        pl.CommandText = "SELECT OccurrenceId, DayIndex, SlotIndex, RoomId FROM Placements WHERE ScheduleId=$sid;";
        pl.Parameters.AddWithValue("$sid", sid);
        await using (var r = await pl.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
                placements.Add(new PlacedLesson
                {
                    OccurrenceId = Guid.Parse(r.GetString(0)),
                    DayIndex = r.GetInt32(1),
                    SlotIndex = r.GetInt32(2),
                    RoomId = r.IsDBNull(3) ? null : Guid.Parse(r.GetString(3)),
                });
        }
        return new ActiveSchedule(Guid.Parse(sid!), Guid.Parse(vid!), number,
            reason!, settings!, rules!, placements);
    }

    public async Task<int> CountActiveVersionsAsync(Guid academicYearId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM Versions v JOIN Schedules s ON s.Id=v.ScheduleId
            WHERE s.AcademicYearId=$y AND v.IsActive=1 AND s.IsActive=1;
            """;
        cmd.Parameters.AddWithValue("$y", academicYearId.ToString("D"));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private readonly string _cs = connectionString;

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_cs);
        await conn.OpenAsync(ct);
        await using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=30000;";
        await pragma.ExecuteNonQueryAsync(ct);
        return conn;
    }
}
