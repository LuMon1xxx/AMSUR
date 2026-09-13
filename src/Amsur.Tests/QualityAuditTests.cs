using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// EPIC-H QUALITY OPTIMIZATION — измерительный харнес (H1–H4).
// Запуск: AMSUR_EPICH=1 dotnet test --filter QualityAuditTests
// Без флага тесты молча пропускаются (долгие LS-прогоны не для CI-гейта).
// Методология: Greedy baseline + LocalSearch напрямую (без CP-SAT), т.к. на
// масштабе ≥388 occ CP-SAT B даёт 0 инкумбентов (D-24) — LS и есть движок качества.
public sealed class QualityAuditTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private bool Enabled()
    {
        if (Environment.GetEnvironmentVariable("AMSUR_EPICH") == "1") return true;
        output.WriteLine("SKIPPED: set AMSUR_EPICH=1 to run EPIC-H measurements.");
        return false;
    }

    private static List<PlacedLesson> GreedyStart(SchedulingProblem problem, int seed = 11)
    {
        var greedy = GreedyPlacer.Place(problem, seed);
        Assert.Empty(greedy.Unplaced);
        return greedy.Placed.Select(kv => new PlacedLesson
        {
            OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
            SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
        }).ToList();
    }

    private void ReportBreakdown(string tag, SchedulingProblem problem, IReadOnlyList<PlacedLesson> placements)
    {
        var occById = problem.Occurrences.ToDictionary(o => o.Id);
        var bd = SoftEvaluator.Evaluate(problem, placements);
        var units = new Dictionary<string, long>();
        foreach (var c in bd.Components)
        {
            long w = c.Code switch
            {
                "student-gap" => RuleCatalog.StudentGap,
                "teacher-gap" => RuleCatalog.TeacherGap,
                "subject-maxperday" => RuleCatalog.SubjectMaxPerDay,
                _ => 0
            };
            units[c.Code] = w == 0 ? c.Value : c.Value / w;
        }
        output.WriteLine($"{tag}: total={bd.Total} " +
            string.Join(" ", bd.Components.Select(c => $"{c.Code}={c.Value}(u={units[c.Code]})")));

        var sGaps = placements.GroupBy(p => occById[p.OccurrenceId].ClassId)
            .SelectMany(g => g.GroupBy(p => p.DayIndex).Select(d =>
            {
                var s = d.Select(p => p.SlotIndex).OrderBy(x => x).ToList();
                return (Class: g.Key, Day: d.Key, Gap: s.Count <= 1 ? 0 : (s[^1] - s[0] + 1) - s.Count, Cnt: s.Count);
            }))
            .Where(x => x.Gap > 0).OrderByDescending(x => x.Gap).Take(5).ToList();
        output.WriteLine($"  worst class-days: {string.Join("; ", sGaps.Select(x =>
            $"{problem.Classes[x.Class].Name} d{x.Day + 1} gap={x.Gap} lessons={x.Cnt}"))}");

        var tGaps = placements.GroupBy(p => occById[p.OccurrenceId].TeacherId)
            .SelectMany(g => g.GroupBy(p => p.DayIndex).Select(d =>
            {
                var s = d.Select(p => p.SlotIndex).OrderBy(x => x).ToList();
                return (Teacher: g.Key, Day: d.Key, Gap: s.Count <= 1 ? 0 : (s[^1] - s[0] + 1) - s.Count, Cnt: s.Count);
            }))
            .Where(x => x.Gap > 0).OrderByDescending(x => x.Gap).Take(5).ToList();
        output.WriteLine($"  worst teacher-days: {string.Join("; ", tGaps.Select(x =>
            $"{problem.Teachers[x.Teacher].Name} d{x.Day + 1} gap={x.Gap} lessons={x.Cnt}"))}");

        int classDays = placements.GroupBy(p => (occById[p.OccurrenceId].ClassId, p.DayIndex)).Count();
        int gappedClassDays = placements.GroupBy(p => (occById[p.OccurrenceId].ClassId, p.DayIndex))
            .Count(g =>
            {
                var s = g.Select(p => p.SlotIndex).OrderBy(x => x).ToList();
                return s.Count > 1 && (s[^1] - s[0] + 1) - s.Count > 0;
            });
        output.WriteLine($"  class-days total={classDays} gapped={gappedClassDays}");
    }

    private void ReportAudit(string tag, LocalSearch.Audit audit, LocalSearch.ImproveResult res)
    {
        double acceptRate = audit.Attempts == 0 ? 0 : (double)res.AcceptedMoves / audit.Attempts * 100;
        long lastImproveMs = audit.Trajectory.Count <= 1 ? 0 : audit.Trajectory[^2].ElapsedMs;
        output.WriteLine($"{tag}: soft={res.SoftTotal} accepted={res.AcceptedMoves} " +
            $"attempts={audit.Attempts} forbidden={audit.Forbidden} nonImproving={audit.NonImproving} " +
            $"acceptRate={acceptRate:F2}% sweeps={audit.Sweeps} " +
            $"perSweep=[{string.Join(",", audit.AcceptedPerSweep)}] " +
            $"evalMs={audit.EvalMs}/{res.ElapsedMs} lastImproveMs={lastImproveMs}");
        output.WriteLine($"  swaps: attempts={audit.SwapAttempts} accepted={audit.SwapAccepted} " +
            $"perSweep=[{string.Join(",", audit.SwapAcceptedPerSweep)}] evalMs={audit.SwapEvalMs}");
        if (audit.Trajectory.Count > 2)
        {
            long start = audit.Trajectory[0].Soft, end = audit.Trajectory[^1].Soft;
            var marks = new[] { 1, 5, 10, 30, 60, 120, 180, 300 }
                .Select(m => audit.Trajectory.LastOrDefault(t => t.ElapsedMs <= m * 1000))
                .Select(t => t == default ? "?" : t.Soft.ToString());
            output.WriteLine($"  trajectory 1/5/10/30/60/120/180/300s: [{string.Join(",", marks)}] (from {start} to {end})");
        }
    }

    // H1: breakdown greedy vs greedy+LS на RealSchool.
    [Fact]
    public void H1_QualityBreakdown()
    {
        if (!Enabled()) return;
        var problem = RealSchoolStressTests.BuildRealSchool(budget: 60);
        output.WriteLine($"H1: occ={problem.Occurrences.Count} classes={problem.Classes.Count} " +
            $"teachers={problem.Teachers.Count} grid={problem.DaysCount}x{problem.SlotsPerDay}");
        var greedy = GreedyStart(problem);
        ReportBreakdown("H1 greedy", problem, greedy);
        var res = LocalSearch.Improve(problem, greedy, TimeSpan.FromSeconds(60), seed: 11);
        ReportBreakdown("H1 greedy+LS60s", problem, res.Placements);
        output.WriteLine($"H1: accepted={res.AcceptedMoves} elapsedMs={res.ElapsedMs}");
        Assert.True(PlacementValidator.Validate(problem, res.Placements).IsValid);
    }

    // H2: аудит механики LS (попытки/отказы/тайминги/плато).
    [Fact]
    public void H2_MoveAudit()
    {
        if (!Enabled()) return;
        var problem = RealSchoolStressTests.BuildRealSchool(budget: 60);
        var greedy = GreedyStart(problem);
        var audit = new LocalSearch.Audit();
        var res = LocalSearch.Improve(problem, greedy, TimeSpan.FromSeconds(30), seed: 11, audit: audit);
        ReportAudit("H2 LS30s", audit, res);
        Assert.True(PlacementValidator.Validate(problem, res.Placements).IsValid);
    }

    // H3: кривая качества от бюджета (одна fixture, один seed).
    [Fact]
    public void H3_TimeBudgetCurve()
    {
        if (!Enabled()) return;
        var problem = RealSchoolStressTests.BuildRealSchool(budget: 300);
        output.WriteLine($"H3: occ={problem.Occurrences.Count}");
        var greedy = GreedyStart(problem);
        long greedySoft = SoftEvaluator.Evaluate(problem, greedy).Total;
        output.WriteLine($"H3 greedy: soft={greedySoft}");
        foreach (int sec in new[] { 10, 30, 60, 90, 180, 300 })
        {
            long memBefore = GC.GetTotalMemory(true);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = LocalSearch.Improve(problem, greedy, TimeSpan.FromSeconds(sec), seed: 11);
            sw.Stop();
            long memAfter = GC.GetTotalMemory(false);
            long peakMb = System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1048576;
            output.WriteLine($"H3 budget={sec}s: soft={res.SoftTotal} accepted={res.AcceptedMoves} " +
                $"wallMs={sw.ElapsedMilliseconds} hard=0 " +
                $"memDeltaMb={(memAfter - memBefore) / 1048576} peakMb={peakMb}");
            Assert.True(PlacementValidator.Validate(problem, res.Placements).IsValid);
        }
    }

    // H5: плато VND на длинном бюджете (сходимость — ранний выход).
    [Fact]
    public void H5_Plateau300s()
    {
        if (!Enabled()) return;
        var problem = RealSchoolStressTests.BuildRealSchool(budget: 300);
        var greedy = GreedyStart(problem);
        var audit = new LocalSearch.Audit();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = LocalSearch.Improve(problem, greedy, TimeSpan.FromSeconds(300), seed: 11, audit: audit);
        sw.Stop();
        ReportAudit("H5 VND300s", audit, res);
        ReportBreakdown("H5 final", problem, res.Placements);
        output.WriteLine($"H5: wallMs={sw.ElapsedMilliseconds}");
        Assert.True(PlacementValidator.Validate(problem, res.Placements).IsValid);
    }

    // H5b: влияет ли seed жадного старта на финал VND (тот же LS-seed)?
    [Fact]
    public void H5_GreedySeeds()
    {
        if (!Enabled()) return;
        var problem = RealSchoolStressTests.BuildRealSchool(budget: 60);
        foreach (int gseed in new[] { 11, 22, 33 })
        {
            var greedy = GreedyStart(problem, gseed);
            long gs = SoftEvaluator.Evaluate(problem, greedy).Total;
            var res = LocalSearch.Improve(problem, greedy, TimeSpan.FromSeconds(60), seed: 11);
            output.WriteLine($"H5b greedySeed={gseed}: greedySoft={gs} final={res.SoftTotal} " +
                $"accepted={res.AcceptedMoves} wallMs={res.ElapsedMs}");
            Assert.True(PlacementValidator.Validate(problem, res.Placements).IsValid);
        }
    }

    // H4: стабильность от seed (одна fixture, один бюджет, workers=1 — LS однопоточен).
    [Fact]
    public void H4_SeedStability()
    {
        if (!Enabled()) return;
        var problem = RealSchoolStressTests.BuildRealSchool(budget: 60);
        var greedy = GreedyStart(problem);
        var softs = new List<(int Seed, long Soft, int Accepted)>();
        foreach (int seed in new[] { 11, 22, 33, 42, 7 })
        {
            var res = LocalSearch.Improve(problem, greedy, TimeSpan.FromSeconds(60), seed: seed);
            softs.Add((seed, res.SoftTotal, res.AcceptedMoves));
            output.WriteLine($"H4 seed={seed}: soft={res.SoftTotal} accepted={res.AcceptedMoves}");
            Assert.True(PlacementValidator.Validate(problem, res.Placements).IsValid);
        }
        var vals = softs.Select(x => x.Soft).OrderBy(x => x).ToList();
        output.WriteLine($"H4 summary: best={vals.First()} median={vals[vals.Count / 2]} " +
            $"worst={vals.Last()} spread={vals.Last() - vals.First()}");
    }
}
