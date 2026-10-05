using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// ВРЕМЕННЫЙ grind идеальной 5–11 (тонкий штат 57): LS60 + раунды
// (Thin15 + TeacherDay20 + CrossShift15 + Swap10 + Sync10 + repair + PeRepair)
// до 3 пустых раундов или 14 мин. Лучшее — в OurIdeal_GRIND_2026.xlsx.
// Удалить после прогона.
public sealed class OurIdealGrindTmpTests(Xunit.Abstractions.ITestOutputHelper output)
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

    [Fact]
    public void OurIdeal_Grind()
    {
        var p = OurIdealTopTmpTests.BuildExpandedWithOptions(33, 60);
        output.WriteLine($"GRIND-IDEAL: occ={p.Occurrences.Count} teachers={p.Teachers.Count}");
        var greedy = GreedyPlacer.Place(p, 33);
        output.WriteLine($"GRIND-IDEAL greedy: {greedy.Placed.Count}/{p.Occurrences.Count}");
        var start = greedy.Placed.Select(kv => new PlacedLesson
        {
            OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
            SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
        }).ToList();
        var rep0 = CompactRepair.Repair(p, start);
        var ls0 = LocalSearch.Improve(p, rep0.Placements, TimeSpan.FromSeconds(60), seed: 33);
        var cur = ls0.Placements;
        var (h0, pu0) = HardPupil(p, cur);
        var best = (Pl: cur, Hard: h0, Pupil: pu0,
            Win: TeacherWindows(p, cur),
            Soft: SoftEvaluator.Evaluate(p, cur, EffectiveRuleSet.Default).Total);
        output.WriteLine($"GRIND-IDEAL start: hard={h0} pupil={pu0} tw={best.Win} soft={best.Soft}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var budget = TimeSpan.FromMinutes(14);
        int seed = 1000, round = 0, stalled = 0;
        while (sw.Elapsed < budget && stalled < 3)
        {
            round++;
            var t1 = ThinDayLns.Improve(p, cur, TimeSpan.FromSeconds(15), seed: seed,
                gapsCap: 3, softCap: 300);
            var t2 = TeacherDayLns.Improve(p, t1.Placements, TimeSpan.FromSeconds(20), seed: seed + 1);
            var t3 = CrossShiftLns.Improve(p, t2.Placements, TimeSpan.FromSeconds(15), seed: seed + 2);
            var t4 = SwapLns.Improve(p, t3.Placements, TimeSpan.FromSeconds(10), seed: seed + 3,
                softCap: 200);
            var sy = SyncLns.Improve(p, t4.Placements, TimeSpan.FromSeconds(10), seed: seed + 4);
            var rp = CompactRepair.Repair(p, sy.Placements);
            var pe = PeSpacingRepair.Repair(p, rp.Placements);
            var (h, pu) = HardPupil(p, pe);
            int w = TeacherWindows(p, pe);
            long s = SoftEvaluator.Evaluate(p, pe, EffectiveRuleSet.Default).Total;
            output.WriteLine($"GRIND-IDEAL r{round}: hard={h} pupil={pu} tw={w} soft={s} " +
                $"t={sw.Elapsed.TotalMinutes:F1}min");
            bool better = (pu, h, w, s).CompareTo((best.Pupil, best.Hard, best.Win, best.Soft)) < 0;
            if (better)
            {
                best = (pe, h, pu, w, s);
                cur = pe;
                stalled = 0;
                output.WriteLine($"GRIND-IDEAL r{round}: NEW BEST tw={w}");
            }
            else
            {
                stalled++;
            }
            seed += 10;
        }
        sw.Stop();
        output.WriteLine($"GRIND-IDEAL final: hard={best.Hard} pupil={best.Pupil} tw={best.Win} " +
            $"soft={best.Soft} rounds={round} t={sw.Elapsed.TotalMinutes:F1}min");
        Assert.Equal(p.Occurrences.Count, best.Pl.Count);
        Assert.Equal(0, best.Hard);
        Assert.Equal(0, best.Pupil);
        var dir = Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "Samples_Export");
        Directory.CreateDirectory(dir);
        var unplaced = p.Occurrences.Select(o => o.Id)
            .Except(best.Pl.Select(x => x.OccurrenceId)).ToList();
        using (var fs = new FileStream(Path.Combine(dir, "OurIdeal_GRIND_2026.xlsx"), FileMode.Create))
            ScheduleExcelExporter.ExportDraftGrid(p, best.Pl, unplaced, fs);
        output.WriteLine("GRIND-IDEAL xlsx written.");
    }
}
