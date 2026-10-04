using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// GRIND до сходимости на финале живой школы (04.10.2026, по команде «гони до победы»):
// цель — побить оригинал (140 окон тем же аудитом: дырки между крайними).
// Раунды (LS90 + Thin20 + TeacherDay30 + CrossShift15 + Swap15 + repair + PeRepair)
// до первого раунда без улучшений или бюджета 25 мин. Лучшее пишется в
// Samples_Export/RealSchool_GRIND.xlsx. Временный файл итерации (потом снести
// в постоянный QualityDocs при победе).
// ВРЕМЕННЫЙ файл кампании (окт. 2026): длинные прогоны выведены из сьюта
// (каждый — 10–30 мин). Итог зафиксирован: 285 → 162 (допы посменно,
// ослабленные капы, свежие базы). См. WORK_LOG (вечер 04.10).
public sealed class GrindExperimentTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static int TeacherWindows(SchedulingProblem p, List<PlacedLesson> placements)
    {
        var occById = p.Occurrences.ToDictionary(o => o.Id);
        int w = 0;
        foreach (var g in placements.GroupBy(x => occById[x.OccurrenceId].TeacherId))
            foreach (var day in g.GroupBy(x => x.DayIndex))
            {
                var s = day.Select(x => x.SlotIndex).Distinct().OrderBy(x => x).ToList();
                if (s.Count > 1) w += (s[^1] - s[0] + 1) - s.Count;
            }
        return w;
    }

    private static (int Hard, int Pupil) HardPupil(SchedulingProblem p, List<PlacedLesson> pl)
    {
        var vr = PlacementValidator.Validate(p, pl);
        return (vr.HardViolations.Count,
            vr.HardViolations.Count(v => v.Code is "student-gap" or "student-late-start"));
    }

    [Fact(Skip = "Долгая кампания (15–30 мин). Итог: 162, файл RealSchool_GRIND.xlsx. Вручную при нужде.")]
    public void GrindToConvergence()
    {
        var p = FinalSchoolRunTests.BuildFinalProblem();
        var greedy = GreedyPlacer.Place(p, 7);
        var start = greedy.Placed.Select(kv => new PlacedLesson
        {
            OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
            SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
        }).ToList();
        output.WriteLine($"GRIND greedy: {greedy.Placed.Count}/{p.Occurrences.Count}");
        var rep0 = CompactRepair.Repair(p, start);
        var cur = rep0.Placements;
        var (h0, pu0) = HardPupil(p, cur);
        var best = (Pl: cur, Hard: h0, Pupil: pu0,
            Win: TeacherWindows(p, cur),
            Soft: SoftEvaluator.Evaluate(p, cur, EffectiveRuleSet.Default).Total);
        output.WriteLine($"GRIND start: hard={h0} pupil={pu0} tw={best.Win} soft={best.Soft}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var budget = TimeSpan.FromMinutes(28);
        int seed = 100, round = 0, stalled = 0;
        var seeds = new[] { 100, 500, 1000, 1500, 2000, 2500 };
        int si = 0;
        while (sw.Elapsed < budget && stalled < 6)
        {
            round++;
            // v4-рецепт: ослабленные капы стадий ( gapsCap/softCap ) + свежие базы.
            // Каждый 3-й раунд — свежая база с нового сида (прыжок по бассейнам).
            List<PlacedLesson> basePl;
            if (round % 3 == 1)
            {
                var gg = GreedyPlacer.Place(p, seeds[si % seeds.Length]);
                basePl = CompactRepair.Repair(p, gg.Placed.Select(kv => new PlacedLesson
                {
                    OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
                    SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
                }).ToList()).Placements;
                si++;
            }
            else basePl = cur;
            var ls = LocalSearch.Improve(p, basePl, TimeSpan.FromSeconds(90), seed: seed);
            var d0 = DayCloseLns.Improve(p, ls.Placements, TimeSpan.FromSeconds(15), seed: seed + 1);
            var t1 = ThinDayLns.Improve(p, d0.Placements, TimeSpan.FromSeconds(10), seed: seed + 2,
                gapsCap: 3, softCap: 300);
            var t2 = TeacherDayLns.Improve(p, t1.Placements, TimeSpan.FromSeconds(20), seed: seed + 3);
            var ov = OverloadLns.Improve(p, t2.Placements, TimeSpan.FromSeconds(10), seed: seed + 4);
            var t3 = CrossShiftLns.Improve(p, ov.Placements, TimeSpan.FromSeconds(25), seed: seed + 5);
            var t4 = SwapLns.Improve(p, t3.Placements, TimeSpan.FromSeconds(10), seed: seed + 6,
                softCap: 200);
            var sy = SyncLns.Improve(p, t4.Placements, TimeSpan.FromSeconds(10), seed: seed + 7);
            var rp = CompactRepair.Repair(p, sy.Placements);
            var pe = PeSpacingRepair.Repair(p, rp.Placements);
            var (h, pu) = HardPupil(p, pe);
            int w = TeacherWindows(p, pe);
            long s = SoftEvaluator.Evaluate(p, pe, EffectiveRuleSet.Default).Total;
            output.WriteLine($"GRIND r{round} seed={seed}: hard={h} pupil={pu} tw={w} soft={s} " +
                $"t={sw.Elapsed.TotalMinutes:F1}min");
            bool better = (pu, h, w, s).CompareTo((best.Pupil, best.Hard, best.Win, best.Soft)) < 0;
            if (better)
            {
                best = (pe, h, pu, w, s);
                cur = pe;
                stalled = 0;
                output.WriteLine($"GRIND r{round}: NEW BEST tw={w} hard={h}");
            }
            else
            {
                stalled++;
                output.WriteLine($"GRIND r{round}: no improvement ({stalled}/4).");
            }
            seed += 10;
        }
        sw.Stop();
        output.WriteLine($"GRIND final: hard={best.Hard} pupil={best.Pupil} tw={best.Win} " +
            $"soft={best.Soft} rounds={round} t={sw.Elapsed.TotalMinutes:F1}min");
        var dir = Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "Samples_Export");
        Directory.CreateDirectory(dir);
        var unplaced = p.Occurrences.Select(o => o.Id)
            .Except(best.Pl.Select(x => x.OccurrenceId)).ToList();
        var relaxedPe = RuleResolver.Resolve("STANDARD",
            new Dictionary<string, long> { ["sanpin-pe-spacing"] = 10 },
            new HashSet<string> { "sanpin-pe-spacing" });
        using (var fs = new FileStream(Path.Combine(dir, "RealSchool_GRIND.xlsx"), FileMode.Create))
            ScheduleExcelExporter.ExportDraftGrid(p, best.Pl, unplaced, fs,
                includeTeacherSheet: true, includeRoomSheet: true, rules: relaxedPe);
        output.WriteLine("GRIND xlsx written.");
    }

    [Fact(Skip = "Зонд гипотезы (допы помогают: 231 vs 162) — итог зафиксирован.")]
    public void GrindNoDopsProbe()
    {
        // Проверка гипотезы: без допов (45 живых, перегрузы) дни плотнее
        // и окон меньше, чем с допами (52, фрагментация). Короткий бюджет.
        var p = FinalSchoolRunTests.BuildFinalProblem(useDops: false);
        var greedy = GreedyPlacer.Place(p, 7);
        var start = greedy.Placed.Select(kv => new PlacedLesson
        {
            OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
            SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
        }).ToList();
        var rep0 = CompactRepair.Repair(p, start);
        var cur = rep0.Placements;
        var (h0, pu0) = HardPupil(p, cur);
        var best = (Pl: cur, Hard: h0, Pupil: pu0,
            Win: TeacherWindows(p, cur),
            Soft: SoftEvaluator.Evaluate(p, cur, EffectiveRuleSet.Default).Total);
        output.WriteLine($"NODOPS greedy: {greedy.Placed.Count}/{p.Occurrences.Count}");
        output.WriteLine($"NODOPS start: hard={h0} pupil={pu0} tw={best.Win} soft={best.Soft}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var budget = TimeSpan.FromMinutes(12);
        int seed = 100, round = 0, stalled = 0;
        while (sw.Elapsed < budget && stalled < 4)
        {
            round++;
            var ls = LocalSearch.Improve(p, cur, TimeSpan.FromSeconds(60), seed: seed);
            var d0 = DayCloseLns.Improve(p, ls.Placements, TimeSpan.FromSeconds(10), seed: seed + 1);
            var t1 = ThinDayLns.Improve(p, d0.Placements, TimeSpan.FromSeconds(8), seed: seed + 2,
                gapsCap: 3, softCap: 300);
            var t2 = TeacherDayLns.Improve(p, t1.Placements, TimeSpan.FromSeconds(15), seed: seed + 3);
            var ov = OverloadLns.Improve(p, t2.Placements, TimeSpan.FromSeconds(8), seed: seed + 4);
            var t3 = CrossShiftLns.Improve(p, ov.Placements, TimeSpan.FromSeconds(15), seed: seed + 5);
            var t4 = SwapLns.Improve(p, t3.Placements, TimeSpan.FromSeconds(8), seed: seed + 6,
                softCap: 200);
            var sy = SyncLns.Improve(p, t4.Placements, TimeSpan.FromSeconds(8), seed: seed + 7);
            var rp = CompactRepair.Repair(p, sy.Placements);
            var pe = PeSpacingRepair.Repair(p, rp.Placements);
            var (h, pu) = HardPupil(p, pe);
            int w = TeacherWindows(p, pe);
            long s = SoftEvaluator.Evaluate(p, pe, EffectiveRuleSet.Default).Total;
            output.WriteLine($"NODOPS r{round} seed={seed}: hard={h} pupil={pu} tw={w} soft={s} " +
                $"t={sw.Elapsed.TotalMinutes:F1}min");
            bool better = (pu, h, w, s).CompareTo((best.Pupil, best.Hard, best.Win, best.Soft)) < 0;
            if (better)
            {
                best = (pe, h, pu, w, s);
                cur = pe;
                stalled = 0;
                output.WriteLine($"NODOPS r{round}: NEW BEST tw={w} hard={h}");
            }
            else
            {
                stalled++;
                output.WriteLine($"NODOPS r{round}: no improvement ({stalled}/4).");
            }
            seed += 10;
        }
        sw.Stop();
        output.WriteLine($"NODOPS final: hard={best.Hard} pupil={best.Pupil} tw={best.Win} " +
            $"soft={best.Soft} rounds={round} t={sw.Elapsed.TotalMinutes:F1}min");
    }
}
