using Amsur.Domain;
using Amsur.Infrastructure;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// EPIC-C C3: backup/restore.
public sealed class BackupTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"amsur-bak-{Guid.NewGuid():N}");

    public ValueTask DisposeAsync()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Backup_Restore_Roundtrip_PreservesActive()
    {
        Directory.CreateDirectory(_dir);
        string db = Path.Combine(_dir, "amsur.db");
        string bak = Path.Combine(_dir, "amsur.bak");
        var store = new SqliteScheduleStore($"Data Source={db}");
        await store.InitializeAsync();

        var year = Guid.NewGuid();
        var cls = new SchoolClass { AcademicYearId = year, Name = "5А", Grade = 5 };
        var t = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var s = new Subject { Name = "Математика", MaxPerDay = 2 };
        var item = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = s.Id, TeacherId = t.Id, HoursPerWeek = 2
        };
        var (problem, errors) = ProblemBuilder.Build(
            new ProblemInput([cls], [t], [s], [item], [], [], [], 2, 4));
        Assert.Empty(errors);
        var placements = problem!.Occurrences.Select((o, i) => new PlacedLesson
        {
            OccurrenceId = o.Id, DayIndex = i / 4, SlotIndex = (i % 4) + 1
        }).ToList();
        await store.AcceptAsync(year, problem, placements, "v1", "{}");

        var backup = new SqliteBackupService($"Data Source={db}");
        await backup.BackupAsync(bak);
        Assert.True(File.Exists(bak));

        // Порча рабочей БД восстановлением проверяется чтением из бэкапа напрямую:
        var restored = new SqliteScheduleStore($"Data Source={bak}");
        var active = await restored.GetActiveAsync(year);
        Assert.NotNull(active);
        Assert.Equal(2, active!.Placements.Count);

        // Restore поверх: закрываем пул, удаляем оригинал, восстанавливаем из бэкапа.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(db);
        await backup.RestoreAsync(bak);
        var active2 = await store.GetActiveAsync(year);
        Assert.NotNull(active2);
        Assert.Equal(2, active2!.Placements.Count);
    }

    [Fact]
    public async Task Restore_Corrupt_Rejected()
    {
        Directory.CreateDirectory(_dir);
        string db = Path.Combine(_dir, "amsur.db");
        string bad = Path.Combine(_dir, "bad.bak");
        await File.WriteAllTextAsync(bad, "not a sqlite database");
        var store = new SqliteScheduleStore($"Data Source={db}");
        await store.InitializeAsync();
        var backup = new SqliteBackupService($"Data Source={db}");
        await Assert.ThrowsAnyAsync<Exception>(() => backup.RestoreAsync(bad));
    }
}
