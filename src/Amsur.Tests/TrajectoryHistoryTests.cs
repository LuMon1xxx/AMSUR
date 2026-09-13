using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;
using Amsur.Scheduling.OrTools;

namespace Amsur.Tests;

// E6 — график «лучшая оценка → время»: только полученные события, без выдумок.
public sealed class TrajectoryHistoryTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static GenerationProgressDto Dto(
        GenerationPhase phase, double seconds, long? best, string run,
        bool feasible = true) =>
        new(phase, TimeSpan.FromSeconds(seconds), best, null, 0, 0, 5, run,
            "Статус", feasible && best.HasValue,
            feasible && best.HasValue ? TimeSpan.FromSeconds(1) : null);

    // --- 1. Накопление: значения сохраняются как есть ---
    [Fact]
    public void Accumulates_ReceivedPoints()
    {
        var h = new TrajectoryHistory();
        h.Append(Dto(GenerationPhase.Preparing, 0, null, "seeds 1,2", false));
        h.Append(Dto(GenerationPhase.SeekingFeasible, 2, null, "run 1/2 (seed 1)", false));
        h.Append(Dto(GenerationPhase.Improving, 5, 300, "run 1/2 (seed 1)"));
        Assert.Equal(3, h.Count);
        var snap = h.Snapshot();
        Assert.True(snap.HasData);
        Assert.False(snap.HasLine); // одно значение — линии нет, и это честно
        Assert.Equal(300, snap.Points[2].BestSoft);
        Assert.True(snap.Points[1].BestSoft is null); // разрыв, не ноль
    }

    // --- 2. Порядок времени: stable sort ---
    [Fact]
    public void Orders_ByElapsedStable()
    {
        var h = new TrajectoryHistory();
        h.Append(Dto(GenerationPhase.Improving, 9, 200, "r1"));
        h.Append(Dto(GenerationPhase.Improving, 3, 300, "r1"));
        h.Append(Dto(GenerationPhase.Improving, 3, 250, "r1")); // то же время — порядок получения
        var pts = h.Snapshot().Points;
        Assert.Equal([3d, 3, 9], pts.Select(p => p.Elapsed.TotalSeconds).ToList());
        Assert.Equal(300, pts[0].BestSoft); // stable: первый полученный — первый
        Assert.Equal(250, pts[1].BestSoft);
    }

    // --- 3. Смена фаз → маркеры человеческими словами ---
    [Fact]
    public void PhaseChange_EmitsHumanMarker()
    {
        var h = new TrajectoryHistory();
        h.Append(Dto(GenerationPhase.Preparing, 0, null, "r", false));
        h.Append(Dto(GenerationPhase.SeekingFeasible, 1, null, "r", false));
        h.Append(Dto(GenerationPhase.Improving, 4, 100, "r"));
        var markers = h.Snapshot().Markers;
        Assert.Contains(markers, m => m.Label == "Поиск рабочего решения");
        Assert.Contains(markers, m => m.Label == "Улучшение");
        Assert.DoesNotContain(markers, m => m.Label == "Подготовка"); // первая точка — не смена
    }

    // --- 4. Несколько запусков: «Запуск N», без seed-чисел ---
    [Fact]
    public void RunChange_EmitsNumberedMarkerWithoutSeeds()
    {
        var h = new TrajectoryHistory();
        h.Append(Dto(GenerationPhase.Improving, 1, 300, "run 1/2 (seed 11)"));
        h.Append(Dto(GenerationPhase.Improving, 6, 250, "run 2/2 (seed 22)"));
        var markers = h.Snapshot().Markers;
        var run = Assert.Single(markers, m => m.Label.StartsWith("Запуск"));
        Assert.Equal("Запуск 2", run.Label);
        Assert.DoesNotContain("11", string.Join("|", markers.Select(m => m.Label)));
        Assert.DoesNotContain("22", string.Join("|", markers.Select(m => m.Label)));
    }

    // --- 5. Пусто: честное пустое состояние ---
    [Fact]
    public void Empty_HonestEmptyState()
    {
        var snap = new TrajectoryHistory().Snapshot();
        Assert.False(snap.HasData);
        Assert.False(snap.HasLine);
        Assert.Empty(snap.Points);
        Assert.Empty(snap.Markers);
    }

    // --- 6. Финал: Done → «Готово», Stopped → «Остановлено» ---
    [Fact]
    public void TerminalFlush_EmitsTerminalMarker()
    {
        var done = new TrajectoryHistory();
        done.Append(Dto(GenerationPhase.Improving, 5, 100, "r"));
        done.Append(Dto(GenerationPhase.Done, 9, 80, "r"));
        var dm = done.Snapshot().Markers;
        Assert.Contains(dm, m => m.Label == "Готово" && m.BestSoft == 80);

        var stopped = new TrajectoryHistory();
        stopped.Append(Dto(GenerationPhase.Improving, 5, 100, "r"));
        stopped.Append(Dto(GenerationPhase.Stopped, 7, 100, "r"));
        Assert.Contains(stopped.Snapshot().Markers, m => m.Label == "Остановлено");
    }

    // --- 7. Словарь маркеров: только разрешённые формулировки ---
    [Fact]
    public void Labels_OnlyHumanVocabulary()
    {
        var h = new TrajectoryHistory();
        h.Append(Dto(GenerationPhase.Preparing, 0, null, "seeds 7,9", false));
        h.Append(Dto(GenerationPhase.SeekingFeasible, 1, null, "run 1/2 (seed 7)", false));
        h.Append(Dto(GenerationPhase.Improving, 3, 400, "run 1/2 (seed 7)"));
        h.Append(Dto(GenerationPhase.Improving, 8, 350, "run 2/2 (seed 9)"));
        h.Append(Dto(GenerationPhase.Done, 10, 350, "run 2/2 (seed 9)"));
        var labels = h.Snapshot().Markers.Select(m => m.Label).ToList();
        Assert.NotEmpty(labels);
        var banned = new[] { "seed", "proxy", "solver", "worker", "objective",
            "cp-sat", "incumbent", "fingerprint", "phase a", "phase b", "%" };
        foreach (var l in labels)
            foreach (var b in banned)
                Assert.DoesNotContain(b, l.ToLowerInvariant());
        var allowed = new HashSet<string>([
            "Подготовка", "Поиск рабочего решения", "Улучшение",
            "Формирование вариантов", "Готово", "Остановлено",
            "Запуск 1", "Запуск 2", "Запуск 3", "Запуск 4", "Запуск 5" ]);
        Assert.All(labels, l => Assert.Contains(l, allowed));
    }

    // --- 8. Интеграция с оркестратором: история + VM из реальных событий ---
    [Fact]
    public async Task Orchestrator_FeedsHistoryAndVm()
    {
        SchedulingProblem BuildTiny()
        {
            var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5, StudentCount = 25 };
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
        // Управляемые часы: события разнесены во времени детерминированно (без Thread.Sleep).
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        StreamingRun run = (p, ct, sink) =>
        {
            now += TimeSpan.FromMilliseconds(10);
            var pl = p.Occurrences.Select((o, i) => new PlacedLesson
            {
                OccurrenceId = o.Id, DayIndex = 0, SlotIndex = i + 1
            }).ToList();
            sink(new SolverIncumbent(pl, 5, 1, 5, "B", 1));
            return Task.FromResult(new SolverResult(SolverStatus.Feasible, pl, 0, 1, 10, 5, 0,
                new Dictionary<string, long>(), [], new Dictionary<string, long>()));
        };
        var stream = new ThrottledIncumbentStream(TimeSpan.FromMilliseconds(5), () => now);
        var orch = new GenerationOrchestrator(_ => { now += TimeSpan.FromMilliseconds(10); return BuildTiny(); }, run, stream);
        var outcome = await orch.RunAsync([11, 22]);
        Assert.Equal("Готово", outcome.Status);
        Assert.True(orch.History.Count >= 2);
        var traj = orch.ViewModel.Trajectory;
        Assert.True(traj.HasData);
        Assert.Contains(traj.Markers, m => m.Label == "Готово");
        Assert.Contains(traj.Markers, m => m.Label.StartsWith("Запуск"));
        // Top-5 E4/E5 жив: карточки + качество на месте.
        Assert.NotEmpty(orch.ViewModel.Top5.Cards);
        Assert.NotEmpty(orch.ViewModel.Top5.Cards[0].QualityLines);
    }

    // --- 9. Perf: 10k событий — пренебрежимо ---
    [Fact]
    public void Append_10k_Negligible()
    {
        var h = new TrajectoryHistory();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 10000; i++)
            h.Append(Dto(GenerationPhase.Improving, i * 0.3, 1000 - i, "r"));
        var snap = h.Snapshot();
        sw.Stop();
        output.WriteLine($"E6-HISTORY 10k append+snapshot: {sw.ElapsedMilliseconds}мс points={snap.Points.Count}");
        Assert.Equal(10000, snap.Points.Count);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"history {sw.ElapsedMilliseconds}мс");
    }

    // --- 10. Smoke: реальный solver → история → VM (продакшн-throttle) ---
    [Fact]
    public async Task RealSolver_Smoke_HistoryEndToEnd()
    {
        ProblemInput Input()
        {
            var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5, StudentCount = 25 };
            var teacher = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
            var math = new Subject { Name = "Математика", MaxPerDay = 2 };
            var item = new CurriculumItem
            {
                ClassId = cls.Id, SubjectId = math.Id, TeacherId = teacher.Id, HoursPerWeek = 2
            };
            return new ProblemInput([cls], [teacher], [math], [item],
                [], [], [], DaysCount: 2, SlotsPerDay: 3);
        }
        var solver = new OrToolsSolver();
        SchedulingProblem Factory(int seed)
        {
            var (p, e) = ProblemBuilder.Build(Input(),
                new SolverOptions(MaxTimeSeconds: 6, NumSearchWorkers: 1, RandomSeed: seed));
            Assert.Empty(e);
            return p!;
        }
        var orch = new GenerationOrchestrator(Factory,
            (problem, ct, sink) => solver.SolveAsync(problem, ct, sink));
        var outcome = await orch.RunAsync([11, 22]);
        Assert.True(outcome.HasFeasible);
        Assert.True(orch.History.Count >= 1);
        var traj = orch.ViewModel.Trajectory;
        Assert.True(traj.HasData);
        Assert.Contains(traj.Markers, m => m.Label == "Готово");
        Assert.NotEmpty(orch.ViewModel.Top5.Cards); // Top-5 E4 жив
        output.WriteLine(
            $"E6-SMOKE: events={orch.History.Count} markers={traj.Markers.Count} " +
            $"hasLine={traj.HasLine} status={outcome.Status}");
    }
}
