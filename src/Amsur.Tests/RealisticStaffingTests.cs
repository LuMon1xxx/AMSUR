using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// Две версии штата реальной школы 5–11 (по просьбе): v1 — как в файле нагрузки
// (147 учителей-плейсхолдеров, все ≤ 21 ч: идеальный штат); v2 — те же 408 строк
// и часы, но учителей в ~3 раза меньше (per-предметный пул K = max(2, n/3)):
// душные ставки 30–60 ч, как кадровые дыры настоящей школы.
// Строки/часы/смены/кабинеты/пары одинаковы — отличается ТОЛЬКО штат.
public sealed class RealisticStaffingTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static string FindWorkbook()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var cand = Path.Combine(dir.FullName, "данные", "НашаШкола_5-11_нагрузка.xlsx");
            if (File.Exists(cand)) return cand;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("НашаШкола_5-11_нагрузка.xlsx не найден вверх от тестов.");
    }

    private static List<LoadRow> FileRows()
    {
        using var fs = File.OpenRead(FindWorkbook());
        return ExcelLoadExchange.ImportLoad(fs).ToList();
    }

    // v2: ужатый штат. Классный час не трогаем (и так по 1 ч у своего).
    private static List<LoadRow> TightRows(List<LoadRow> src)
    {
        var bySubject = src.Where(r => r.SubjectName != "Классный час")
            .GroupBy(r => r.SubjectName, StringComparer.OrdinalIgnoreCase).ToList();
        var mapA = new Dictionary<(string Subj, string Old), string>();
        var mapB = new Dictionary<(string Subj, string Old), string>();
        foreach (var g in bySubject)
        {
            var distinct = g.Select(r => r.TeacherName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            int k = Math.Max(2, (int)Math.Ceiling(distinct.Count / 3.0));
            var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in g)
            {
                if (!idx.TryGetValue(r.TeacherName, out int ri))
                {
                    ri = idx.Count;
                    idx[r.TeacherName] = ri;
                }
                mapA[(g.Key, r.TeacherName)] = $"{g.Key}-В{ri % k + 1}";
                if (r.SplitTeacherBName is not null)
                    mapB[(g.Key, r.SplitTeacherBName)] =
                        $"{g.Key}-В{(ri + 1) % k + 1}";
            }
        }
        return src.Select(r =>
            r.SubjectName == "Классный час" ? r :
            r with
            {
                TeacherName = mapA[(r.SubjectName, r.TeacherName)],
                SplitTeacherBName = r.SplitTeacherBName is null
                    ? null : mapB[(r.SubjectName, r.SplitTeacherBName)],
            }).ToList();
    }

    private static SchedulingProblem BuildOf(List<LoadRow> rows)
    {
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows,
            daysCount: 5, slotsPerDay: 12);
        foreach (var c in data.Classes) c.MaxLessonsPerDay = 8; // как PhotoSchool
        var (p, errors) = ProblemBuilder.Build(data.ToProblemInput());
        Assert.NotNull(p);
        Assert.True(errors.Count == 0, "build: " + string.Join(";", errors));
        return p!;
    }

    // Лучшее из нескольких сидов (как продукт Top-5): сначала ноль ученических
    // окон/стартов (святое, D-28), затем минимум hard, затем soft.
    private static (List<PlacedLesson> Placements, long Soft, int Hard, int GapsFixed) BestOf(
        SchedulingProblem p, int[] seeds, double lsSeconds)
    {
        (List<PlacedLesson> Placements, long Soft, int Hard, int GapsFixed)? best = null;
        int PupilIssues(List<PlacedLesson> pl) =>
            PlacementValidator.Validate(p, pl).HardViolations
                .Count(v => v.Code is "student-gap" or "student-late-start");
        (List<PlacedLesson> Placements, long Soft, int Hard, int GapsFixed)? Pick(
            (List<PlacedLesson> Placements, long Soft, int Hard, int GapsFixed) cur)
        {
            if (best is null) return cur;
            int pb = PupilIssues(best.Value.Placements), pc = PupilIssues(cur.Placements);
            if ((pc, cur.Hard, cur.Soft).CompareTo((pb, best.Value.Hard, best.Value.Soft)) < 0)
                return cur;
            return best;
        }
        foreach (int seed in seeds)
        {
            best = Pick(Run(p, seed, lsSeconds));
            var b = best.Value;
            if (PupilIssues(b.Placements) == 0 && b.Hard == 0) break;
        }
        return best!.Value;
    }

    private static (List<PlacedLesson> Placements, long Soft, int Hard, int GapsFixed) Run(
        SchedulingProblem p, int seed, double lsSeconds)
    {
        var greedy = GreedyPlacer.Place(p, seed);
        var start = greedy.Placed.Select(kv => new PlacedLesson
        {
            OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
            SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
        }).ToList();
        var repaired = CompactRepair.Repair(p, start);
        var res = LocalSearch.Improve(p, repaired.Placements,
            TimeSpan.FromSeconds(lsSeconds), seed: seed);
        // Добивка троек физры (поиск их видит в soft, но точечный ремонт надёжнее).
        var finished = PeSpacingRepair.Repair(p, res.Placements);
        var vr = PlacementValidator.Validate(p, finished);
        long soft = SoftEvaluator.Evaluate(p, finished, EffectiveRuleSet.Default).Total;
        return (finished, soft, vr.HardViolations.Count, repaired.RepairedDays);
    }

    private static void Stats(string tag, SchedulingProblem p, List<PlacedLesson> placements,
        int unplaced, Xunit.Abstractions.ITestOutputHelper output)
    {
        var occById = p.Occurrences.ToDictionary(o => o.Id);
        int tDays = 0, tWin = 0, tWinDays = 0, pWin = 0, pLate = 0;
        foreach (var g in placements.GroupBy(x => occById[x.OccurrenceId].TeacherId))
            foreach (var day in g.GroupBy(x => x.DayIndex))
            {
                var s = day.Select(x => x.SlotIndex).Distinct().OrderBy(x => x).ToList();
                tDays++;
                int w = s.Count <= 1 ? 0 : (s[^1] - s[0] + 1) - s.Count;
                tWin += w;
                if (w > 0) tWinDays++;
            }
        foreach (var g in placements.GroupBy(x => occById[x.OccurrenceId].ClassId))
        {
            int anchor = StudentCompactness.AnchorFor(p, g.Key);
            foreach (var day in g.GroupBy(x => x.DayIndex))
            {
                var s = day.Select(x => x.SlotIndex).Distinct().OrderBy(x => x).ToList();
                if (s.Count > 1) pWin += (s[^1] - s[0] + 1) - s.Count;
                pLate += StudentCompactness.LateExcess(s, anchor);
            }
        }
        var loads = placements.GroupBy(x => occById[x.OccurrenceId].TeacherId)
            .Select(g => g.Count()).OrderByDescending(x => x).Take(3).ToList();
        output.WriteLine($"{tag}: teachers={p.Teachers.Count} placed={placements.Count}/{p.Occurrences.Count} " +
            $"unplaced={unplaced} tWin={tWin} tWinDays={tWinDays}/{tDays} pWin={pWin} pLate={pLate} " +
            $"topLoads=[{string.Join(",", loads)}]");
    }

    private static string ExportPath(string name)
    {
        var dir = Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "Samples_Export");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    [Fact]
    public void Staffing_ShapesDifficulty()
    {
        var fileRows = FileRows();
        Assert.Equal(408, fileRows.Count);
        var v1 = BuildOf(fileRows);
        var v2rows = TightRows(fileRows);
        var v2 = BuildOf(v2rows);
        output.WriteLine($"STAFF: v1 teachers={v1.Teachers.Count} v2 teachers={v2.Teachers.Count}");

        var g1 = GreedyPlacer.Place(v1, 11);
        var g2 = GreedyPlacer.Place(v2, 11);
        output.WriteLine($"STAFF: v1 greedy {g1.Placed.Count}/{v1.Occurrences.Count}, " +
            $"v2 greedy {g2.Placed.Count}/{v2.Occurrences.Count}");

        var (pl1, soft1, hard1, _) = BestOf(v1, [11, 22, 33, 42, 7], 12);
        var (pl2, soft2, hard2, _) = Run(v2, 11, 10);
        output.WriteLine("V1 hard: " + string.Join(";",
            PlacementValidator.Validate(v1, pl1).HardViolations.Take(6).Select(v => v.Code + "@" + v.Message)));
        output.WriteLine("V2 hard: " + string.Join(";",
            PlacementValidator.Validate(v2, pl2).HardViolations.Take(6).Select(v => v.Code)));
        Stats("V1-IDEAL", v1, pl1, v1.Occurrences.Count - g1.Placed.Count, output);
        Stats("V2-HARD", v2, pl2, v2.Occurrences.Count - g2.Placed.Count, output);

        var un1 = v1.Occurrences.Select(o => o.Id).Except(g1.Placed.Keys).ToList();
        var un2 = v2.Occurrences.Select(o => o.Id).Except(g2.Placed.Keys).ToList();
        using (var fs = new FileStream(ExportPath("RealSchool_v1_ideal.xlsx"), FileMode.Create))
            ScheduleExcelExporter.ExportDraftGrid(v1, pl1, un1, fs);
        // v2: кадровый голод местами не даёт разнести физру — черновик идёт
        // с ЧЕСТНО ослабленным контролем физкультуры (B2-механика, в файле
        // предупреждения; остальное — отказ как обычно).
        var relaxedPe = RuleResolver.Resolve("STANDARD",
            new Dictionary<string, long> { ["sanpin-pe-spacing"] = 10 },
            new HashSet<string> { "sanpin-pe-spacing" });
        using (var fs = new FileStream(ExportPath("RealSchool_v2_hard.xlsx"), FileMode.Create))
            ScheduleExcelExporter.ExportDraftGrid(v2, pl2, un2, fs,
                includeTeacherSheet: true, includeRoomSheet: true, rules: relaxedPe);
        output.WriteLine("V2 exported with pe-spacing relaxed (warnings in file).");

        // v1 обязана крыться полностью и валидно (идеальный штат).
        Assert.Equal(v1.Occurrences.Count, g1.Placed.Count);
        var vhard1 = PlacementValidator.Validate(v1, pl1).HardViolations;
        Assert.True(0 == hard1, "v1 hard: " + string.Join(";",
            vhard1.Take(5).Select(v => v.Code + "@" + v.Message)));
        // v2 обязана быть строго тяжелее (покрытие или штраф).
        Assert.True(g2.Placed.Count < v2.Occurrences.Count || soft2 > soft1 * 2,
            $"v2 not harder: placed {g2.Placed.Count}/{v2.Occurrences.Count}, soft {soft2} vs {soft1}");
    }
}
