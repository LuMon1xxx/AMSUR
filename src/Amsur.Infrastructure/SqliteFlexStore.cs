namespace Amsur.Infrastructure;

using Amsur.Domain;
using Microsoft.Data.Sqlite;

// Персистентность гибких настроек R1–R9 (D-34). Без EF (D-13), как соседи.
// Ключи — ИМЕНА (схлопываются OrdinalIgnoreCase на чтении, как LoadRows).
// CREATE TABLE IF NOT EXISTS → старые БД читаются без миграций: недостающие
// таблицы просто пусты, LoadAsync отдаёт дефолты (FlexDataset.Empty).
public sealed class SqliteFlexStore(string connectionString)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var ddl = """
            CREATE TABLE IF NOT EXISTS RoomConfig(
              RoomName TEXT PRIMARY KEY,
              IsManualOnly INTEGER NOT NULL,
              OnlySubjectName TEXT NULL,
              MaxGroups INTEGER NOT NULL,
              DesiredGroups INTEGER NOT NULL,
              CountSubgroupAsGroup INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS ClassConfig(
              ClassName TEXT PRIMARY KEY,
              ClassTeacherName TEXT NULL,
              Grade INTEGER NOT NULL,
              StudentCount INTEGER NOT NULL DEFAULT 25);
            CREATE TABLE IF NOT EXISTS HourNorms(
              SubjectName TEXT NOT NULL,
              Grade INTEGER NOT NULL,
              HoursPerWeek INTEGER NOT NULL,
              PRIMARY KEY(SubjectName, Grade));
            CREATE TABLE IF NOT EXISTS HourOverrides(
              ClassName TEXT NOT NULL,
              SubjectName TEXT NOT NULL,
              HoursPerWeek INTEGER NOT NULL,
              PRIMARY KEY(ClassName, SubjectName));
            CREATE TABLE IF NOT EXISTS SubjectDifficulty(
              SubjectName TEXT PRIMARY KEY,
              Difficulty INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS CommonLesson(
              Id INTEGER PRIMARY KEY CHECK(Id = 1),
              Enabled INTEGER NOT NULL,
              DayIndex INTEGER NOT NULL,
              SlotIndex INTEGER NOT NULL,
              GradesCsv TEXT NOT NULL,
              UseOwnRooms INTEGER NOT NULL,
              SlotIndexShift2 INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS TeacherAssign(
              TeacherName TEXT NOT NULL,
              SubjectName TEXT NOT NULL,
              Scope INTEGER NOT NULL,
              ClassName TEXT NULL,
              Grade INTEGER NULL,
              PRIMARY KEY(TeacherName, SubjectName, Scope, ClassName, Grade));
            CREATE TABLE IF NOT EXISTS FlexSettings(
              Id INTEGER PRIMARY KEY CHECK(Id = 1),
              GradePriorityEnabled INTEGER NOT NULL,
              W11 INTEGER NOT NULL,
              W9 INTEGER NOT NULL,
              WOther INTEGER NOT NULL,
              IsHeavyThreshold INTEGER NOT NULL,
              AssignMode INTEGER NOT NULL);
            """;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ddl;
        await cmd.ExecuteNonQueryAsync(ct);
        // Миграция старых БД (P2): StudentCount в ClassConfig.
        try
        {
            await using var mig = conn.CreateCommand();
            mig.CommandText = "ALTER TABLE ClassConfig ADD COLUMN StudentCount INTEGER NOT NULL DEFAULT 25;";
            await mig.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
        {
            // Колонка уже есть (свежая БД) — нечего делать.
        }
        // Миграция (MidSchool, R3 two-shift): SlotIndexShift2 в CommonLesson.
        try
        {
            await using var mig = conn.CreateCommand();
            mig.CommandText = "ALTER TABLE CommonLesson ADD COLUMN SlotIndexShift2 INTEGER NOT NULL DEFAULT 0;";
            await mig.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
        {
            // Колонка уже есть (свежая БД) — нечего делать.
        }
    }

    public async Task SaveAsync(FlexDataset data, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await ExecAsync(conn, tx, "DELETE FROM RoomConfig;", ct);
        foreach (var r in data.Rooms)
            await ExecAsync(conn, tx,
                "INSERT INTO RoomConfig(RoomName, IsManualOnly, OnlySubjectName, MaxGroups, DesiredGroups, CountSubgroupAsGroup) VALUES($n, $m, $o, $mx, $d, $c);",
                ct, ("$n", (object)r.RoomName), ("$m", r.IsManualOnly ? 1 : 0),
                ("$o", (object?)r.OnlySubjectName ?? DBNull.Value),
                ("$mx", r.MaxGroups), ("$d", r.DesiredGroups),
                ("$c", r.CountSubgroupAsGroup ? 1 : 0));

        await ExecAsync(conn, tx, "DELETE FROM ClassConfig;", ct);
        foreach (var c in data.Classes)
            await ExecAsync(conn, tx,
                "INSERT INTO ClassConfig(ClassName, ClassTeacherName, Grade, StudentCount) VALUES($n, $t, $g, $sc);",
                ct, ("$n", (object)c.ClassName),
                ("$t", (object?)c.ClassTeacherName ?? DBNull.Value), ("$g", c.Grade),
                ("$sc", c.StudentCount));

        await ExecAsync(conn, tx, "DELETE FROM HourNorms;", ct);
        foreach (var n in data.HourNorms)
            await ExecAsync(conn, tx,
                "INSERT INTO HourNorms(SubjectName, Grade, HoursPerWeek) VALUES($s, $g, $h);",
                ct, ("$s", (object)n.SubjectName), ("$g", n.Grade), ("$h", n.HoursPerWeek));

        await ExecAsync(conn, tx, "DELETE FROM HourOverrides;", ct);
        foreach (var o in data.HourOverrides)
            await ExecAsync(conn, tx,
                "INSERT INTO HourOverrides(ClassName, SubjectName, HoursPerWeek) VALUES($c, $s, $h);",
                ct, ("$c", (object)o.ClassName), ("$s", (object)o.SubjectName),
                ("$h", o.HoursPerWeek));

        await ExecAsync(conn, tx, "DELETE FROM SubjectDifficulty;", ct);
        foreach (var d in data.SubjectDifficulty)
            await ExecAsync(conn, tx,
                "INSERT INTO SubjectDifficulty(SubjectName, Difficulty) VALUES($s, $d);",
                ct, ("$s", (object)d.SubjectName), ("$d", d.Difficulty));

        await ExecAsync(conn, tx, "DELETE FROM CommonLesson;", ct);
        if (data.CommonLesson is not null)
            await ExecAsync(conn, tx,
                "INSERT INTO CommonLesson(Id, Enabled, DayIndex, SlotIndex, GradesCsv, UseOwnRooms, SlotIndexShift2) VALUES(1, $e, $d, $s, $g, $o, $s2);",
                ct, ("$e", data.CommonLesson.Enabled ? 1 : 0),
                ("$d", data.CommonLesson.DayIndex), ("$s", data.CommonLesson.SlotIndex),
                ("$g", (object)data.CommonLesson.GradesCsv),
                ("$o", data.CommonLesson.UseOwnRooms ? 1 : 0),
                ("$s2", data.CommonLesson.SlotIndexShift2));

        await ExecAsync(conn, tx, "DELETE FROM TeacherAssign;", ct);
        foreach (var a in data.Assignments)
            await ExecAsync(conn, tx,
                "INSERT INTO TeacherAssign(TeacherName, SubjectName, Scope, ClassName, Grade) VALUES($t, $s, $sc, $c, $g);",
                ct, ("$t", (object)a.TeacherName), ("$s", (object)a.SubjectName),
                ("$sc", (int)a.Scope), ("$c", (object?)a.ClassName ?? DBNull.Value),
                ("$g", (object?)a.Grade ?? DBNull.Value));

        await ExecAsync(conn, tx, "DELETE FROM FlexSettings;", ct);
        var st = data.Settings;
        await ExecAsync(conn, tx,
            "INSERT INTO FlexSettings(Id, GradePriorityEnabled, W11, W9, WOther, IsHeavyThreshold, AssignMode) VALUES(1, $e, $w11, $w9, $wo, $th, $am);",
            ct, ("$e", st.GradePriorityEnabled ? 1 : 0), ("$w11", st.W11),
            ("$w9", st.W9), ("$wo", st.WOther), ("$th", st.IsHeavyThreshold),
            ("$am", (int)st.AssignMode));

        await tx.CommitAsync(ct);
    }

    public async Task<FlexDataset> LoadAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var rooms = new List<RoomConfigRow>();
        await using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT RoomName, IsManualOnly, OnlySubjectName, MaxGroups, DesiredGroups, CountSubgroupAsGroup FROM RoomConfig;";
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rooms.Add(new RoomConfigRow(r.GetString(0), r.GetInt32(1) != 0,
                    r.IsDBNull(2) ? null : r.GetString(2),
                    r.GetInt32(3), r.GetInt32(4), r.GetInt32(5) != 0));
        }
        var classes = new List<ClassConfigRow>();
        await using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT ClassName, ClassTeacherName, Grade, StudentCount FROM ClassConfig;";
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                classes.Add(new ClassConfigRow(r.GetString(0),
                    r.IsDBNull(1) ? null : r.GetString(1), r.GetInt32(2), r.GetInt32(3)));
        }
        var norms = new List<HourNormRow>();
        await using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT SubjectName, Grade, HoursPerWeek FROM HourNorms;";
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                norms.Add(new HourNormRow(r.GetString(0), r.GetInt32(1), r.GetInt32(2)));
        }
        var overrides = new List<HourOverrideRow>();
        await using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT ClassName, SubjectName, HoursPerWeek FROM HourOverrides;";
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                overrides.Add(new HourOverrideRow(r.GetString(0), r.GetString(1), r.GetInt32(2)));
        }
        var difficulty = new List<SubjectDifficultyRow>();
        await using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT SubjectName, Difficulty FROM SubjectDifficulty;";
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                difficulty.Add(new SubjectDifficultyRow(r.GetString(0), r.GetInt32(1)));
        }
        CommonLessonRow? common = null;
        await using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT Enabled, DayIndex, SlotIndex, GradesCsv, UseOwnRooms, SlotIndexShift2 FROM CommonLesson WHERE Id=1;";
            await using var r = await q.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
                common = new CommonLessonRow(r.GetInt32(0) != 0, r.GetInt32(1),
                    r.GetInt32(2), r.GetString(3), r.GetInt32(4) != 0, r.GetInt32(5));
        }
        var assigns = new List<TeacherAssignRow>();
        await using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT TeacherName, SubjectName, Scope, ClassName, Grade FROM TeacherAssign;";
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                assigns.Add(new TeacherAssignRow(r.GetString(0), r.GetString(1),
                    (AssignmentScope)r.GetInt32(2),
                    r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetInt32(4)));
        }
        var settings = FlexSettingsRow.Default;
        await using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT GradePriorityEnabled, W11, W9, WOther, IsHeavyThreshold, AssignMode FROM FlexSettings WHERE Id=1;";
            await using var r = await q.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
                settings = new FlexSettingsRow(r.GetInt32(0) != 0, r.GetInt32(1),
                    r.GetInt32(2), r.GetInt32(3), r.GetInt32(4),
                    (TeacherAssignMode)r.GetInt32(5));
        }
        return new FlexDataset(rooms, classes, norms, overrides, difficulty, common, assigns, settings);
    }

    private static async Task ExecAsync(SqliteConnection conn, SqliteTransaction tx,
        string sql, CancellationToken ct, params (string Name, object Value)[] ps)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in ps)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
