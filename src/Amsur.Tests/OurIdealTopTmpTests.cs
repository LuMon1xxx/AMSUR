using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// ВРЕМЕННЫЙ харнес (окт. 2026): полная идеальная 5–11 как СШ № 8
// (5–9 по А,Б,В,Г; 10–11 по А,Б; смены как в школе: 6–7-е — 2-я;
// классный час — Чт 1-м уроком) + TOP-прогон в OurIdeal_TOP_2026.xlsx.
// Удалить после прогона.
public sealed class OurIdealTopTmpTests(Xunit.Abstractions.ITestOutputHelper output)
{
    internal static List<LoadRow> ExpandedRows()
    {
        var rows = new List<LoadRow>();
        var classes = new[] { "5А","5Б","5В","5Г","6А","6Б","6В","6Г","7А","7Б","7В","7Г",
            "8А","8Б","8В","8Г","9А","9Б","9В","9Г","10А","10Б","11А","11Б" };
        // Тонкий общий штат (~57, никому не больше 18): сплиты настоящие,
        // пары английского, труды чёт/нечет, ДМП двое. Имена пронумерованы.
        var packs = new List<(string Cls, string Subj, int Hours, string Pool, string Room, int? Shift)>();
        foreach (var cls in classes)
        {
            int grade = int.Parse(new string(cls.TakeWhile(char.IsDigit).ToArray()));
            int? shift = grade is 6 or 7 ? 2 : null;
            foreach (var (subj, hours) in OurIdealPlans.PlanForGrade(grade))
            {
                string room = subj == "Физическая культура и здоровье" ? "Спортзал"
                    : subj == "Трудовое обучение" ? $"Маст-{cls}"
                    : $"Каб-{cls}";
                string pool = subj switch
                {
                    "Русская литература" => "Русский язык",
                    "Белорусская литература" => "Белорусский язык",
                    "Геометрия" => "Алгебра",
                    "Обществоведение" => "История",
                    "Астрономия" => "Физика",
                    "Черчение" => "Труд-Д",
                    _ => subj,
                };
                packs.Add((cls, subj, hours, pool, room, shift));
            }
        }
        var engPairs = new[] { ("Учитель Иностранный язык 1", "Учитель Иностранный язык 2"),
            ("Учитель Иностранный язык 3", "Учитель Иностранный язык 4"),
            ("Учитель Иностранный язык 5", "Учитель Иностранный язык 6"),
            ("Учитель Иностранный язык 7", "Учитель Иностранный язык 8") };
        var engIdx = new Dictionary<string, int>();
        int ei = 0;
        foreach (var cls in classes) engIdx[cls] = (ei++) % 4;
        var trudClasses = classes.Where(c =>
        {
            int gr = int.Parse(new string(c.TakeWhile(char.IsDigit).ToArray()));
            return gr is >= 5 and <= 9;
        }).ToList();
        foreach (var g in packs.GroupBy(x => x.Pool).OrderBy(x => x.Key))
        {
            if (g.Key == "Иностранный язык")
            {
                foreach (var pk in g.OrderBy(x => x.Cls))
                {
                    var (a, b) = engPairs[engIdx[pk.Cls]];
                    rows.Add(new(pk.Cls, pk.Subj, pk.Hours, a, true, b, pk.Room,
                        null, null, null, pk.Shift));
                }
                continue;
            }
            if (g.Key == "Трудовое обучение")
            {
                foreach (var pk in g.OrderBy(x => x.Cls))
                {
                    int idx = trudClasses.IndexOf(pk.Cls);
                    var (a, b) = idx % 2 == 0
                        ? ("Учитель Трудовое обучение М1", "Учитель Трудовое обучение Д1")
                        : ("Учитель Трудовое обучение М2", "Учитель Трудовое обучение Д2");
                    rows.Add(new(pk.Cls, pk.Subj, pk.Hours, a, true, b, pk.Room,
                        null, null, null, pk.Shift));
                }
                continue;
            }
            if (g.Key == "Допризывная и медицинская подготовка")
            {
                foreach (var pk in g.OrderBy(x => x.Cls))
                {
                    rows.Add(new(pk.Cls, pk.Subj, pk.Hours, "Учитель ДМП М", true, "Учитель ДМП Д", pk.Room,
                        null, null, null, pk.Shift));
                }
                continue;
            }
            if (g.Key == "Труд-Д")
            {
                foreach (var pk in g.OrderBy(x => x.Cls))
                {
                    string t = pk.Cls == "10Б"
                        ? "Учитель Трудовое обучение Д2"
                        : "Учитель Трудовое обучение Д1";
                    rows.Add(new(pk.Cls, pk.Subj, pk.Hours, t, false, null, pk.Room,
                        null, null, null, pk.Shift));
                }
                continue;
            }
            int n = 1, cur = 0;
            string teacher = $"Учитель {g.Key} 1";
            foreach (var pk in g.OrderBy(x => x.Cls).ThenBy(x => x.Subj))
            {
                if (cur + pk.Hours > 18) { n++; teacher = $"Учитель {g.Key} {n}"; cur = 0; }
                rows.Add(new(pk.Cls, pk.Subj, pk.Hours, teacher, false, null, pk.Room,
                    null, null, null, pk.Shift));
                cur += pk.Hours;
            }
        }
        return rows;
    }

