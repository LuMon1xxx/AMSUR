using Amsur.Domain;
using Amsur.Scheduling.Core;
using Amsur.Scheduling.OrTools;

namespace Amsur.Tests;

// E3: enumeration A/B/C + perturbation + budgets. SPIKE-харнес (медленный).
public sealed class ExpansionTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private sealed record Collected(
        List<ScheduleCandidate> Pool, SolverResult Result, long OverheadMs);

    private static SchedulingProblem Build18(int seed, double budget,
        IReadOnlyDictionary<string, IReadOnlySet<(int Day, int Slot)>>? excluded = null)
    {
        var classes = new[] { "5А", "5Б", "6А" }.Select(n => new SchoolClass
        {
            AcademicYearId = Guid.NewGuid(), Name = n, Grade = 5, StudentCount = 25
        }).ToList();
        var teachers = new[] { "Иванов", "Петрова", "Сидоров" }.Select(n => new Teacher
        {
            Name = n, MaxLessonsPerDay = 6
        }).ToList();
        var subjects = new[] { "Мат", "Рус", "Анг" }.Select(n => new Subject
        {
            Name = n, MaxPerDay = 2
        }).ToList();
        var curriculum = new List<CurriculumItem>();
        foreach (var c in classes)
            for (int s = 0; s < 3; s++)
                curriculum.Add(new CurriculumItem
                {
                    ClassId = c.Id, SubjectId = subjects[s].Id,
                    TeacherId = teachers[s].Id, HoursPerWeek = 2
                });
        var unav = new List<TeacherUnavailability>();
        for (int slot = 1; slot <= 4; slot++)
            unav.Add(new TeacherUnavailability
            {
                TeacherId = teachers[0].Id, DayIndex = 2, SlotIndex = slot,
                Kind = AvailabilityKind.Forbidden
            });
        var rooms = new[]
        {
            new Room { Name = "101", PhysicalCapacity = 30, MaxSimultaneousGroups = 2 },
            new Room { Name = "102", PhysicalCapacity = 30, MaxSimultaneousGroups = 2 },
        };
        var input = new ProblemInput(classes, teachers, subjects, curriculum,
            [], [], unav, DaysCount: 3, SlotsPerDay: 4, rooms: rooms,
            excludedPairs: excluded);
        var (problem, errors) = ProblemBuilder.Build(input,
            new SolverOptions(MaxTimeSeconds: budget, NumSearchWorkers: 1, RandomSeed: seed));
        Assert.Empty(errors);
        return problem!;
    }

    private async Task<Collected> CollectAsync(SchedulingProblem problem, int seed)
    {
        var pool = new List<ScheduleCandidate>();
        var sw = new System.Diagnostics.Stopwatch();
        var result = await new OrToolsSolver().SolveAsync(problem, default,
            sink: inc =>
            {
                sw.Start();
                var c = ScheduleCandidate.Create(
                    problem, inc.Placements, seed, inc.Proxy, inc.Phase);
                sw.Stop();
                if (c is not null) pool.Add(c);
            });
        return new Collected(pool, result, sw.ElapsedMilliseconds);
    }

    private void ReportPool(string tag, List<ScheduleCandidate> pool, SolverResult result)
    {
        int unique = CandidatePoolMerger.UniqueFingerprints(pool);
        var verdict = PoolDiagnostics.Summarize(
            result.Status, result.OptimalProven, unique, 5);
        long tK = -1;
        var arch = new ScheduleCandidateArchive(5, 100);
        // time-to-K аппроксимируем порядком пула (sink ElapsedMs недоступен здесь).
        foreach (var c in pool)
        {
            arch.TryAdd(c);
            if (arch.Members.Count >= 5 && tK < 0) tK = 0; // порядок, не время
        }
        output.WriteLine(
            $"{tag}: pool={pool.Count} unique={unique} " +
            $"best={(pool.Count == 0 ? -1 : pool.Min(c => c.SoftTotal))} " +
            $"mean={(pool.Count == 0 ? 0 : pool.Average(c => c.SoftTotal)):F1} " +
            $"worst={(pool.Count == 0 ? -1 : pool.Max(c => c.SoftTotal))} " +
            $"optimalProven={result.OptimalProven} verdict={verdict} " +
            $"archN={arch.Members.Count} wall={result.ElapsedMs}ms");
    }

    // --- E3.2 room-tight A/B/C: single vs 3 seeds vs merge.
    [Fact]
    public async Task RoomTight_Single_vs_MultiSeed_vs_Merged()
    {
        var pools = new List<ScheduleCandidate>();
        SolverResult? first = null;
        foreach (int seed in new[] { 11, 22, 33 })
        {
            var clsA = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5, StudentCount = 25 };
            var clsB = new SchoolClass { AcademicYearId = clsA.AcademicYearId, Name = "5Б", Grade = 5, StudentCount = 25 };
            var tA = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
            var tB = new Teacher { Name = "Петрова", MaxLessonsPerDay = 6 };
            var math = new Subject { Name = "Математика", MaxPerDay = 2 };
            var ia = new CurriculumItem { ClassId = clsA.Id, SubjectId = math.Id, TeacherId = tA.Id, HoursPerWeek = 2 };
            var ib = new CurriculumItem { ClassId = clsB.Id, SubjectId = math.Id, TeacherId = tB.Id, HoursPerWeek = 2 };
            var room = new Room { Name = "101", PhysicalCapacity = 30, MaxSimultaneousGroups = 1 };
            var room2 = new Room { Name = "102", PhysicalCapacity = 30, MaxSimultaneousGroups = 1 };
            var input = new ProblemInput([clsA, clsB], [tA, tB], [math], [ia, ib],
                [], [], [], DaysCount: 2, SlotsPerDay: 2, rooms: [room, room2]);
            var (problem, errors) = ProblemBuilder.Build(input,
                new SolverOptions(MaxTimeSeconds: 15, NumSearchWorkers: 1, RandomSeed: seed));
            Assert.Empty(errors);
            var run = await CollectAsync(problem!, seed);
            if (first is null) first = run.Result;
            output.WriteLine($"ROOMTIGHT-A seed={seed}: pool={run.Pool.Count} " +
                $"best={(run.Pool.Count == 0 ? -1 : run.Pool.Min(c => c.SoftTotal))}");
            pools.AddRange(run.Pool);
        }
        int unique = CandidatePoolMerger.UniqueFingerprints(pools);
        var merged = CandidatePoolMerger.Merge(pools);
        output.WriteLine($"ROOMTIGHT-B multiseed: total={pools.Count} unique={unique}");
        output.WriteLine($"ROOMTIGHT-C merged: n={merged.Members.Count} " +
            $"best={merged.Members.Min(m => m.SoftTotal)} " +
            $"verdict={PoolDiagnostics.Summarize(first!.Status, first.OptimalProven, unique, 5)}");
    }

    // --- E3.4 budgets 10/30/60 на 18-occ: TTFF / time-to-K / best / pool.
    [Fact]
    public async Task Budgets_10_30_60()
    {
        foreach (double budget in new[] { 10.0, 30.0, 60.0 })
        {
            var problem = Build18(seed: 11, budget: budget);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var run = await CollectAsync(problem, 11);
            sw.Stop();
            long tK = -1;
            var arch = new ScheduleCandidateArchive(5, 100);
            foreach (var c in run.Pool)
            {
                arch.TryAdd(c);
                if (arch.Members.Count >= 5) break;
            }
            // time-to-K через порядок недостижим без меток — фиксируем wall и факт.
            output.WriteLine(
                $"BUDGET {budget}s: TTFF={run.Result.FirstFeasibleMs}ms pool={run.Pool.Count} " +
                $"archN={arch.Members.Count} best={run.Pool.Min(c => c.SoftTotal)} " +
                $"wall={sw.ElapsedMilliseconds}ms optimal={run.Result.OptimalProven}");
        }
    }

    // --- E3.3 perturbation vs чистый multi-seed.
    [Fact]
    public async Task Perturbation_vs_MultiSeed()
    {
        var base18 = Build18(seed: 11, budget: 25);
        var run1 = await CollectAsync(base18, 11);
        var best1 = run1.Pool.MinBy(c => c.SoftTotal)!;
        // Чистый multi-seed: второй запуск, другой seed, без банов.
        var clean2 = Build18(seed: 22, budget: 25);
        var run2 = await CollectAsync(clean2, 22);
        // Perturbation: баним половину placements лучшего (по StableKey), новый seed.
        var banKeys = best1.Placements
            .Take(best1.Placements.Count / 2)
            .Select(p => best1.OccKeys[p.OccurrenceId])
            .ToList();
        var excluded = new Dictionary<string, IReadOnlySet<(int Day, int Slot)>>();
        foreach (var key in banKeys)
        {
            var pl = best1.Placements.First(p => best1.OccKeys[p.OccurrenceId] == key);
            excluded[key] = new HashSet<(int Day, int Slot)> { (pl.DayIndex, pl.SlotIndex) };
        }
        var pert = Build18(seed: 33, budget: 25, excluded: excluded);
        var run3 = await CollectAsync(pert, 33);

        ReportPool("PERT-BASE", run1.Pool, run1.Result);
        ReportPool("PERT-CLEAN-SEED22", run2.Pool, run2.Result);
        ReportPool("PERT-BANNED-SEED33", run3.Pool, run3.Result);
        // Perturbation обязан дать решение, отличное от забаненного best1.
        bool differs = run3.Pool.Any(c =>
            ScheduleCandidateArchive.Distance(best1, c) > 0);
        output.WriteLine($"PERT-DIFFERS-FROM-BEST1: {differs}");
        Assert.True(differs);
    }
}
