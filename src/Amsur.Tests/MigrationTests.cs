using Amsur.Application;
using Amsur.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Amsur.Tests;

// S10: миграции LoadRows — Contains (PRAGMA table_info), а не перехват дубля.
// Старые БД (без Pair/Shift и даже без Unavail-колонок) мигрируют с сохранением данных.
public sealed class MigrationTests
{
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"amsur-mig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    // --- 1. Старая схема (7 колонок) → миграция добавляет 4, данные целы ---
    [Fact]
    public async Task Migrate_OldDb_AddsColumns_KeepsData()
    {
        var dir = NewDir();
        try
        {
            string cs = $"Data Source={Path.Combine(dir, "school.db")}";
            // Вручную создаём схему до P-DAYOFF (без Unavail/Pair/Shift).
            await using (var conn = new SqliteConnection(cs))
            {
                await conn.OpenAsync();
                await using var ddl = conn.CreateCommand();
                ddl.CommandText = """
                    CREATE TABLE SchoolMeta(
                      YearId TEXT PRIMARY KEY, DaysCount INTEGER NOT NULL,
                      SlotsPerDay INTEGER NOT NULL, Source TEXT NOT NULL);
                    CREATE TABLE LoadRows(
                      Id INTEGER PRIMARY KEY AUTOINCREMENT,
                      ClassName TEXT NOT NULL, SubjectName TEXT NOT NULL,
                      HoursPerWeek INTEGER NOT NULL, TeacherName TEXT NOT NULL,
                      Split INTEGER NOT NULL, TeacherB TEXT NULL, Room TEXT NULL);
                    INSERT INTO SchoolMeta(YearId, DaysCount, SlotsPerDay, Source)
                    VALUES('11111111-1111-1111-1111-111111111111', 5, 8, 'old');
                    INSERT INTO LoadRows(ClassName, SubjectName, HoursPerWeek, TeacherName, Split)
                    VALUES('5А', 'Мат', 2, 'Иванов', 0);
                    """;
                await ddl.ExecuteNonQueryAsync();
            }
            var store = new SqliteSchoolDataStore(cs);
            await store.InitializeAsync(); // Contains-миграция, не перехват
            await store.InitializeAsync(); // повтор — идемпотентна, дублей нет
            var got = await store.LoadAsync();
            Assert.NotNull(got);
            var r = Assert.Single(got!.Rows);
            Assert.Equal("5А", r.ClassName);
            Assert.Null(r.PairName); // новых колонок в старой строке не было
            Assert.Null(r.Shift);
            Assert.Null(r.UnavailDays);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // --- 2. Roundtrip Pair/Shift через персист (ручной carry-over) ---
    [Fact]
    public async Task Roundtrip_PairShift()
    {
        var dir = NewDir();
        try
        {
            string cs = $"Data Source={Path.Combine(dir, "school.db")}";
            var store = new SqliteSchoolDataStore(cs);
            await store.InitializeAsync();
            var year = Guid.NewGuid();
            await store.SaveAsync(year,
            [
                new StoredLoadRow("10А", "Химия", 1, "Петрова", false, null, "312", PairName: "П1"),
                new StoredLoadRow("10А", "Биология", 1, "Сидорова", false, null, "314", PairName: "П1"),
                new StoredLoadRow("6А", "Мат", 2, "Сидоров", false, null, null, Shift: 2),
            ], 5, 12, "test");
            var got = await store.LoadAsync();
            Assert.NotNull(got);
            Assert.Equal(3, got!.Rows.Count);
            Assert.Equal("П1", got.Rows[0].PairName);
            Assert.Null(got.Rows[0].Shift);
            Assert.Equal(2, got.Rows[2].Shift);
            Assert.Null(got.Rows[2].PairName);
            // И обратно в LoadRow — carry-over полный (Excel-first для пар,
            // но персист и ручные правки пару не теряют).
            var loadRows = got.Rows.Select(r => new LoadRow(r.ClassName, r.SubjectName,
                r.HoursPerWeek, r.TeacherName, r.SplitSubgroups, r.SplitTeacherBName,
                r.RoomName, r.UnavailDays, r.UnavailSlots, r.PairName, r.Shift)).ToList();
            var data = SchoolDataImporter.Import(year, loadRows, daysCount: 5, slotsPerDay: 12);
            Assert.Equal("П1", loadRows[0].PairName);
            Assert.NotNull(data.Curriculum[0].SyncGroupId); // пара пережила персист
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
