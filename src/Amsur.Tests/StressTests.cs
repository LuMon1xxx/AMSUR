using Amsur.Domain;
using Amsur.Scheduling.Core;
using Amsur.Scheduling.OrTools;

namespace Amsur.Tests;

// E2: Pool Stress + Diversity Gate. SPIKE-харнес: измеряет, не гейтит жёстко
// (кроме FullValidator-clean инварианта и archiveCount>=K там, где ожидается).
public sealed class StressTests(Xunit.Abstractions.ITestOutputHelper output)
{
    // Плотность (формула BENCHMARKS): occ / (classes × days × slots).
    private static double Density(int occ, int classes, int days, int slots) =>
        (double)occ / (classes * days * slots);

    private sealed record RunData(
        List<(ScheduleCandidate Cand, long Proxy, int Seq, long Elapsed)> Pool,
        ScheduleCandidateArchive Archive,
        SolverResult Result,
        long OverheadMs,
        long MemBefore, long MemAfter);

    private async Task<RunData> RunAsync(SchedulingProblem problem, int seed, int k = 5)
    {
        long memBefore = GC.GetTotalMemory(forceFullCollection: true);
        var pool = new List<(ScheduleCandidate, long, int, long)>();
        var archive = new ScheduleCandidateArchive(maxSize: k, diversityThreshold: 100);
        var swOver = new System.Diagnostics.Stopwatch();
        long timeToFirst = -1, timeToK = -1;
        var solver = new OrToolsSolver();
        var result = await solver.SolveAsync(problem, default,
            sink: inc =>
            {
                swOver.Start();
                var c = ScheduleCandidate.Create(
                    problem, inc.Placements, seed, inc.Proxy, inc.Phase,
                    stats: $"seed={seed} seq={inc.Sequence}");
                swOver.Stop();
                if (c is null) return;
                pool.Add((c, inc.Proxy, inc.Sequence, inc.ElapsedMs));
                if (archive.TryAdd(c))
                {
                    if (timeToFirst < 0) timeToFirst = inc.ElapsedMs;
                    if (archive.Members.Count >= k && timeToK < 0) timeToK = inc.ElapsedMs;
                }
            });
        long memAfter = GC.GetTotalMemory(forceFullCollection: false);
        output.WriteLine(
            $"RUN seed={seed}: status={result.Status} incumbents={pool.Count} " +
            $"archive={archive.Members.Count}/{k} bestSoft={(pool.Count == 0 ? -1 : pool.Min(p => p.Item1.SoftTotal))} " +
            $"acc={archive.AcceptedCount} rejWorse={archive.RejectedWorseCount} " +
            $"rejSim={archive.RejectedSimilarCount} rejInv={archive.RejectedInvalidCount} " +
            $"tFirst={timeToFirst}ms tK={timeToK}ms overhead={swOver.ElapsedMilliseconds}ms " +
            $"memDelta={(memAfter - memBefore) / 1024}KB total={result.ElapsedMs}ms");
        if (pool.Count > 1)
        {
            var gaps = pool.Zip(pool.Skip(1), (a, b) => b.Item4 - a.Item4).ToList();
            output.WriteLine($"  interTimes ms: [{string.Join(",", gaps)}]");
        }
        return new RunData(pool, archive, result, swOver.ElapsedMilliseconds, memBefore, memAfter);
    }

    private void ReportABC(string tag, List<(ScheduleCandidate Cand, long Proxy, int Seq, long Elapsed)> pool,
        ScheduleCandidateArchive archive)
    {
        var setA = pool.OrderBy(p => p.Cand.SoftTotal).Take(5).Select(p => p.Cand).ToList();
        var setB = pool.OrderBy(p => p.Proxy).Take(5).Select(p => p.Cand).ToList();
        var setC = archive.Members.ToList();
        foreach (var (name, set) in new[] { ("A", setA), ("B", setB), ("C", setC) })
        {
            long sum = 0; int pairs = 0; long min = long.MaxValue;
            int dups = 0, near = 0;
            for (int i = 0; i < set.Count; i++)
                for (int j = i + 1; j < set.Count; j++)
                {
                    long d = ScheduleCandidateArchive.Distance(set[i], set[j]);
                    sum += d; pairs++;
                    if (d < min) min = d;
                    if (d == 0) dups++;
                    if (d < 30) near++;
                }
            var best = set.Count == 0 ? -1 : set.Min(m => m.SoftTotal);
            output.WriteLine(
                $"  {tag}-{name}: n={set.Count} best={best} " +
                $"mean={(set.Count == 0 ? 0 : set.Average(m => m.SoftTotal)):F1} " +
                $"worst={(set.Count == 0 ? -1 : set.Max(m => m.SoftTotal))} " +
                $"pairMean={(pairs == 0 ? 0 : (double)sum / pairs):F1} pairMin={(min == long.MaxValue ? 0 : min)} " +
                $"dups={dups} near={near}");
        }
    }

