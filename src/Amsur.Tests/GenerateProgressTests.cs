using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// E4 Top-5 UI + Live Progress (§13 ТЗ). Быстрые детерминированные тесты
// (фейк-запуски через StreamingRun-делегат, без solver; тяжёлый solver — только в Benchmark).
public sealed class GenerateProgressTests
{
    private static (SchedulingProblem Problem, List<LessonOccurrence> Occs) Tiny()
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5, StudentCount = 25 };
        var teacher = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var subjects = new[] { "Мат", "Рус", "Анг" }.Select(n => new Subject { Name = n, MaxPerDay = 2 }).ToList();
        var curriculum = subjects.Select(s => new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = s.Id, TeacherId = teacher.Id, HoursPerWeek = 1
        }).ToList();
        var input = new ProblemInput([cls], [teacher], subjects, curriculum,
            [], [], [], DaysCount: 2, SlotsPerDay: 3);
        var (problem, errors) = ProblemBuilder.Build(input,
            new SolverOptions(MaxTimeSeconds: 5, NumSearchWorkers: 1, RandomSeed: 11));
        Assert.Empty(errors);
        return (problem!, problem!.Occurrences.ToList());
    }

    private static List<PlacedLesson> Place(SchedulingProblem problem, params (int Day, int Slot)[] at)
    {
        Assert.Equal(problem.Occurrences.Count, at.Length);
        return problem.Occurrences.Zip(at).Select(p => new PlacedLesson
        {
            OccurrenceId = p.First.Id, DayIndex = p.Second.Day, SlotIndex = p.Second.Slot
        }).ToList();
    }

    private static SolverResult FeasibleResult(IReadOnlyList<PlacedLesson> placements) =>
        new(SolverStatus.Feasible, placements, 0, 1, 10, 5, 0,
            new Dictionary<string, long>(), [], new Dictionary<string, long>());

    // --- §13.1 progress DTO: adapter без CP-SAT типов ---
    [Fact]
    public void ProgressDto_Adapter_MapsIncumbent()
    {
        var (problem, _) = Tiny();
        var placements = Place(problem, (0, 1), (0, 2), (0, 3));
        var inc = new SolverIncumbent(placements, Proxy: 7, Sequence: 3,
            ElapsedMs: 120, Phase: "B", Seed: 11);
        var dto = IncumbentProgressAdapter.Adapt(inc, TimeSpan.FromMilliseconds(120),
            bestSoft: 50, candidateCount: 3, archiveCount: 2, archiveCapacity: 5,
            hasFeasible: true, firstFeasibleAt: TimeSpan.FromSeconds(1),
            status: "Ищем подходящее расписание…", phase: GenerationPhase.Improving);
        Assert.Equal(GenerationPhase.Improving, dto.Phase);
        Assert.Equal(50, dto.BestSoft);
        Assert.Equal(3, dto.CandidateCount);
        Assert.Equal(2, dto.ArchiveCount);
        Assert.True(dto.HasFeasible);
        // DTO не ссылается на CP-SAT: ни одно поле не из Google.OrTools.
        foreach (var p in typeof(GenerationProgressDto).GetProperties())
            Assert.False(p.PropertyType.FullName?.StartsWith("Google.OrTools") == true,
                $"DTO leaks CP-SAT: {p.Name}");
    }

    // --- §13.2 throttling: пачка 100 событий → ≤2 выдачи, последнее состояние живо ---
    [Fact]
    public void Throttling_Burst_EmitsAggregatedOnly()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var stream = new ThrottledIncumbentStream(TimeSpan.FromMilliseconds(250), () => now);
        GenerationProgressDto? last = null;
        stream.ProgressChanged += p => last = p;
        GenerationProgressDto Dto(int n) => new(GenerationPhase.Improving,
            TimeSpan.FromMilliseconds(n), n, null, n, Math.Min(n, 5), 5,
            "seed 11", "Ищем…", true, TimeSpan.FromSeconds(1));
        for (int i = 1; i <= 100; i++) stream.Report(Dto(i));
        Assert.Equal(100, stream.ReceivedCount);
        Assert.Equal(1, stream.EmittedCount); // solver работал на полной частоте, UI — нет
        now += TimeSpan.FromMilliseconds(250);
        stream.Report(Dto(101));
        Assert.Equal(2, stream.EmittedCount);
        stream.Flush(); // финал всегда доставляется
        Assert.Equal(101, last!.CandidateCount); // агрегированное последнее состояние
    }

    // --- §13.3 ordering: карточки отсортированы, лучший первый ---
    [Fact]
    public void CandidateOrdering_BestFirst()
    {
        var (problem, _) = Tiny();
        var variants = new[]
        {
            Place(problem, (0, 1), (0, 2), (0, 3)),
            Place(problem, (0, 1), (0, 2), (1, 1)),
            Place(problem, (0, 1), (1, 1), (1, 2)),
        };
        var archive = new ScheduleCandidateArchive(5, 100);
        foreach (var (v, i) in variants.Select((v, i) => (v, i)))
            Assert.True(archive.TryAdd(ScheduleCandidate.Create(problem, v, 11, i, "B")));
        var softs = archive.Members.Select(m => m.SoftTotal).ToList();
        Assert.Equal(softs.OrderBy(s => s).ToList(), softs);
        var panel = Top5PanelModel.FromArchive(archive);
        Assert.Equal(3, panel.Cards.Count);
        Assert.Equal(panel.Best!.SoftTotal, archive.Members.Min(m => m.SoftTotal));
        Assert.Equal("Лучший", panel.Cards[0].Badge);
    }

    // --- §13.4 small pool: 1 и 3, без пустых карточек и дубликатов ---
    [Fact]
    public void SmallPool_One_HeaderWithoutFiller()
    {
        var (problem, _) = Tiny();
        var archive = new ScheduleCandidateArchive(5, 100);
        archive.TryAdd(ScheduleCandidate.Create(problem, Place(problem, (0, 1), (0, 2), (0, 3)), 11, 5, "B"));
        var panel = Top5PanelModel.FromArchive(archive);
        Assert.Equal("Найден 1 подходящий вариант", panel.HeaderText);
        Assert.Single(panel.Cards); // никаких фальшивых дубликатов ради пятёрки
    }

    [Fact]
    public void SmallPool_Three_HeaderWithoutFiller()
    {
        var (problem, _) = Tiny();
        var archive = new ScheduleCandidateArchive(5, 100);
        archive.TryAdd(ScheduleCandidate.Create(problem, Place(problem, (0, 1), (0, 2), (0, 3)), 11, 5, "B"));
        archive.TryAdd(ScheduleCandidate.Create(problem, Place(problem, (0, 1), (0, 2), (1, 1)), 22, 6, "B"));
        archive.TryAdd(ScheduleCandidate.Create(problem, Place(problem, (0, 1), (1, 1), (1, 2)), 33, 7, "B"));
        var panel = Top5PanelModel.FromArchive(archive);
        Assert.Equal(3, panel.Cards.Count);
        Assert.Equal("Найдено 3 подходящих варианта", panel.HeaderText);
    }

    // --- §13.5 cancel after feasible: best и архив живы, статус честный ---
    [Fact]
    public async Task CancelAfterFeasible_KeepsBestAndArchive()
    {
        using var cts = new CancellationTokenSource();
        StreamingRun run = (p, ct, sink) =>
        {
            // Placements строим от задачи ЭТОГО запуска (OccurrenceId должны совпадать).
            var pl = Place(p, (0, 1), (0, 2), (0, 3));
            sink(new SolverIncumbent(pl, 5, 1, 5, "B", 11));
            cts.Cancel(); // остановка ПОСЛЕ feasible
            return Task.FromResult(FeasibleResult(pl));
        };
        var orch = new GenerationOrchestrator(_ => Tiny().Problem, run);
        var outcome = await orch.RunAsync([11, 22], cts.Token);
        Assert.True(outcome.HasFeasible);
        Assert.NotNull(outcome.BestSoft);
        Assert.True(outcome.ArchiveCount >= 1); // архив в памяти
        Assert.Equal("Генерация остановлена", outcome.Status); // §8
        Assert.Equal("Генерация остановлена", orch.ViewModel.StatusText);
        Assert.NotEmpty(orch.ViewModel.Top5.Cards); // варианты доступны
    }

    // --- §13.6 cancel before feasible: честно пусто, без «невозможно» ---
    [Fact]
    public async Task CancelBeforeFeasible_HonestEmpty()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        StreamingRun run = (p, ct, sink) =>
            Task.FromResult(new SolverResult(SolverStatus.Unknown, [], 0, 0, 5, 0, 0,
                new Dictionary<string, long>(), [], new Dictionary<string, long>(), WasCancelled: true));
        var orch = new GenerationOrchestrator(_ => Tiny().Problem, run);
        var outcome = await orch.RunAsync([11], cts.Token);
        Assert.False(outcome.HasFeasible);
        Assert.Equal("Рабочее расписание не найдено", outcome.Status);
        Assert.DoesNotContain("невозмож", outcome.Status.ToLowerInvariant()); // timeout ≠ infeasible
        Assert.Empty(orch.ViewModel.Top5.Cards);
    }

    // --- §13.7 Top-5 rendering: бейджи, скрытые internals ---
    [Fact]
    public void Top5Rendering_BadgesAndNoInternalsLeak()
    {
        var (problem, _) = Tiny();
        var archive = new ScheduleCandidateArchive(5, 100);
        archive.TryAdd(ScheduleCandidate.Create(problem, Place(problem, (0, 1), (0, 2), (0, 3)), 11, 5, "B"));
        archive.TryAdd(ScheduleCandidate.Create(problem, Place(problem, (0, 1), (0, 2), (1, 1)), 22, 6, "B"));
        var panel = Top5PanelModel.FromArchive(archive);
        Assert.Equal("Лучший", panel.Cards[0].Badge);
        Assert.StartsWith("Отличается на ", panel.Cards[1].Badge);
        Assert.Equal("0 жёстких нарушений", panel.Cards[0].HardText);
        // Карточка не знает про proxy/seed/workers/objective/seq/fingerprint.
        var banned = new[] { "proxy", "seed", "worker", "objective", "sequence", "seq", "fingerprint" };
        foreach (var p in typeof(CandidateCardModel).GetProperties())
            foreach (var b in banned)
                Assert.DoesNotContain(b, p.Name.ToLowerInvariant());
        // Различия — человеческие строки, не raw fingerprint.
        Assert.Contains(panel.Cards[1].DifferenceLines, l => l.Contains("относительно варианта 1"));
        Assert.DoesNotContain(panel.Cards[1].DifferenceLines, l => l.Contains(":"));
    }

    // --- §13.8 no duplicates ---
    [Fact]
    public void NoDuplicateCandidates_SameFingerprintRejectedWhenFull()
    {
        var (problem, _) = Tiny();
        var archive = new ScheduleCandidateArchive(1, 100);
        var v = Place(problem, (0, 1), (0, 2), (0, 3));
        Assert.True(archive.TryAdd(ScheduleCandidate.Create(problem, v, 11, 5, "B")));
        Assert.False(archive.TryAdd(ScheduleCandidate.Create(problem, v, 22, 6, "B")));
        Assert.Single(archive.Members);
    }

    // --- §13.9 correct best: минимум soft, Accept только у лучшего ---
    [Fact]
    public void CorrectBestCandidate_MinSoftAcceptOnlyBest()
    {
        var (problem, _) = Tiny();
        var archive = new ScheduleCandidateArchive(5, 100);
        foreach (var (v, i) in new[]
                 {
                     Place(problem, (0, 1), (0, 2), (0, 3)),
                     Place(problem, (0, 1), (0, 3), (1, 1)),
                     Place(problem, (0, 1), (1, 1), (1, 3)),
                 }.Select((v, i) => (v, i)))
            archive.TryAdd(ScheduleCandidate.Create(problem, v, 11 + i, i, "B"));
        var panel = Top5PanelModel.FromArchive(archive);
        long min = archive.Members.Min(m => m.SoftTotal);
        Assert.Equal(min, panel.Best!.SoftTotal);
        Assert.True(panel.Cards[0].CanAccept);
        Assert.All(panel.Cards.Skip(1), c => Assert.False(c.CanAccept));
    }

    // --- §13.10 StableKey across multi-seed: одинаковые размещения → один fingerprint ---
    [Fact]
    public void StableKey_AcrossMultiSeed_SameFingerprint()
    {
        ProblemInput Input(Guid tag)
        {
            var cls = new SchoolClass { AcademicYearId = tag, Name = "5А", Grade = 5, StudentCount = 25 };
            var teacher = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
            var math = new Subject { Name = "Математика", MaxPerDay = 2 };
            var item = new CurriculumItem
            {
                ClassId = cls.Id, SubjectId = math.Id, TeacherId = teacher.Id, HoursPerWeek = 2
            };
            return new ProblemInput([cls], [teacher], [math], [item],
                [], [], [], DaysCount: 2, SlotsPerDay: 3);
        }
        var (p1, e1) = ProblemBuilder.Build(Input(Guid.NewGuid()));
        var (p2, e2) = ProblemBuilder.Build(Input(Guid.NewGuid()));
        Assert.Empty(e1);
        Assert.Empty(e2);
        // Одинаковый StableKey-паттерн разными Guid: math#hour0→(0,1), math#hour1→(0,2).
        List<PlacedLesson> Pattern(SchedulingProblem p) =>
            p.Occurrences.OrderBy(o => o.StableKey).Select((o, i) => new PlacedLesson
            {
                OccurrenceId = o.Id, DayIndex = 0, SlotIndex = i + 1
            }).ToList();
        var c1 = ScheduleCandidate.Create(p1!, Pattern(p1!), 11, 5, "B");
        var c2 = ScheduleCandidate.Create(p2!, Pattern(p2!), 22, 6, "B");
        Assert.NotNull(c1);
        Assert.NotNull(c2);
        Assert.Equal(c1!.Fingerprint, c2!.Fingerprint); // StableKey, не Guid
        Assert.Equal(0, ScheduleCandidateArchive.Distance(c1, c2));
        var merged = CandidatePoolMerger.Merge([c1, c2]);
        Assert.Equal(1, CandidatePoolMerger.UniqueFingerprints([c1, c2]));
        Assert.Equal(c1.SoftTotal, merged.Members[0].SoftTotal); // best корректен
    }
}