    internal static SchedulingProblem BuildExpandedWithOptions(int seed, double budgetSec,
        bool twoPhase = true, bool presolve = true)
    {
        var rows = ExpandedRows();
        // Классный руководитель — свой человек на класс (час у всех в один
        // слот, учителя часа обязаны различаться). Первый ещё не занятый
        // учитель из плана класса.
        var homeroom = new Dictionary<string, string>();
        var usedHr = new HashSet<string>();
        var hrFamCount = new Dictionary<string, int>();
        string FamOf(string teacher)
        {
            var s = teacher.StartsWith("Учитель ") ? teacher["Учитель ".Length..] : teacher;
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+[БМД]$", "");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+[0-9]+[АБВГ]$", "");
            return s;
        }
        foreach (var cls in rows.Select(r => r.ClassName).Distinct().OrderBy(c => c))
        {
            int grade = int.Parse(new string(cls.TakeWhile(char.IsDigit).ToArray()));
            // Свой человек на класс + часы классных размазаны по предметам
            // (иначе все 24 часа садятся на математиков — первый пункт плана).
            string? pick = null;
            int bestN = int.MaxValue;
            foreach (var (subj, _) in OurIdealPlans.PlanForGrade(grade))
            {
                var t = rows.FirstOrDefault(r => r.ClassName == cls && r.SubjectName == subj)?.TeacherName;
                if (t is null || usedHr.Contains(t)) continue;
                int n = hrFamCount.GetValueOrDefault(FamOf(t));
                if (n < bestN) { bestN = n; pick = t; }
            }
            pick ??= $"Классный руководитель {cls}";
            usedHr.Add(pick);
            hrFamCount[FamOf(pick)] = hrFamCount.GetValueOrDefault(FamOf(pick)) + 1;
            homeroom[cls] = pick;
        }
        var classesCfg = homeroom
            .Select(kv => new ClassConfigRow(kv.Key, kv.Value,
                HourResolution.ParseGrade(kv.Key), 25)).ToList();
        var flex = FlexDataset.Empty with
        {
            Classes = classesCfg,
            CommonLesson = new CommonLessonRow(true, 3, 1, "5,6,7,8,9,10,11", true, 6),
            Rooms = [new RoomConfigRow("Спортзал", false, null, 4, 0, true)],
        };
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows,
            daysCount: 5, slotsPerDay: 12, flex: flex);
        foreach (var c in data.Classes)
            c.MaxLessonsPerDay = c.Grade <= 6 ? 6 : 7;
        var bands = data.Classes.ToDictionary(
            c => c.Id,
            c => (IReadOnlyList<int>)(c.Grade is 6 or 7
                ? Enumerable.Range(6, 7).ToList()
                // 10-е: 35 уроков + час = 36 клеток при стене 35 (5x7) — черчение
                // довело до переполна; даём слоты 1–8 (уроков всё равно ≤7).
                : c.Grade == 10
                ? Enumerable.Range(1, 8).ToList()
                : Enumerable.Range(1, 7).ToList()));
        var input = new ProblemInput(
            data.Classes, data.Teachers, data.Subjects,
            data.Curriculum, data.Groups, data.DaysOff, data.Unavailability,
            DaysCount: 5, SlotsPerDay: 12,
            SplitTeachers: new Dictionary<Guid, (Guid, Guid)>(data.SplitTeachers),
            rooms: data.Rooms,
            classSlots: bands,
            commonLesson: new CommonLesson
            {
                Enabled = true, DayIndex = 3, SlotIndex = 1, SlotIndexShift2 = 6,
                GradesCsv = "5,6,7,8,9,10,11", UseOwnRooms = true,
            });
        foreach (var t in input.Teachers)
            t.MaxLessonsPerDay = 10;
        var (p, errors) = ProblemBuilder.Build(input,
            new SolverOptions(MaxTimeSeconds: budgetSec, NumSearchWorkers: 1, RandomSeed: seed,
                TwoPhaseSolve: twoPhase, PresolveInPhaseA: presolve));
        Assert.True(errors.Count == 0, "build: " + string.Join(";", errors));
        Assert.NotNull(p);
        return p!;
    }

    private static int PupilCount(SchedulingProblem pr, List<PlacedLesson> pl) =>
        PlacementValidator.Validate(pr, pl)
            .HardViolations.Count(v => v.Code is "student-gap" or "student-late-start");

    [Fact]
    public void OurIdeal_TopRun()
    {
        (List<PlacedLesson> Placements, long Soft, int Hard)? best = null;
        SchedulingProblem? bestP = null;
        int bestSeed = 0;
        foreach (int seed in new[] { 11, 22, 33, 42 })
        {
            var sp = BuildExpandedWithOptions(seed, 60);
            if (seed == 11)
                output.WriteLine($"TOP-FULL: occ={sp.Occurrences.Count} teachers={sp.Teachers.Count} " +
                    $"rooms={sp.Rooms.Count} classes={sp.Classes.Count}");
            var greedy = GreedyPlacer.Place(sp, seed);
            List<PlacedLesson> startBase;
            string startSrc;
            startBase = greedy.Placed.Select(kv => new PlacedLesson
            {
                OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
                SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
            }).ToList();
            startSrc = "greedy";
            var repaired = CompactRepair.Repair(sp, startBase);
            var res = LocalSearch.Improve(sp, repaired.Placements,
                TimeSpan.FromSeconds(45), seed: seed);
            var polished = GrindLite.Polish(sp, res.Placements,
                EffectiveRuleSet.Default, TimeSpan.FromSeconds(45), seed: seed);
            var cur = (polished.Placements, polished.SoftTotal,
                PlacementValidator.Validate(sp, polished.Placements).HardViolations.Count);
            output.WriteLine($"TOP-FULL seed={seed}: greedy={greedy.Placed.Count}/{sp.Occurrences.Count} " +
                $"start={startSrc} " +
                $"placed={cur.Placements.Count}/{sp.Occurrences.Count} " +
                $"soft={cur.Item2} hard={cur.Item3} pupil={PupilCount(sp, cur.Item1)} " +
                $"log=[{string.Join(" | ", polished.Log)}]");
            if (best is null || (PupilCount(sp, cur.Item1), cur.Item3, cur.Item2)
                    .CompareTo((PupilCount(bestP!, best.Value.Placements), best.Value.Hard, best.Value.Soft)) < 0)
            {
                best = cur;
                bestP = sp;
                bestSeed = seed;
            }
        }
        var p = bestP!;
        var final = best!.Value;
        var occById = p.Occurrences.ToDictionary(o => o.Id);
        int tWin = 0, tDays = 0;
        foreach (var g in final.Placements.GroupBy(x => occById[x.OccurrenceId].TeacherId))
            foreach (var day in g.GroupBy(x => x.DayIndex))
            {
                var s = day.Select(x => x.SlotIndex).Distinct().OrderBy(x => x).ToList();
                tDays++;
                if (s.Count > 1) tWin += (s[^1] - s[0] + 1) - s.Count;
            }
        var findings = SanPinChecker.Check(p, final.Placements);
        var unplacedIds = p.Occurrences.Select(o => o.Id)
            .Except(final.Placements.Select(x => x.OccurrenceId)).ToList();
        foreach (var id in unplacedIds.Take(8))
        {
            var o = occById[id];
            output.WriteLine($"TOP-FULL-UNPLACED: class={p.Classes[o.ClassId].Name} subj={p.Subjects[o.SubjectId].Name}");
        }
        foreach (var v in PlacementValidator.Validate(p, final.Placements).HardViolations.Take(8))
            output.WriteLine("TOP-FULL-HARD: " + v.Code + " :: " + v.Message);
        output.WriteLine($"TOP-FULL best seed={bestSeed}: soft={final.Soft} hard={final.Hard} " +
            $"pupil={PupilCount(p, final.Placements)} tWin={tWin}/{tDays} " +
            $"sanpin={findings.Count} err={findings.Count(f => f.IsError)}");
        foreach (var f in findings.Where(f => f.IsError).Take(15))
            output.WriteLine("TOP-FULL sanpin-ERR: " + f.Text);
        Assert.Equal(p.Occurrences.Count, final.Placements.Count);
        Assert.Equal(0, final.Hard);
        Assert.Equal(0, PupilCount(p, final.Placements));
        var dir = Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "Samples_Export");
        Directory.CreateDirectory(dir);
        var unplaced = p.Occurrences.Select(o => o.Id)
            .Except(final.Placements.Select(x => x.OccurrenceId)).ToList();
        using (var fs = new FileStream(Path.Combine(dir, "OurIdeal_TOP_2026.xlsx"), FileMode.Create))
            ScheduleExcelExporter.ExportDraftGrid(p, final.Placements, unplaced, fs);
        output.WriteLine("TOP-FULL xlsx written.");
    }
}