    // --- E2.1 Dense 83%: 4 класса × 5 предметов × 5ч = 100 occ, 5д × 6сл, 3 каб cap2.
    private static SchedulingProblem Dense(int seed, double budgetSec = 60)
    {
        var classes = Enumerable.Range(0, 4).Select(i => new SchoolClass
        {
            AcademicYearId = Guid.NewGuid(), Name = $"К{i}", Grade = 5, StudentCount = 25
        }).ToList();
        var teachers = Enumerable.Range(0, 5).Select(i => new Teacher
        {
            Name = $"У{i}", MaxLessonsPerDay = 6
        }).ToList();
        var subjects = Enumerable.Range(0, 5).Select(i => new Subject
        {
            Name = $"П{i}", MaxPerDay = 2
        }).ToList();
        var curriculum = new List<CurriculumItem>();
        foreach (var c in classes)
            for (int s = 0; s < 5; s++)
                curriculum.Add(new CurriculumItem
                {
                    ClassId = c.Id, SubjectId = subjects[s].Id,
                    TeacherId = teachers[s].Id, HoursPerWeek = 5
                });
        var rooms = Enumerable.Range(0, 3).Select(i => new Room
        {
            Name = $"R{i}", PhysicalCapacity = 30, MaxSimultaneousGroups = 2
        }).ToList();
        var input = new ProblemInput(classes, teachers, subjects, curriculum,
            [], [], [], DaysCount: 5, SlotsPerDay: 6, rooms: rooms);
        var (problem, errors) = ProblemBuilder.Build(input,
            new SolverOptions(MaxTimeSeconds: budgetSec, NumSearchWorkers: 1, RandomSeed: seed));
        Assert.Empty(errors);
        Assert.Equal(100, problem!.Occurrences.Count);
        return problem;
    }

    [Fact]
    public async Task Dense83_PoolSufficiency()
    {
        output.WriteLine($"DENSE: occ=100 density={Density(100, 4, 5, 6):F2}");
        foreach (int seed in new[] { 11, 22 })
        {
            var problem = Dense(seed);
            var run = await RunAsync(problem, seed);
            Assert.Equal(SolverStatus.Feasible, run.Result.Status);
            output.WriteLine(
                $"  TTFF={run.Result.FirstFeasibleMs}ms bestSoft={run.Pool.Min(p => p.Item1.SoftTotal)} " +
                $"bestPos={run.Pool.MinBy(p => p.Item1.SoftTotal).Item3}/{run.Pool.Count}");
            // K=5 sufficiency — фиксируем факт, не скрываем.
            output.WriteLine($"  SUFFICIENCY archiveCount>=5: {run.Archive.Members.Count >= 5}");
            Assert.All(run.Archive.Members, m => Assert.True(
                PlacementValidator.Validate(problem, m.Placements).IsValid));
            ReportABC($"dense{seed}", run.Pool, run.Archive);
        }
    }

