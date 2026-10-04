using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// Качественные документы 5–11 к 2026/27 (по просьбе): НЕ быстро, а хорошо.
// v1 — штат как в файле (167 учителей, все ≤21 ч); v2 — ужатый (~45, как живой).
// Оба: те же 408 строк/769 ч, классный час — синтез Чт 1/6 (строки файла убраны),
// дневные капы — строго по СанПиН (5–6: 6, 7–11: 7), конвейер BestOf 5 сидов.
public sealed class QualityDocsTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly int[] Seeds = [11, 22, 33, 42, 7];

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

    // v2-штат — как живой (по словам школы, 04.10.2026): английский 4,
    // русский 4, белорусский 4, математики 5 (4 чистых + 1 с информатики);
    // семьи: языки/лит-ры и математика/алгебра/геометрия — общие пулы
    // (в жизни их ведут одни люди); остальным — K=max(1,round(часы/30)).
    // Второй половине сплитов — следующий по пулу (A≠B; K≥2 у семей есть).
    // Классный час не трогаем.
    private static string FamilyOf(string subject)
    {
        string n = subject.ToLowerInvariant();
        if (n.Contains("англ") || n.Contains("иностран")) return "Английский";
        if (n.Contains("математи") || n.Contains("алгебр") || n.Contains("геометри")) return "Математики";
        if (n == "русский язык" || n == "русская литература") return "Русский";
        if (n == "белорусский язык" || n == "белорусская литература") return "Белорусский";
        return subject;
    }

    private static int PoolSize(string family, int totalHours) => family switch
    {
        "Английский" => 4,
        "Русский" => 4,
        "Белорусский" => 4,
        "Математики" => 5,
        _ => Math.Max(1, (int)Math.Round(totalHours / 30.0)),
    };

    private static List<LoadRow> TightRows(List<LoadRow> src)
    {
        var byFamily = src.Where(r => r.SubjectName != "Классный час")
            .GroupBy(r => FamilyOf(r.SubjectName)).ToList();
        var mapA = new Dictionary<(string Fam, string Old), string>();
        var mapB = new Dictionary<(string Fam, string Old), string>();
        foreach (var g in byFamily)
        {
            int total = g.Sum(r => r.HoursPerWeek);
            int k = PoolSize(g.Key, total);
            var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in g)
            {
                if (!idx.TryGetValue(r.TeacherName, out int ri))
                {
                    ri = idx.Count;
                    idx[r.TeacherName] = ri;
                }
                mapA[(g.Key, r.TeacherName)] = $"{g.Key}-У{ri % k + 1}";
                if (r.SplitTeacherBName is not null)
                    mapB[(g.Key, r.SplitTeacherBName)] = k == 1
                        ? $"{g.Key}-Б"
                        : $"{g.Key}-У{(ri + 1) % k + 1}";
            }
        }
        return src.Select(r =>
            r.SubjectName == "Классный час" ? r :
            r with
            {
                TeacherName = mapA[(FamilyOf(r.SubjectName), r.TeacherName)],
                SplitTeacherBName = r.SplitTeacherBName is null
                    ? null : mapB[(FamilyOf(r.SubjectName), r.SplitTeacherBName)],
            }).ToList();
    }

    // Классный час: строки файла → классруки + синтез (Чт, 1/6).
    // allowOverload — только для v2 (кадровый голод вынуждает перегруз;
    // недельные 25 ч всё равно подсвечены чекером).
    private static SchedulingProblem BuildDocs(List<LoadRow> rows, bool allowOverload = false)
    {
        var hourRows = rows.Where(r => r.SubjectName == "Классный час").ToList();
        var body = rows.Where(r => r.SubjectName != "Классный час").ToList();
        var classesCfg = hourRows
            .GroupBy(r => r.ClassName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ClassConfigRow(g.Key, g.First().TeacherName,
                HourResolution.ParseGrade(g.Key), 25)).ToList();
        var settings = new FlexSettingsRow(true, 3, 2, 1, 7, TeacherAssignMode.HardClass,
            AllowTeacherOverload: allowOverload, TeacherOverloadCap: 12);
        var flex = FlexDataset.Empty with
        {
            Classes = classesCfg,
            CommonLesson = new CommonLessonRow(true, 3, 1, "5,6,7,8,9,10,11", true, 6),
            Settings = settings,
        };
        var data = SchoolDataImporter.Import(Guid.NewGuid(), body,
            daysCount: 5, slotsPerDay: 12, flex: flex);
        foreach (var c in data.Classes)
            c.MaxLessonsPerDay = c.Grade <= 6 ? 6 : 7; // СанПиН, без натяжки 8
        var (p, errors) = ProblemBuilder.Build(data.ToProblemInput());
        Assert.NotNull(p);
        Assert.True(errors.Count == 0, "build: " + string.Join(";", errors));
        return p!;
    }

    private static (List<PlacedLesson> Placements, long Soft, int Hard) RunOnce(
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
        // Grind-lite доводка (тот же код, что режим ТОП, короче бюджеты):
        // плотные дни учителей вместо 1–2 уроков + финальный ремонт.
        var polished = GrindLite.Polish(p, res.Placements, EffectiveRuleSet.Default,
            TimeSpan.FromSeconds(20), seed: seed);
        var vr = PlacementValidator.Validate(p, polished.Placements);
        return (polished.Placements, polished.SoftTotal, vr.HardViolations.Count);
    }

    private static (List<PlacedLesson> Placements, long Soft, int Hard) BestOf(
        SchedulingProblem p, double lsSeconds)
    {
        (List<PlacedLesson> Placements, long Soft, int Hard)? best = null;
        int Pupil(List<PlacedLesson> pl) => PlacementValidator.Validate(p, pl)
            .HardViolations.Count(v => v.Code is "student-gap" or "student-late-start");
        foreach (int seed in Seeds)
        {
            var cur = RunOnce(p, seed, lsSeconds);
            if (best is null || (Pupil(cur.Placements), cur.Hard, cur.Soft)
                    .CompareTo((Pupil(best.Value.Placements), best.Value.Hard, best.Value.Soft)) < 0)
                best = cur;
            var b = best.Value;
            if (Pupil(b.Placements) == 0 && b.Hard == 0) break;
        }
        return best!.Value;
    }

    private sealed record Compliance(
        int Hard, int Pupil, int DayCaps, int PeRuns,
        long Peak, long Edge, long Alt, int ClassHourOk, int ClassHourTotal,
        int WeekBaseWarn, int WeekMaxErr, int TeachersOver25, int Unplaced,
        int TeacherWindows, int TeacherDays);

    private Compliance Check(SchedulingProblem p, List<PlacedLesson> placements, int unplaced)
    {
        var occById = p.Occurrences.ToDictionary(o => o.Id);
        var vr = PlacementValidator.Validate(p, placements);
        var bd = SoftEvaluator.Evaluate(p, placements, EffectiveRuleSet.Default);
        long Comp(string c) => bd.Components.First(x => x.Code == c).Value;
        int pupil = vr.HardViolations.Count(v => v.Code is "student-gap" or "student-late-start");
        // Дневные капы СанПиН (только уроки — внеурочка не в счёт).
        int dayCaps = 0;
        foreach (var g in placements.GroupBy(x => occById[x.OccurrenceId].ClassId))
        {
            if (!p.Classes.TryGetValue(g.Key, out var cls)) continue;
            int cap = cls.Grade <= 1 ? 5 : cls.Grade <= 4 ? 5 : cls.Grade <= 6 ? 6 : 7;
            foreach (var day in g.GroupBy(x => x.DayIndex))
            {
                var slots = day.Where(x =>
                    {
                        var o = occById[x.OccurrenceId];
                        return !o.IsExtra && !(p.Subjects.TryGetValue(o.SubjectId, out var s) && s.IsNonLesson);
                    }).Select(x => x.SlotIndex).Distinct().ToList();
                if (slots.Count > cap) dayCaps++;
            }
        }
        // Недельные нормы.
        var week = new Dictionary<Guid, HashSet<(int, int)>>();
        var weekAll = new Dictionary<Guid, HashSet<(int, int)>>();
        foreach (var x in placements)
        {
            var o = occById[x.OccurrenceId];
            if (!weekAll.TryGetValue(o.ClassId, out var a)) weekAll[o.ClassId] = a = [];
            a.Add((x.DayIndex, x.SlotIndex));
            bool counted = !o.IsExtra &&
                !(p.Subjects.TryGetValue(o.SubjectId, out var s) && s.IsNonLesson);
            if (!counted) continue;
            if (!week.TryGetValue(o.ClassId, out var c)) week[o.ClassId] = c = [];
            c.Add((x.DayIndex, x.SlotIndex));
        }
        int baseWarn = 0, maxErr = 0;
        foreach (var (cid, all) in weekAll)
        {
            if (!p.Classes.TryGetValue(cid, out var cls)) continue;
            int cnt = week.TryGetValue(cid, out var c) ? c.Count : 0;
            if (SanPinLimits.WeeklyClassBase.TryGetValue(cls.Grade, out int norm) && cnt > norm)
                baseWarn++;
            if (SanPinLimits.WeeklyClassMax.TryGetValue(cls.Grade, out int max) && all.Count > max)
                maxErr++;
        }
        // Классный час: 24 шт, Чт, первые слоты, extra.
        var hourId = p.Subjects.Values
            .First(s => string.Equals(s.Name, "Классный час", StringComparison.OrdinalIgnoreCase)).Id;
        var hours = p.Occurrences.Where(o => o.SubjectId == hourId).ToList();
        var pos = placements.ToDictionary(x => x.OccurrenceId);
        int hourOk = hours.Count(o => pos.TryGetValue(o.Id, out var pl) && pl.DayIndex == 3 &&
            (pl.SlotIndex == 1 || pl.SlotIndex == 6) && o.IsExtra);
        // Учителя >25 ч.
        int over = placements.GroupBy(x => occById[x.OccurrenceId].TeacherId)
            .Count(g => g.Count() > 25);
        var peDays = placements
            .Where(x => { var o = occById[x.OccurrenceId];
                return !o.IsExtra && p.Subjects.TryGetValue(o.SubjectId, out var s) && s.IsPhysicalEducation; })
            .GroupBy(x => occById[x.OccurrenceId].ClassId).ToList();
        int peRuns = peDays.Sum(g => SoftUnits.PeRuns(g.Select(x => x.DayIndex).ToList()));
        // Окна и дни учителей (честный учительский счёт).
        int tWin = 0, tDays = 0;
        foreach (var g in placements.GroupBy(x => occById[x.OccurrenceId].TeacherId))
            foreach (var day in g.GroupBy(x => x.DayIndex))
            {
                var s = day.Select(x => x.SlotIndex).Distinct().OrderBy(x => x).ToList();
                tDays++;
                if (s.Count > 1) tWin += (s[^1] - s[0] + 1) - s.Count;
            }
        return new Compliance(vr.HardViolations.Count, pupil, dayCaps, peRuns,
            Comp("peak-days"), Comp("edge-once"), Comp("alternation"),
            hourOk, hours.Count, baseWarn, maxErr, over, unplaced, tWin, tDays);
    }

    private static string ExportPath(string name)
    {
        var dir = Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "Samples_Export");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    private void Report(string tag, SchedulingProblem p, Compliance c, long soft, int placed)
    {
        output.WriteLine($"{tag}: teachers={p.Teachers.Count} placed={placed}/{p.Occurrences.Count} " +
            $"soft={soft} hard={c.Hard} pupil={c.Pupil} dayCaps={c.DayCaps} peRuns={c.PeRuns} " +
            $"peak={c.Peak} edge={c.Edge} alt={c.Alt}");
        output.WriteLine($"{tag}: classHour={c.ClassHourOk}/{c.ClassHourTotal} " +
            $"weekBaseWarn={c.WeekBaseWarn} weekMaxErr={c.WeekMaxErr} teachersOver25={c.TeachersOver25} " +
            $"unplaced={c.Unplaced} tWin={c.TeacherWindows}/{c.TeacherDays}");
    }

    [Fact]
    public void QualityDocs_V1_Ideal()
    {
        var p = BuildDocs(FileRows());
        output.WriteLine($"V1 teachers={p.Teachers.Count} occ={p.Occurrences.Count}");
        var (pl, soft, _) = BestOf(p, 60);
        var unplaced = p.Occurrences.Select(o => o.Id).Except(pl.Select(x => x.OccurrenceId)).ToList();
        var c = Check(p, pl, unplaced.Count);
        Report("V1-2026", p, c, soft, pl.Count);
        Assert.Equal(0, unplaced.Count);
        Assert.Equal(0, c.Hard);
        Assert.Equal(0, c.Pupil);
        Assert.Equal(0, c.DayCaps);
        Assert.Equal(24, c.ClassHourOk);
        Assert.Equal(0, c.TeachersOver25);
        using (var fs = new FileStream(ExportPath("RealSchool_2026_v1.xlsx"), FileMode.Create))
            ScheduleExcelExporter.ExportGrid(p, pl, fs);
    }

    [Fact]
    public void QualityDocs_V2_Hard()
    {
        var rows = TightRows(FileRows());
        var p = BuildDocs(rows, allowOverload: true);
        output.WriteLine($"V2 teachers={p.Teachers.Count} occ={p.Occurrences.Count}");
        var (pl, soft, _) = BestOf(p, 60);
        var unplaced = p.Occurrences.Select(o => o.Id).Except(pl.Select(x => x.OccurrenceId)).ToList();
        var c = Check(p, pl, unplaced.Count);
        Report("V2-2026", p, c, soft, pl.Count);
        var relaxedPe = RuleResolver.Resolve("STANDARD",
            new Dictionary<string, long> { ["sanpin-pe-spacing"] = 10 },
            new HashSet<string> { "sanpin-pe-spacing" });
        using (var fs = new FileStream(ExportPath("RealSchool_2026_v2.xlsx"), FileMode.Create))
            ScheduleExcelExporter.ExportDraftGrid(p, pl, unplaced, fs,
                includeTeacherSheet: true, includeRoomSheet: true, rules: relaxedPe);
        output.WriteLine("V2 draft: pe-spacing relaxed (warnings in file).");
        // Тяжелее в разы: покрытие или штраф.
        Assert.True(unplaced.Count > 0 || soft > 30000,
            $"v2 not harder: placed {pl.Count}/{p.Occurrences.Count}, soft {soft}");
    }
}