internal static class OurIdealPlans
{
    internal static (string Subj, int Hours)[] PlanForGrade(int grade) => grade switch
    {
        5 => P5,
        6 => P6,
        7 => P7,
        8 => P8,
        9 => P9,
        10 => P10,
        11 => P11,
        _ => P5,
    };
    internal static readonly (string Subj, int Hours)[] P5 = [
        ("Математика", 5), ("Русский язык", 3), ("Русская литература", 2),
        ("Белорусский язык", 2), ("Белорусская литература", 2), ("Иностранный язык", 3),
        ("История", 2), ("География", 2), ("Биология", 1), ("Информатика", 1),
        ("Музыка", 1),
        ("Физическая культура и здоровье", 2), ("Трудовое обучение", 2)];
    internal static readonly (string Subj, int Hours)[] P6 = [
        ("Математика", 5), ("Русский язык", 3), ("Русская литература", 2),
        ("Белорусский язык", 2), ("Белорусская литература", 2), ("Иностранный язык", 3),
        ("История", 2), ("География", 2), ("Биология", 2), ("Информатика", 1),
        ("Физическая культура и здоровье", 3), ("Трудовое обучение", 2)];
    internal static readonly (string Subj, int Hours)[] P7 = [
        ("Алгебра", 4), ("Геометрия", 2), ("Русский язык", 2),
        ("Русская литература", 2), ("Белорусский язык", 2), ("Белорусская литература", 2),
        ("Иностранный язык", 3), ("История", 2), ("География", 2), ("Биология", 2),
        ("Физика", 2), ("Информатика", 2),
        ("Физическая культура и здоровье", 3), ("Трудовое обучение", 2)];
    internal static readonly (string Subj, int Hours)[] P8 = [
        ("Алгебра", 4), ("Геометрия", 2), ("Русский язык", 2),
        ("Русская литература", 2), ("Белорусский язык", 2), ("Белорусская литература", 1),
        ("Иностранный язык", 3), ("История", 2), ("География", 2), ("Биология", 2),
        ("Физика", 3), ("Химия", 2), ("Информатика", 1),
        ("Физическая культура и здоровье", 3), ("Трудовое обучение", 1)];
    internal static readonly (string Subj, int Hours)[] P9 = [
        ("Алгебра", 4), ("Геометрия", 2), ("Русский язык", 2),
        ("Русская литература", 2), ("Белорусский язык", 2), ("Белорусская литература", 1),
        ("Иностранный язык", 3), ("История", 2), ("География", 2), ("Биология", 2),
        ("Физика", 3), ("Химия", 2), ("Информатика", 1),
        ("Физическая культура и здоровье", 3), ("Трудовое обучение", 1)];
    internal static readonly (string Subj, int Hours)[] P10 = [
        ("Алгебра", 3), ("Геометрия", 2), ("Русский язык", 2),
        ("Русская литература", 3), ("Белорусский язык", 2), ("Белорусская литература", 1),
        ("Иностранный язык", 3), ("Физика", 3), ("Химия", 2), ("Биология", 2),
        ("География", 2), ("История", 2), ("Обществоведение", 2), ("Информатика", 2),
        ("Физическая культура и здоровье", 2), ("Допризывная и медицинская подготовка", 1),
        ("Черчение", 1)];
    internal static readonly (string Subj, int Hours)[] P11 = [
        ("Алгебра", 3), ("Геометрия", 2), ("Русский язык", 2),
        ("Русская литература", 3), ("Белорусский язык", 2), ("Белорусская литература", 1),
        ("Иностранный язык", 3), ("Физика", 3), ("Химия", 2), ("Биология", 2),
        ("География", 1), ("История", 2), ("Обществоведение", 2), ("Информатика", 2),
        ("Физическая культура и здоровье", 2), ("Допризывная и медицинская подготовка", 1),
        ("Астрономия", 1)];
}