    // --- E2.2 Room-constrained: 3 класса × 2 предмета × 2ч = 12 occ, 2д × 3сл,
    // 2 каб cap1 + Forbidden (Анг в 101 запрещён).
    // D-28: у каждого класса СВОИ учителя (давление — только кабинеты; общее
    // учительское голодание делало бы экземпляр невыполнимым без окон).
    [Fact]
    public async Task RoomConstrained_CleanCandidates()
    {
        var classes = new[] { "5А", "5Б", "6А" }.Select(n => new SchoolClass
        {
            AcademicYearId = Guid.NewGuid(), Name = n, Grade = 5, StudentCount = 24
        }).ToList();
        var teachers = new[] { "Иванов", "Петрова", "Сидоров", "Кузнецов", "Орлов", "Фёдоров" }.Select(n => new Teacher
        {
            Name = n, MaxLessonsPerDay = 6
        }).ToList();
        var subjects = new[] { "Мат", "Анг" }.Select(n => new Subject
        {
            Name = n, MaxPerDay = 2
        }).ToList();
        var curriculum = new List<CurriculumItem>();
        foreach (var (c, ci) in classes.Select((c, i) => (c, i)))
            for (int s = 0; s < 2; s++)
                curriculum.Add(new CurriculumItem
                {
                    ClassId = c.Id, SubjectId = subjects[s].Id,
                    TeacherId = teachers[ci * 2 + s].Id, HoursPerWeek = 2
                });
        var r1 = new Room { Name = "101", PhysicalCapacity = 30, MaxSimultaneousGroups = 1 };
        var r2 = new Room { Name = "102", PhysicalCapacity = 30, MaxSimultaneousGroups = 1 };
        var caps = new List<RoomCapability>
        {
            new() { RoomId = r1.Id, SubjectId = subjects[1].Id, Kind = RoomCapabilityKind.Forbidden }
        };
        var input = new ProblemInput(classes, teachers, subjects, curriculum,
            [], [], [], DaysCount: 2, SlotsPerDay: 3,
            rooms: [r1, r2], roomCaps: caps);
        var (problem, errors) = ProblemBuilder.Build(input,
            new SolverOptions(MaxTimeSeconds: 25, NumSearchWorkers: 1, RandomSeed: 11));
        Assert.Empty(errors);
        output.WriteLine($"ROOM: occ=12 density={Density(12, 3, 2, 3):F2} rooms=2×cap1 + Forbidden(Анг→101)");
        var run = await RunAsync(problem!, 11);
        Assert.Equal(SolverStatus.Feasible, run.Result.Status);
        Assert.All(run.Archive.Members, m => Assert.True(
            PlacementValidator.Validate(problem!, m.Placements).IsValid));
        Assert.All(run.Pool, p => Assert.All(p.Item1.Placements,
            pl => Assert.NotNull(pl.RoomId))); // кабинеты реально назначены
        output.WriteLine($"  SUFFICIENCY archiveCount>=5: {run.Archive.Members.Count >= 5}");
        ReportABC("room", run.Pool, run.Archive);
    }

    // --- E2.4 Near-dup pressure: 2 класса × 3 предмета × 3ч = 18 occ,
    // 3д × 4сл, 2 ИДЕНТИЧНЫХ каб cap2. Room-swap дубликаты: dist кратны 5 (<30).
    [Fact]
    public async Task NearDuplicate_DiversityBranchFires()
    {
        var classes = new[] { "5А", "5Б" }.Select(n => new SchoolClass
        {
            AcademicYearId = Guid.NewGuid(), Name = n, Grade = 5, StudentCount = 20
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
                    TeacherId = teachers[s].Id, HoursPerWeek = 3
                });
        var rooms = new[]
        {
            new Room { Name = "R1", PhysicalCapacity = 30, MaxSimultaneousGroups = 2 },
            new Room { Name = "R2", PhysicalCapacity = 30, MaxSimultaneousGroups = 2 },
        };
        var input = new ProblemInput(classes, teachers, subjects, curriculum,
            [], [], [], DaysCount: 3, SlotsPerDay: 4, rooms: rooms);
        var (problem, errors) = ProblemBuilder.Build(input,
            new SolverOptions(MaxTimeSeconds: 30, NumSearchWorkers: 1, RandomSeed: 11));
        Assert.Empty(errors);
        var run = await RunAsync(problem!, 11);
        Assert.Equal(SolverStatus.Feasible, run.Result.Status);
        output.WriteLine($"NEARDUP: pool={run.Pool.Count} rejSim={run.Archive.RejectedSimilarCount}");
        // Наблюдение E2: solver-поток на этой размерности даёт pool≤K → ветка diversity
        // через solver не активируется (давление создано synthetic-тестом ниже).
        output.WriteLine($"  BRANCH-FIRED-VIA-SOLVER: {run.Archive.RejectedSimilarCount > 0} (pool<=K expected)");
        ReportABC("neardup", run.Pool, run.Archive);
    }

