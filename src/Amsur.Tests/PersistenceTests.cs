using Amsur.Application;
using Amsur.Domain;
using Amsur.Infrastructure;
using Amsur.Scheduling.Core;
using Microsoft.Data.Sqlite;

namespace Amsur.Tests;

// EPIC-C C1: atomic Accept + validator-before-write + конкурентность + data-repair.
public sealed class PersistenceTests : IAsyncDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"amsur-p0-{Guid.NewGuid():N}.db");
    private string Cs => $"Data Source={_dbPath}";

    public async ValueTask DisposeAsync()
    {
        for (int i = 0; i < 5; i++)
        {
            try { if (File.Exists(_dbPath)) File.Delete(_dbPath); break; }
            catch { await Task.Delay(100); }
        }
        try { if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal"); } catch { }
        try { if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm"); } catch { }
        GC.SuppressFinalize(this);
    }

    private static (SchedulingProblem Problem, Guid Year) TinyProblem(int hours = 2)
    {
        var year = Guid.NewGuid();
        var cls = new SchoolClass { AcademicYearId = year, Name = "5А", Grade = 5 };
        var t = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var s = new Subject { Name = "Математика", MaxPerDay = 2 };
        var item = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = s.Id, TeacherId = t.Id, HoursPerWeek = hours
        };
        var input = new ProblemInput([cls], [t], [s], [item], [], [], [], 2, 4);
        var (problem, errors) = ProblemBuilder.Build(input);
        Assert.Empty(errors);
        return (problem!, year);
    }

    private static List<PlacedLesson> ValidPlacements(SchedulingProblem problem)
    {
        var occ = problem.Occurrences;
        return occ.Select((o, i) => new PlacedLesson
        {
            OccurrenceId = o.Id, DayIndex = i / 4, SlotIndex = (i % 4) + 1
        }).ToList();
    }

    [Fact]
    public async Task Accept_Valid_BecomesActive()
    {
        var (problem, year) = TinyProblem();
        var store = new SqliteScheduleStore(Cs);
        await store.InitializeAsync();
        var svc = new AcceptScheduleService(store);

        var (accepted, breakdown) = await svc.AcceptAsync(
            year, problem, ValidPlacements(problem), "initial", "{}");
        Assert.Equal(1, accepted.Number);
        Assert.True(breakdown.Total >= 0);

        var active = await store.GetActiveAsync(year);
        Assert.NotNull(active);
        Assert.Equal(accepted.VersionId, active!.VersionId);
        Assert.Equal(2, active.Placements.Count);
        Assert.Equal(1, await store.CountActiveVersionsAsync(year));
    }

    [Fact]
    public async Task Accept_InvalidHard_DoesNotReplaceActive()
    {
        var (problem, year) = TinyProblem();
        var store = new SqliteScheduleStore(Cs);
        await store.InitializeAsync();
        var svc = new AcceptScheduleService(store);
        var good = ValidPlacements(problem);
        await svc.AcceptAsync(year, problem, good, "initial", "{}");

        // Невалидные: оба occurrence в один слот (teacher+group collision).
        var bad = new List<PlacedLesson>
        {
            new() { OccurrenceId = problem.Occurrences[0].Id, DayIndex = 0, SlotIndex = 1 },
            new() { OccurrenceId = problem.Occurrences[1].Id, DayIndex = 0, SlotIndex = 1 },
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.AcceptAsync(year, problem, bad, "bad", "{}"));

        var active = await store.GetActiveAsync(year);
        Assert.NotNull(active);
        Assert.Equal(1, active!.Number); // прежняя версия на месте
        Assert.Equal(2, active.Placements.Count);
    }

    [Fact]
    public async Task Concurrent_Accepts_ExactlyOneActive()
    {
        var (problem, year) = TinyProblem(hours: 3);
        var store = new SqliteScheduleStore(Cs);
        await store.InitializeAsync();
        var svc = new AcceptScheduleService(store);

        // 5 конкурентных приёмок валидных (но разных) placements.
        // D-28: все на дне 0 сплошным блоком {1,2,3} (ротация) — compact-clean.
        var tasks = Enumerable.Range(0, 5).Select(i =>
        {
            var pl = problem.Occurrences.Select((o, k) => new PlacedLesson
            {
                OccurrenceId = o.Id,
                DayIndex = 0,
                SlotIndex = ((k + i) % 3) + 1
            }).ToList();
            return svc.AcceptAsync(year, problem, pl, $"gen{i}", "{}");
        }).ToList();
        await Task.WhenAll(tasks);

        Assert.Equal(1, await store.CountActiveVersionsAsync(year));
        var active = await store.GetActiveAsync(year);
        Assert.NotNull(active);
        Assert.Equal(5, active!.Number); // 5 последовательных версий, активна последняя
        Assert.True(PlacementValidator.Validate(problem, active.Placements).IsValid);
    }

    [Fact]
    public async Task Repair_DuplicateActive_LeavesMaxNumber()
    {
        var (problem, year) = TinyProblem();
        var store = new SqliteScheduleStore(Cs);
        await store.InitializeAsync();
        var svc = new AcceptScheduleService(store);
        await svc.AcceptAsync(year, problem, ValidPlacements(problem), "v1", "{}");
        await svc.AcceptAsync(year, problem, ValidPlacements(problem), "v2", "{}");

        // Порча вручную: реактивируем старый schedule + его версию в обход API.
        await using (var conn = new SqliteConnection(Cs))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE Schedules SET IsActive=1 WHERE Id IN
                  (SELECT ScheduleId FROM Versions WHERE Number=1);
                UPDATE Versions SET IsActive=1 WHERE Number=1;
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        Assert.Equal(2, await store.CountActiveVersionsAsync(year));

        var log = await store.RepairAsync();
        Assert.NotEmpty(log);
        Assert.Equal(1, await store.CountActiveVersionsAsync(year));
        var active = await store.GetActiveAsync(year);
        Assert.Equal(2, active!.Number); // оставлен max(Number)
    }
}
