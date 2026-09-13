using Amsur.Application;
using Amsur.Domain;
using Amsur.Infrastructure;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// E7 — Accept-цикл: лучший вариант Top-5 → активная версия (D-06).
public sealed class AcceptFlowTests : IAsyncDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"amsur-e7-{Guid.NewGuid():N}.db");
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

    private static SchedulingProblem Tiny(Guid year)
    {
        var cls = new SchoolClass { AcademicYearId = year, Name = "5А", Grade = 5, StudentCount = 25 };
        var teacher = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Мат", MaxPerDay = 2 };
        var item = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = math.Id, TeacherId = teacher.Id, HoursPerWeek = 2
        };
        var input = new ProblemInput([cls], [teacher], [math], [item],
            [], [], [], DaysCount: 2, SlotsPerDay: 3);
        var (p, e) = ProblemBuilder.Build(input,
            new SolverOptions(MaxTimeSeconds: 5, NumSearchWorkers: 1, RandomSeed: 1));
        Assert.Empty(e);
        return p!;
    }

    private static List<PlacedLesson> Valid(SchedulingProblem p) =>
        p.Occurrences.Select((o, i) => new PlacedLesson
        {
            OccurrenceId = o.Id, DayIndex = 0, SlotIndex = i + 1
        }).ToList();

    private async Task<(GenerationOrchestrator Orch, Guid Year, SqliteScheduleStore Store)> HarnessAsync()
    {
        var year = Guid.NewGuid();
        var store = new SqliteScheduleStore(Cs);
        await store.InitializeAsync();
        StreamingRun run = (p, ct, sink) =>
        {
            var pl = Valid(p);
            sink(new SolverIncumbent(pl, 5, 1, 5, "B", 1));
            return Task.FromResult(new SolverResult(SolverStatus.Feasible, pl, 0, 1, 10, 5, 0,
                new Dictionary<string, long>(), [], new Dictionary<string, long>()));
        };
        var orch = new GenerationOrchestrator(_ => Tiny(year), run,
            acceptor: new AcceptScheduleService(store));
        await orch.RunAsync([11]);
        return (orch, year, store);
    }

    // --- 1. Лучший принимается и становится активным ---
    [Fact]
    public async Task AcceptBest_PersistsActive()
    {
        var (orch, year, store) = await HarnessAsync();
        var best = orch.ViewModel.Top5.Best!;
        Assert.True(best.CanAccept);
        var outcome = await orch.AcceptAsync(best);
        Assert.True(outcome.Succeeded);
        Assert.Contains("версия 1", outcome.Message);
        Assert.Equal(1, await store.CountActiveVersionsAsync(year));
        var active = await store.GetActiveAsync(year);
        Assert.NotNull(active);
        Assert.Equal(2, active!.Placements.Count);
        Assert.Equal("Активна версия 1", orch.ViewModel.ActiveVersionText);
        Assert.NotEmpty(orch.ViewModel.AcceptResultText);
    }

    // --- 2. Повторная приёмка: актив один, номер растёт ---
    [Fact]
    public async Task AcceptTwice_SingleActiveNumberGrows()
    {
        var (orch, year, store) = await HarnessAsync();
        var best = orch.ViewModel.Top5.Best!;
        Assert.True((await orch.AcceptAsync(best)).Succeeded);
        Assert.True((await orch.AcceptAsync(best)).Succeeded);
        Assert.Equal(1, await store.CountActiveVersionsAsync(year));
        Assert.Equal(2, (await store.GetActiveAsync(year))!.Number);
        Assert.Equal("Активна версия 2", orch.ViewModel.ActiveVersionText);
    }

    // --- 3. Не-лучший отклонить, ничего не писать ---
    [Fact]
    public async Task AcceptNonBest_RefusedWithoutWrite()
    {
        var (orch, year, store) = await HarnessAsync();
        var best = orch.ViewModel.Top5.Best!;
        var fake = new CandidateCardModel
        {
            Rank = 2, SoftTotal = best.SoftTotal, HardViolations = 0,
            Badge = "x", CanAccept = false, Candidate = best.Candidate,
        };
        var outcome = await orch.AcceptAsync(fake);
        Assert.False(outcome.Succeeded);
        Assert.Contains("только лучший", outcome.Message);
        Assert.Equal(0, await store.CountActiveVersionsAsync(year));
    }

    // --- 4. Битый вариант: validator-before-write, отказано ---
    [Fact]
    public async Task AcceptInvalid_RefusedByValidator()
    {
        var (orch, year, store) = await HarnessAsync();
        var best = orch.ViewModel.Top5.Best!;
        var broken = new ScheduleCandidate(
            best.Candidate.Placements.Take(1).ToList(), // неполнота: 1 из 2
            0,
            new PenaltyBreakdown { Total = 0, Components = [] },
            "fp", 11, 0, "", DateTime.UtcNow, "B",
            new Dictionary<Guid, string>());
        var card = new CandidateCardModel
        {
            Rank = 1, SoftTotal = 0, HardViolations = 0,
            Badge = "Лучший", CanAccept = true, Candidate = broken,
        };
        var outcome = await orch.AcceptAsync(card);
        Assert.False(outcome.Succeeded);
        Assert.Contains("жёстких", outcome.Message);
        Assert.Equal(0, await store.CountActiveVersionsAsync(year));
        Assert.Equal("Активной версии нет", orch.ViewModel.ActiveVersionText);
    }

    // --- 5. Без хранилища: честный отказ ---
    [Fact]
    public async Task AcceptWithoutStore_RefusedHonestly()
    {
        var year = Guid.NewGuid();
        StreamingRun run = (p, ct, sink) => Task.FromResult(
            new SolverResult(SolverStatus.Feasible, Valid(p), 0, 1, 10, 5, 0,
                new Dictionary<string, long>(), [], new Dictionary<string, long>()));
        var orch = new GenerationOrchestrator(_ => Tiny(year), run); // без acceptor
        await orch.RunAsync([11]);
        var best = orch.ViewModel.Top5.Best!;
        var outcome = await orch.AcceptAsync(best);
        Assert.False(outcome.Succeeded);
        Assert.Contains("Хранилище не подключено", outcome.Message);
    }

    // --- 6. Путь через событие окна: VM-текст обновляется ---
    [Fact]
    public async Task AcceptViaEvent_UpdatesVmText()
    {
        var (orch, _, _) = await HarnessAsync();
        var best = orch.ViewModel.Top5.Best!;
        orch.ViewModel.RequestAccept(best); // fire-and-forget внутри оркестратора
        for (int i = 0; i < 40 && string.IsNullOrEmpty(orch.ViewModel.AcceptResultText); i++)
            await Task.Delay(50);
        Assert.Contains("Принято", orch.ViewModel.AcceptResultText);
    }
}