    // --- E2.4b Synthetic pressure (детерминированный, без solver):
    // 8 кандидатов: exact dup + room-swap near-dups (dist 5–15) + distant.
    [Fact]
    public void SyntheticPressure_DiversityBranchFires()
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5, StudentCount = 20 };
        var t = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var s = new Subject { Name = "Мат", MaxPerDay = 2 };
        var item = new CurriculumItem { ClassId = cls.Id, SubjectId = s.Id, TeacherId = t.Id, HoursPerWeek = 2 };
        var r1 = new Room { Name = "R1", PhysicalCapacity = 30, MaxSimultaneousGroups = 2 };
        var r2 = new Room { Name = "R2", PhysicalCapacity = 30, MaxSimultaneousGroups = 2 };
        var input = new ProblemInput([cls], [t], [s], [item], [], [], [],
            DaysCount: 2, SlotsPerDay: 3, rooms: [r1, r2]);
        var (problem, errors) = ProblemBuilder.Build(input);
        Assert.Empty(errors);
        var occ = problem!.Occurrences;
        List<PlacedLesson> Pl(int d0, int s0, Guid? r0, int d1, int s1, Guid? r2_) =>
        [
            new() { OccurrenceId = occ[0].Id, DayIndex = d0, SlotIndex = s0, RoomId = r0 },
            new() { OccurrenceId = occ[1].Id, DayIndex = d1, SlotIndex = s1, RoomId = r2_ },
        ];
        var fed = new List<List<PlacedLesson>>
        {
            Pl(0, 1, r1.Id, 0, 2, r1.Id), // A base, soft 0
            Pl(0, 1, r2.Id, 0, 2, r1.Id), // near: 1 room swap, dist 5
            Pl(0, 1, r1.Id, 0, 2, r2.Id), // near: dist 5
            Pl(0, 1, r1.Id, 0, 2, r1.Id), // exact dup, dist 0
            Pl(0, 2, r1.Id, 0, 3, r1.Id), // time var, clean (старт со 2-го), dist 30
            Pl(1, 1, r1.Id, 1, 2, r1.Id), // distant day1, soft 0
            Pl(1, 1, r2.Id, 1, 2, r2.Id), // distant rooms, soft 0
            Pl(0, 2, r2.Id, 0, 1, r2.Id), // near swap of A-times, soft 0, dist 10
        };
        var archive = new ScheduleCandidateArchive(maxSize: 5, diversityThreshold: 100);
        int fed_valid = 0;
        foreach (var pl in fed)
        {
            var c = ScheduleCandidate.Create(problem, pl, seed: 1, proxy: 0, phase: "synthetic");
            Assert.NotNull(c); // все валидны — давление именно quality/diversity
            fed_valid++;
            archive.TryAdd(c);
        }
        Assert.Equal(8, fed_valid);
        output.WriteLine(
            $"SYNTH: fed=8 archive={archive.Members.Count} " +
            $"rejWorse={archive.RejectedWorseCount} rejSim={archive.RejectedSimilarCount}");
        Assert.True(archive.RejectedSimilarCount > 0,
            "Diversity branch never fired under synthetic near-dup pressure.");
        Assert.True(archive.Members.Count <= 5);
        // C отличается от pure-quality A: near-dups отфильтрованы.
        var all = fed.Select(pl => ScheduleCandidate.Create(problem, pl, 1, 0, "s")!).ToList();
        var setA = all.OrderBy(c => c.SoftTotal).Take(5).ToList();
        long minA = long.MaxValue, minC = long.MaxValue;
        for (int i = 0; i < setA.Count; i++)
            for (int j = i + 1; j < setA.Count; j++)
                minA = Math.Min(minA, ScheduleCandidateArchive.Distance(setA[i], setA[j]));
        var setC = archive.Members.ToList();
        for (int i = 0; i < setC.Count; i++)
            for (int j = i + 1; j < setC.Count; j++)
                minC = Math.Min(minC, ScheduleCandidateArchive.Distance(setC[i], setC[j]));
        output.WriteLine($"  SYNTH A-pureQuality minDist={minA}; C-archive minDist={minC}");
        // Измерение E2 (не превосходство): порядок заполнения удерживает ранние near-dups
        // (dup принят 4-м при пустом архиве) — uplift C над A в этом сценарии отсутствует.
        // Order-dependence зафиксирована как finding для вердикта (FIX_ARCHIVE-кандидат).
        output.WriteLine($"  SYNTH-UPLIFT: {minC > minA} (minC={minC} minA={minA})");
    }

    // --- E2.6 Seed diversity: траектории fingerprint/proxy/soft по seeds.
    [Fact]
    public async Task SeedDiversity_Trajectories()
    {
        var fps = new Dictionary<int, string>();
        foreach (int seed in new[] { 11, 22, 33 })
        {
            var problem = Dense(seed, budgetSec: 30);
            var run = await RunAsync(problem, seed);
            var best = run.Pool.MinBy(p => p.Item1.SoftTotal);
            fps[seed] = best.Item1.Fingerprint;
            output.WriteLine(
                $"SEED seed={seed}: n={run.Pool.Count} " +
                $"proxies=[{string.Join(",", run.Pool.Select(p => p.Proxy))}] " +
                $"softs=[{string.Join(",", run.Pool.Select(p => p.Item1.SoftTotal))}] " +
                $"bestFp={best.Item1.Fingerprint[..32]}...");
        }
        bool allSameBest = fps.Values.Distinct().Count() == 1;
        output.WriteLine($"SEEDDIV: identicalBestAcrossSeeds={allSameBest}");
    }
}
