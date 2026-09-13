using Amsur.Domain;
using Amsur.Scheduling.Core;
using Amsur.Scheduling.OrTools;

namespace Amsur.Tests;

// E1.3–E1.4: pool quality + diversity. SPIKE-харнес: измеряет, не гейтит.
// Фикстура 18 occ (3 класса × 3 предмета × 2ч), availability-натяжения, 2 кабинета.
public sealed class ArchiveExperimentTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static SchedulingProblem Build(int seed)
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
        for (int day = 0; day < 3; day++)
            unav.Add(new TeacherUnavailability
            {
                TeacherId = teachers[1].Id, DayIndex = day, SlotIndex = 4,
                Kind = AvailabilityKind.Forbidden
            });
        unav.Add(new TeacherUnavailability
        {
            TeacherId = teachers[2].Id, DayIndex = 0, SlotIndex = 1,
            Kind = AvailabilityKind.Forbidden
        });
        var rooms = new[]
        {
            new Room { Name = "101", PhysicalCapacity = 30, MaxSimultaneousGroups = 2 },
            new Room { Name = "102", PhysicalCapacity = 30, MaxSimultaneousGroups = 2 },
        };
        var input = new ProblemInput(classes, teachers, subjects, curriculum,
            [], [], unav, DaysCount: 3, SlotsPerDay: 4, rooms: rooms);
        var (problem, errors) = ProblemBuilder.Build(input,
            new SolverOptions(MaxTimeSeconds: 25, NumSearchWorkers: 1, RandomSeed: seed));
        Assert.Empty(errors);
        Assert.Equal(18, problem!.Occurrences.Count);
        return problem;
    }

    private static (double Mean, long Min) Pairwise(IReadOnlyList<ScheduleCandidate> set)
    {
        if (set.Count < 2) return (0, 0);
        long sum = 0; int pairs = 0; long min = long.MaxValue;
        for (int i = 0; i < set.Count; i++)
            for (int j = i + 1; j < set.Count; j++)
            {
                long d = ScheduleCandidateArchive.Distance(set[i], set[j]);
                sum += d; pairs++;
                if (d < min) min = d;
            }
        return (pairs == 0 ? 0 : (double)sum / pairs, min == long.MaxValue ? 0 : min);
    }

    private static double DupRate(IReadOnlyList<ScheduleCandidate> set, long below)
    {
        int pairs = 0, hits = 0;
        for (int i = 0; i < set.Count; i++)
            for (int j = i + 1; j < set.Count; j++)
            {
                pairs++;
                if (ScheduleCandidateArchive.Distance(set[i], set[j]) < below) hits++;
            }
        return pairs == 0 ? 0 : (double)hits / pairs;
    }

    private void ReportSet(string name, IReadOnlyList<ScheduleCandidate> set)
    {
        var (mean, min) = Pairwise(set);
        var best = set.Min(m => m.SoftTotal);
        var worst = set.Max(m => m.SoftTotal);
        var avg = set.Average(m => m.SoftTotal);
        int diffFromBest = 0;
        var top = set.MinBy(m => m.SoftTotal)!;
        foreach (var m in set)
            if (!ReferenceEquals(m, top) && ScheduleCandidateArchive.Distance(top, m) > 0)
                diffFromBest++;
        output.WriteLine(
            $"SET {name}: n={set.Count} soft best={best} mean={avg:F1} worst={worst} " +
            $"pairwise mean={mean:F1} min={min} dupRate={DupRate(set, 1):F2} " +
            $"nearDupRate(<30)={DupRate(set, 30):F2} diffFromBest={diffFromBest}");
    }

    [Fact]
    public async Task PoolQuality_Diversity_AcrossSeeds()
    {
        foreach (int seed in new[] { 11, 22, 33 })
        {
            var problem = Build(seed);
            var pool = new List<(ScheduleCandidate Cand, long Proxy, int Seq)>();
            var swOver = new System.Diagnostics.Stopwatch();
            var solver = new OrToolsSolver();
            var result = await solver.SolveAsync(problem, default,
                sink: inc =>
                {
                    swOver.Start();
                    var c = ScheduleCandidate.Create(
                        problem, inc.Placements, seed, inc.Proxy, inc.Phase,
                        stats: $"seed={seed} seq={inc.Sequence}");
                    swOver.Stop();
                    if (c is not null) pool.Add((c, inc.Proxy, inc.Sequence));
                });
            Assert.Equal(SolverStatus.Feasible, result.Status);

            long bestProxy = pool.Min(p => p.Proxy);
            var bestSoft = pool.MinBy(p => p.Cand.SoftTotal);
            output.WriteLine(
                $"POOL seed={seed}: incumbents={pool.Count} " +
                $"bestProxy={bestProxy} bestSoft={bestSoft.Cand.SoftTotal} " +
                $"bestSoftPos={bestSoft.Seq}/{pool.Count} " +
                $"softs=[{string.Join(",", pool.Select(p => p.Cand.SoftTotal))}] " +
                $"proxies=[{string.Join(",", pool.Select(p => p.Proxy))}] " +
                $"archiveOverhead={swOver.ElapsedMilliseconds}ms total={result.ElapsedMs}ms");

            // Набор A: топ-5 по soft. Набор B: топ-5 по proxy.
            var setA = pool.OrderBy(p => p.Cand.SoftTotal).Take(5).Select(p => p.Cand).ToList();
            var setB = pool.OrderBy(p => p.Proxy).Take(5).Select(p => p.Cand).ToList();
            // Набор C: quality + diversity filtering в порядке посещения.
            var archive = new ScheduleCandidateArchive(maxSize: 5, diversityThreshold: 100);
            foreach (var p in pool) archive.TryAdd(p.Cand);
            var setC = archive.Members.ToList();
            Assert.True(setC.Count is >= 1 and <= 5);
            Assert.All(setC, m => Assert.True(
                PlacementValidator.Validate(problem, m.Placements).IsValid));

            ReportSet($"A-soft seed={seed}", setA);
            ReportSet($"B-proxy seed={seed}", setB);
            ReportSet($"C-arch seed={seed}", setC);
        }
    }
}
