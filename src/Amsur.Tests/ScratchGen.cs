using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// SCRATCH 22.09.2026: достройка greedy до 100% валидатор-циклом:
// best-of-N greedy → repair → completion (прямая постановка + каскад с
// выселением ≤6, sync-группы целиком, часы не трогаем) → repair → VND →
// RuinRecreate → gate hard=0 → экспорт ФИНАЛ. Только публичные API;
// гейт — тот же PlacementValidator, что у экспорта и архива.
public sealed class ScratchGen(Xunit.Abstractions.ITestOutputHelper output)
{
    private static string FindWorkbook()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var cand = Path.Combine(dir.FullName, "данные", "тест_реал", "РеальнаяНагрузка_5-11.xlsx");
            if (File.Exists(cand)) return cand;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Нагрузка не найдена.");
    }

    internal sealed record Built(SchoolData Data, SchedulingProblem Problem);

    internal static Built BuildAll(int seed)
    {
        using var fs = File.OpenRead(FindWorkbook());
        var rows = ExcelLoadExchange.ImportLoad(fs);
        // P2-era merge: SchoolDataImporter (пакет P1) держит стену №1–12 и
        // режет slotsPerDay>12. Временная сетка BuildAll идёт НЕ из импортёра,
        // а из ProblemInput ниже (SlotsPerDay 14 + собственные bands 1–7/6–12),
        // поэтому здесь максимум стены (12): curriculum/люди/комнаты те же,
        // задача на выходе — та же (888, Денискин 14ч, к/ч 24).
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows, daysCount: 5, slotsPerDay: 12);
        foreach (var c in data.Classes) c.MaxLessonsPerDay = 8;
        foreach (var t in data.Teachers)
            t.MaxLessonsPerDay = t.Name.StartsWith("ВАКАНСИЯ") ? 100 : 10;

        var subj = data.Subjects.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var teach = data.Teachers.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var cls10 = data.Classes.First(c => c.Name == "10А");
        var cls11 = data.Classes.First(c => c.Name == "11А");
        var profIds = new HashSet<Guid> { cls10.Id, cls11.Id };
        var hourSubjId = data.Subjects.First(
            s => string.Equals(s.Name, "Классный час", StringComparison.OrdinalIgnoreCase)).Id;
        foreach (var item in data.Curriculum.Where(i => i.SubjectId == hourSubjId))
            data.Classes.First(c => c.Id == item.ClassId).ClassTeacherId = item.TeacherId;

        var dropItems = new HashSet<Guid>(data.Curriculum
            .Where(i => profIds.Contains(i.ClassId) || i.SubjectId == hourSubjId)
            .Select(i => i.Id));
        var curriculum = data.Curriculum.Where(i => !dropItems.Contains(i.Id)).ToList();
        var groups = data.Groups.Where(g => !profIds.Contains(g.ClassId)).ToList();
        var splits = new Dictionary<Guid, (Guid, Guid)>(
            data.SplitTeachers.Where(kv => !dropItems.Contains(kv.Key)));

        void AddWhole(SchoolClass cls, string subject, string teacher, int hours)
        {
            if (!subj.TryGetValue(subject, out var s))
                throw new InvalidOperationException($"Нет предмета '{subject}'.");
            if (!teach.TryGetValue(teacher, out var t))
                throw new InvalidOperationException($"Нет учителя '{teacher}'.");
            curriculum.Add(new CurriculumItem
            {
                ClassId = cls.Id, SubjectId = s.Id, TeacherId = t.Id, HoursPerWeek = hours,
            });
        }
        void AddPair(SchoolClass cls, string cellTag, (string Subject, string Teacher)[] halves)
        {
            var sync = Guid.NewGuid();
            for (int i = 0; i < halves.Length; i++)
            {
                if (!subj.TryGetValue(halves[i].Subject, out var s))
                    throw new InvalidOperationException($"Нет предмета '{halves[i].Subject}'.");
                if (!teach.TryGetValue(halves[i].Teacher, out var t))
                    throw new InvalidOperationException($"Нет учителя '{halves[i].Teacher}'.");
                var g = new StudentGroup { ClassId = cls.Id, Name = $"{cls.Name}-{cellTag}-{i}" };
                groups.Add(g);
                curriculum.Add(new CurriculumItem
                {
                    ClassId = cls.Id, SubjectId = s.Id, TeacherId = t.Id,
                    HoursPerWeek = 1, GroupId = g.Id, SyncGroupId = sync,
                });
            }
        }
        AddWhole(cls10, "География", "Берёзко Т.А.", 1);
        AddWhole(cls10, "Черчение", "Лабикова Н.К.", 1);
        AddWhole(cls10, "Русский язык", "Потапов В.Н.", 2);
        AddWhole(cls10, "Русская литература", "Потапов В.Н.", 1);
        AddWhole(cls10, "Физическая культура и здоровье", "Гигель Н.К.", 3);
        AddWhole(cls10, "Математика", "Мисевич Ю.Е.", 4);
        AddWhole(cls10, "Белорусская литература", "Бурко А.С.", 2);
        AddWhole(cls10, "Белорусский язык", "Бурко А.С.", 1);
        AddWhole(cls10, "Физика", "Денискин Е.В.", 2);
        AddWhole(cls10, "Биология (проф)", "Шаройкина А.В.", 1);
        string hib = "История Беларуси (база)", hip = "История Беларуси (проф)";
        string vor = "Воробьёва Ж.А.", lip = "Липницкая М.И.", sha = "Шаройкина А.В.";
        string pav = "Павлович С.А.", en = "Английский язык", hor = "Хорошко Е.Н.";
        string ost = "Островская В.В.", inf = "Информатика", bad = "Бадеева Е.В.";
        string nak = "Наконечная Е.В.", dmp = "ДМП", shab = "Шаболтиев С.В.", dam = "Дамашевич Е.С.";
        AddPair(cls10, "P01", [(hib, vor), ("Биология (база)", sha)]);
        AddPair(cls10, "P02", [(hip, vor), ("Химия (проф)", lip)]);
        AddPair(cls10, "P03", [(hip, vor), ("Химия (проф)", lip)]);
        AddPair(cls10, "P04", [(hip, vor), ("Химия (проф)", lip)]);
        AddPair(cls10, "P05", [(hip, vor), ("Химия (проф)", lip)]);
        AddPair(cls10, "P06", [("Обществоведение (проф)", pav), ("Биология (проф)", sha)]);
        AddPair(cls10, "P07", [("Обществоведение (проф)", pav), ("Биология (проф)", sha)]);
        AddPair(cls10, "P08", [("Обществоведение (база)", pav), ("Биология (база)", sha)]);
        AddPair(cls10, "P09", [(hib, vor), ("Химия (база)", lip)]);
        AddPair(cls10, "P10", [("Биология (проф)", sha), ("Химия (база)", lip)]);
        AddPair(cls10, "S11", [(en, hor), (en, ost)]);
        AddPair(cls10, "S12", [(en, hor), (en, ost)]);
        AddPair(cls10, "S13", [(inf, bad), (inf, nak)]);
        AddPair(cls10, "S14", [(dmp, shab), (dmp, dam)]);
        AddWhole(cls11, "Математика", "Городионок С.Г.", 4);
        AddWhole(cls11, "Белорусский язык", "Толстик С.М.", 2);
        AddWhole(cls11, "Белорусская литература", "Толстик С.М.", 1);
        AddWhole(cls11, "Физическая культура и здоровье", "Гигель Н.К.", 3);
        AddWhole(cls11, "Русская литература", "Абрамова Т.П.", 2);
        AddWhole(cls11, "Русский язык", "Абрамова Т.П.", 1);
        AddWhole(cls11, "География", "Берёзко Т.А.", 1);
        AddWhole(cls11, "Физика", "Денискин Е.В.", 2);
        AddWhole(cls11, "Астрономия", "Денискин Е.В.", 1);
        AddWhole(cls11, "История РБ в контексте мировой истории", "Воробьёва Ж.А.", 2);
        AddWhole(cls11, "Биология (проф)", "Демидчик И.Е.", 2);
        string chp = "Химия (проф)", dem = "Демидчик И.Е.";
        string obp = "Обществоведение (проф)", enp = "Английский язык (проф)";
        string enb = "Английский язык (база)", kas = "Касперович А.А.", bip = "Биология (проф)";
        AddPair(cls11, "S01", [(chp, lip), ("Биология (база)", dem)]);
        AddPair(cls11, "S02", [(chp, lip), (obp, vor)]);
        AddPair(cls11, "S03", [(chp, lip), (obp, vor)]);
        AddPair(cls11, "S04", [(chp, lip), ("Биология (база)", dem)]);
        AddPair(cls11, "S05", [("Химия (база)", lip), ("Обществоведение (база)", vor)]);
        AddPair(cls11, "S06", [(enp, hor), (enp, ost), (enb, kas)]);
        AddPair(cls11, "S07", [(enp, hor), (enp, ost), (enb, kas)]);
        AddPair(cls11, "S08", [(enp, hor), (enp, ost), (bip, dem)]);
        AddPair(cls11, "S09", [(enp, hor), (enp, ost), (bip, dem)]);
        AddPair(cls11, "S10", [(dmp, shab), (dmp, dam)]);
        AddPair(cls11, "S11", [(inf, bad), (inf, nak)]);

        var bands = data.Classes.ToDictionary(
            c => c.Id,
            c => (IReadOnlyList<int>)(c.Grade is 6 or 7
                ? Enumerable.Range(6, 7).ToList()
                : Enumerable.Range(1, 7).ToList()));
        var input = new ProblemInput(
            data.Classes, data.Teachers, data.Subjects, curriculum,
            groups, data.DaysOff, data.Unavailability,
            DaysCount: 5, SlotsPerDay: 14,
            SplitTeachers: splits,
            rooms: data.Rooms, classSlots: bands,
            commonLesson: new CommonLesson
            {
                Enabled = true, DayIndex = 3, SlotIndex = 1, SlotIndexShift2 = 7,
                GradesCsv = "5,6,7,8,9,10,11", UseOwnRooms = true,
            });
        var (problem, errors) = ProblemBuilder.Build(input,
            new SolverOptions(MaxTimeSeconds: 8, NumSearchWorkers: 1,
                RandomSeed: seed, PresolveInPhaseA: false));
        Assert.Empty(errors);
        return new Built(data, problem!);
    }

    // Метрики полного прогона (для FullRunV2 и отчётности конвейера).
    internal sealed record FullMetrics(
        int Placed, int Total, int Gaps, int FailedDays, int PupilHard,
        int Hard, long Soft, int Doubles, int ActiveDays);

    // Полный конвейер v2/v3 (тест-инфра, не продукт): bin-pack greedy
    // (compact+dayReuse) → completion → VND → TeacherDay×3 → DayClose×2 →
    // ThinDay микро-стадия (never-worsens) → DayClose×1 → Sync×3 →
    // TeacherDay-полировка → pupil-дожим (RuinRecreate×10) → TeacherDay×2 +
    // DayClose×1 → pupil-single sync-aware чинка (кап +6 gaps/ход) → экспорт
    // в данные/тест_реал/<outFileName>. rules threadyтся во все фазы.
    internal static FullMetrics RunFullPipeline(
        Xunit.Abstractions.ITestOutputHelper output,
        string outFileName,
        EffectiveRuleSet rules,
        int seed)
    {
        var built = BuildAll(seed);
        var problem = built.Problem;
        var occById = problem.Occurrences.ToDictionary(o => o.Id);
        output.WriteLine($"PIPE occurrences={problem.Occurrences.Count}");

        // 0. Bin-pack greedy (compact+dayReuse), best-of по покрытию.
        GreedyPlacement best = GreedyPlacer.Place(problem, seed, compact: true, dayReuse: true);
        foreach (int s in new[] { seed + 11, seed + 22, seed + 33 })
        {
            var g = GreedyPlacer.Place(problem, s, compact: true, dayReuse: true);
            if (g.Unplaced.Count < best.Unplaced.Count) best = g;
        }
        output.WriteLine($"PIPE best-greedy placed={best.Placed.Count}/{problem.Occurrences.Count}");
        var hourId = problem.Subjects.Values.First(
            s => string.Equals(s.Name, "Классный час", StringComparison.OrdinalIgnoreCase)).Id;
        var hourOcc = new HashSet<Guid>(problem.Occurrences
            .Where(o => o.SubjectId == hourId).Select(o => o.Id));

        var pos = best.Placed.Select(kv => new PlacedLesson
        {
            OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
            SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId,
        }).ToDictionary(p => p.OccurrenceId);
        int validations = 0;
        bool TryValidate(Dictionary<Guid, PlacedLesson> p)
        {
            validations++;
            var vr = PlacementValidator.Validate(problem, p.Values.ToList());
            return !HasBlockingHard(vr);
        }

        // Completion: прямая постановка юнитов (sync целиком) + каскад с
        // точечным выселением ≤8 (часы не трогаем). Порт Scratch_Generate_Final.
        var units = best.Unplaced
            .GroupBy(id => occById[id].SyncGroupId ?? id)
            .Select(g => g.ToList())
            .OrderBy(u => DomainSize(problem, u))
            .ToList();
        var failed = new List<List<Guid>>();
        foreach (var u in units)
            if (!PlaceUnitDirect(problem, occById, pos, u, TryValidate)) failed.Add(u);
        output.WriteLine($"PIPE after direct: failed={failed.Count} validations={validations}");
        foreach (var u in failed.ToList())
        {
            if (PlaceUnitCascade(problem, occById, pos, u, hourOcc, TryValidate))
                failed.Remove(u);
        }
        output.WriteLine($"PIPE after cascade: failed={failed.Count} validations={validations}");
        if (failed.Count > 0)
            throw new InvalidOperationException($"PIPE: не размещено юнитов: {failed.Count}.");

        List<PlacedLesson> Cur() => pos.Values.ToList();
        var repaired = CompactRepair.Repair(problem, Cur());
        var cur = repaired.Placements;
        void Log(string stage)
        {
            var m = Measure(problem, cur, rules);
            output.WriteLine($"PIPE {stage}: gaps={m.Gaps} failedDays={m.FailedDays} " +
                $"pupilHard={m.PupilHard} hard={m.Hard} soft={m.Soft} dbl={m.Doubles} " +
                $"activeDays={m.ActiveDays}");
        }
        Log("vnd-in");
        var vnd = LocalSearch.Improve(problem, cur, TimeSpan.FromSeconds(60), seed, rules: rules);
        cur = vnd.Placements;
        Log("vnd-out");
        for (int i = 0; i < 3; i++)
        {
            var td = TeacherDayLns.Improve(problem, cur,
                TimeSpan.FromSeconds(45), seed + 100 + i * 1000, rules: rules);
            cur = td.Placements;
            Log($"td{i}");
        }
        for (int i = 0; i < 2; i++)
        {
            var dc = DayCloseLns.Improve(problem, cur,
                TimeSpan.FromSeconds(60), seed + 200 + i * 1000, rules: rules);
            cur = dc.Placements;
            Log($"dc{i}");
        }
        // ThinDay микро-стадия после dayclose2 (never-worsens; rules threadyтся).
        var thin = ThinDayLns.Improve(problem, cur,
            TimeSpan.FromSeconds(45), seed + 250, topK: 32, restarts: 6, rules: rules);
        cur = thin.Placements;
        Log("thin");
        {
            var dc = DayCloseLns.Improve(problem, cur,
                TimeSpan.FromSeconds(60), seed + 300, rules: rules);
            cur = dc.Placements;
            Log("dc2");
        }
        for (int i = 0; i < 3; i++)
        {
            var sy = SyncLns.Improve(problem, cur,
                TimeSpan.FromSeconds(30), seed + 400 + i * 1000, rules: rules);
            cur = sy.Placements;
            Log($"sync{i}");
        }
        {
            var td = TeacherDayLns.Improve(problem, cur,
                TimeSpan.FromSeconds(45), seed + 500, rules: rules);
            cur = td.Placements;
            Log("td-polish");
        }
        // Pupil-дожим: RuinRecreate×10.
        for (int round = 0; round < 10; round++)
        {
            var lns = RuinRecreate.Improve(problem, cur,
                TimeSpan.FromSeconds(20), seed + 600 + round * 17, rules: rules);
            cur = lns.Placements;
            Log($"rr{round}");
            if (RuinRecreate.FailedDays(problem, occById, cur) == 0) break;
        }
        for (int i = 0; i < 2; i++)
        {
            var td = TeacherDayLns.Improve(problem, cur,
                TimeSpan.FromSeconds(45), seed + 700 + i * 1000, rules: rules);
            cur = td.Placements;
            Log($"td2-{i}");
        }
        {
            var dc = DayCloseLns.Improve(problem, cur,
                TimeSpan.FromSeconds(60), seed + 800, rules: rules);
            cur = dc.Placements;
            Log("dc3");
        }
        // Pupil-single sync-aware чинка (кап +6 gaps/ход).
        cur = PupilSingleChinka(problem, occById, cur, rules, seed + 900,
            TimeSpan.FromSeconds(60), output);
        Log("chinka");

        var m2 = Measure(problem, cur, rules);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? root = null;
        while (dir is not null)
        {
            var c = Path.Combine(dir.FullName, "данные", "тест_реал");
            if (Directory.Exists(c)) { root = c; break; }
            dir = dir.Parent;
        }
        Assert.NotNull(root);
        var outPath = Path.Combine(root!, outFileName);
        using (var fs = File.Create(outPath))
            ScheduleExcelExporter.ExportGrid(problem, cur, fs);
        output.WriteLine($"PIPE exported: {outPath}");
        return m2;
    }

    // Замер состояния (те же гейты, что в приёмке: gaps/failedDays/pupilHard/hard/soft).
    internal static FullMetrics Measure(
        SchedulingProblem problem,
        IReadOnlyList<PlacedLesson> placements,
        EffectiveRuleSet rules)
    {
        var occById = problem.Occurrences.ToDictionary(o => o.Id);
        return new FullMetrics(
            placements.Count, problem.Occurrences.Count,
            TeacherDayLns.TeacherGridGaps(occById, placements),
            RuinRecreate.FailedDays(problem, occById, placements),
            OverloadLns.PupilHard(problem, placements),
            OverloadLns.BlockingHard(problem, placements),
            SoftEvaluator.Evaluate(problem, placements, rules).Total,
            CountScatteredDoubles(problem, occById, placements),
            placements.Select(p => (occById[p.OccurrenceId].TeacherId, p.DayIndex))
                .Distinct().Count());
    }

    // Рассеянные целые дубли (замороженная формула decision §1, без каталога:
    // units=1 iff eligCount==2 && DISTINCT==2 && |a-b|>1; только сырые единицы).
    internal static int CountScatteredDoubles(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        IReadOnlyList<PlacedLesson> placements)
    {
        var pos = placements.ToDictionary(p => p.OccurrenceId);
        int n = 0;
        foreach (var g in problem.Occurrences
                     .Where(o => o.GroupId is null && o.SyncGroupId is null && pos.ContainsKey(o.Id))
                     .GroupBy(o => (o.ClassId, o.SubjectId)))
        {
            foreach (var day in g.GroupBy(o => pos[o.Id].DayIndex))
            {
                var slots = day.Select(o => pos[o.Id].SlotIndex).ToList();
                if (slots.Count == 2 && slots[0] != slots[1] &&
                    Math.Abs(slots[0] - slots[1]) > 1)
                    n++;
            }
        }
        return n;
    }

    // Sync-aware точечная чинка pupil-стороны: юниты (sync целиком, иначе
    // одиночки) в StableKey-порядке; принять первый ход со строго меньшими
    // failedDays/pupilHard при pupilHard==0, hard==0 и росте gaps ≤ +6/ход.
    private static List<PlacedLesson> PupilSingleChinka(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        List<PlacedLesson> start,
        EffectiveRuleSet rules,
        int seed,
        TimeSpan budget,
        Xunit.Abstractions.ITestOutputHelper output)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var best = start.ToList();
        var pos = best.ToDictionary(p => p.OccurrenceId);
        int bestGaps = TeacherDayLns.TeacherGridGaps(occById, best);
        int bestFailed = RuinRecreate.FailedDays(problem, occById, best);
        int bestPupil = OverloadLns.PupilHard(problem, best);
        var units = best.Select(p => p.OccurrenceId)
            .GroupBy(id => occById[id].SyncGroupId ?? id)
            .Select(g => g.OrderBy(id => occById[id].StableKey, StringComparer.Ordinal).ToList())
            .OrderBy(u => occById[u[0]].StableKey, StringComparer.Ordinal)
            .ToList();
        // Seed влияет только на порядок обхода равных (детерминизм по seed).
        var rng = new Random(seed);
        units = units.OrderBy(u => occById[u[0]].StableKey, StringComparer.Ordinal)
            .ThenBy(_ => rng.Next())
            .ToList();
        bool improved = true;
        int accepted = 0;
        while (improved && sw.Elapsed < budget)
        {
            improved = false;
            foreach (var unit in units)
            {
                if (sw.Elapsed >= budget) break;
                var cells = new HashSet<(int Day, int Slot)>(
                    problem.AllowedDays[unit[0]].SelectMany(d =>
                        problem.AllowedSlots[unit[0]].Select(s => (d, s))));
                foreach (var id in unit.Skip(1))
                    cells.IntersectWith(problem.AllowedDays[id].SelectMany(d =>
                        problem.AllowedSlots[id].Select(s => (d, s))));
                var saved = unit.ToDictionary(id => id, id => pos[id]);
                foreach (var (d, s) in cells.OrderBy(c => c.Day).ThenBy(c => c.Slot))
                {
                    if (sw.Elapsed >= budget) break;
                    if (saved[unit[0]].DayIndex == d && saved[unit[0]].SlotIndex == s) continue;
                    foreach (var id in unit)
                        pos[id] = new PlacedLesson { OccurrenceId = id, DayIndex = d, SlotIndex = s };
                    var trial = pos.Values.ToList();
                    int blocking = OverloadLns.BlockingHard(problem, trial);
                    int pupil = OverloadLns.PupilHard(problem, trial);
                    int fd = RuinRecreate.FailedDays(problem, occById, trial);
                    int gaps = TeacherDayLns.TeacherGridGaps(occById, trial);
                    bool accept = blocking == 0 && pupil == 0 &&
                        (fd < bestFailed || pupil < bestPupil) && gaps - bestGaps <= 6;
                    if (!accept)
                    {
                        foreach (var kv in saved) pos[kv.Key] = kv.Value;
                        continue;
                    }
                    best = trial; bestGaps = gaps; bestFailed = fd; bestPupil = pupil;
                    accepted++; improved = true;
                    break; // pos уже держит принятый ход
                }
                if (improved) break;
                foreach (var kv in saved) pos[kv.Key] = kv.Value;
            }
        }
        output.WriteLine($"PIPE chinka accepted={accepted} failedDays={bestFailed}");
        return best;
    }

    private static int DomainSize(SchedulingProblem problem, List<Guid> unit)
    {
        var cells = new HashSet<(int, int)>(problem.AllowedDays[unit[0]]
            .SelectMany(d => problem.AllowedSlots[unit[0]].Select(s => (d, s))));
        foreach (var id in unit.Skip(1))
            cells.IntersectWith(problem.AllowedDays[id]
                .SelectMany(d => problem.AllowedSlots[id].Select(s => (d, s))));
        return cells.Count;
    }

    private static bool PlaceUnitDirect(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        Dictionary<Guid, PlacedLesson> pos,
        List<Guid> unit,
        Func<Dictionary<Guid, PlacedLesson>, bool> tryValidate)
    {
        var cells = new HashSet<(int Day, int Slot)>(
            problem.AllowedDays[unit[0]].SelectMany(d =>
                problem.AllowedSlots[unit[0]].Select(s => (d, s))));
        foreach (var id in unit.Skip(1))
            cells.IntersectWith(problem.AllowedDays[id].SelectMany(d =>
                problem.AllowedSlots[id].Select(s => (d, s))));
        foreach (var (d, s) in OrderedCells(problem, occById, pos, unit, cells))
        {
            foreach (var id in unit)
                pos[id] = new PlacedLesson { OccurrenceId = id, DayIndex = d, SlotIndex = s };
            if (tryValidate(pos)) return true;
            foreach (var id in unit) pos.Remove(id);
        }
        return false;
    }

    private static bool PlaceUnitCascade(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        Dictionary<Guid, PlacedLesson> pos,
        List<Guid> unit,
        HashSet<Guid> hourOcc,
        Func<Dictionary<Guid, PlacedLesson>, bool> tryValidate)
    {
        var cells = new HashSet<(int Day, int Slot)>(
            problem.AllowedDays[unit[0]].SelectMany(d =>
                problem.AllowedSlots[unit[0]].Select(s => (d, s))));
        foreach (var id in unit.Skip(1))
            cells.IntersectWith(problem.AllowedDays[id].SelectMany(d =>
                problem.AllowedSlots[id].Select(s => (d, s))));
        foreach (var (d, s) in OrderedCells(problem, occById, pos, unit, cells))
        {
            var evict = new HashSet<Guid>();
            foreach (var mid in unit)
            {
                var mo = occById[mid];
                Guid mkey = mo.GroupId ?? mo.ClassId;
                foreach (var p in pos.Values.Where(p => p.DayIndex == d && p.SlotIndex == s))
                {
                    var o = occById[p.OccurrenceId];
                    if (o.TeacherId == mo.TeacherId) evict.Add(p.OccurrenceId);
                    else if ((o.GroupId ?? o.ClassId) == mkey) evict.Add(p.OccurrenceId);
                    else if (o.ClassId == mo.ClassId &&
                             (o.GroupId.HasValue != mo.GroupId.HasValue))
                        evict.Add(p.OccurrenceId);
                }
                int tcount = pos.Values.Count(p =>
                    occById[p.OccurrenceId].TeacherId == mo.TeacherId && p.DayIndex == d);
                if (problem.Teachers.TryGetValue(mo.TeacherId, out var tt) &&
                    tcount + 1 > tt.MaxLessonsPerDay)
                {
                    var one = pos.Values.FirstOrDefault(p =>
                        occById[p.OccurrenceId].TeacherId == mo.TeacherId && p.DayIndex == d);
                    if (one is not null) evict.Add(one.OccurrenceId);
                }
                int cslots = pos.Values
                    .Where(p => occById[p.OccurrenceId].ClassId == mo.ClassId && p.DayIndex == d)
                    .Select(p => p.SlotIndex).Append(s).Distinct().Count();
                if (problem.Classes.TryGetValue(mo.ClassId, out var cc) &&
                    cslots > cc.MaxLessonsPerDay)
                {
                    var one = pos.Values.FirstOrDefault(p =>
                        occById[p.OccurrenceId].ClassId == mo.ClassId && p.DayIndex == d);
                    if (one is not null) evict.Add(one.OccurrenceId);
                }
            }
            foreach (var id in evict.ToList())
            {
                var o = occById[id];
                if (o.SyncGroupId.HasValue)
                    foreach (var m in pos.Values
                                 .Where(p => occById[p.OccurrenceId].SyncGroupId == o.SyncGroupId)
                                 .Select(p => p.OccurrenceId))
                        evict.Add(m);
            }
            if (evict.Any(id => hourOcc.Contains(id)) || evict.Count > 8) continue;
            var saved = evict.ToDictionary(id => id, id => pos[id]);
            foreach (var id in evict) pos.Remove(id);
            foreach (var id in unit)
                pos[id] = new PlacedLesson { OccurrenceId = id, DayIndex = d, SlotIndex = s };
            var evUnits = evict.GroupBy(id => occById[id].SyncGroupId ?? id)
                .Select(g => g.ToList()).ToList();
            bool allBack = true;
            var added = new List<Guid>();
            foreach (var eu in evUnits)
            {
                if (!PlaceUnitSilent(problem, occById, pos, eu, added, tryValidate))
                { allBack = false; break; }
            }
            if (allBack && tryValidate(pos)) return true;
            foreach (var id in added.Concat(unit).ToList()) pos.Remove(id);
            foreach (var kv in saved) pos[kv.Key] = kv.Value;
        }
        return false;
    }

    private static bool PlaceUnitSilent(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        Dictionary<Guid, PlacedLesson> pos,
        List<Guid> unit,
        List<Guid> added,
        Func<Dictionary<Guid, PlacedLesson>, bool> tryValidate)
    {
        var cells = new HashSet<(int Day, int Slot)>(
            problem.AllowedDays[unit[0]].SelectMany(dd =>
                problem.AllowedSlots[unit[0]].Select(ss => (dd, ss))));
        foreach (var id in unit.Skip(1))
            cells.IntersectWith(problem.AllowedDays[id].SelectMany(dd =>
                problem.AllowedSlots[id].Select(ss => (dd, ss))));
        foreach (var (d, s) in OrderedCells(problem, occById, pos, unit, cells))
        {
            foreach (var id in unit)
                pos[id] = new PlacedLesson { OccurrenceId = id, DayIndex = d, SlotIndex = s };
            if (tryValidate(pos)) { added.AddRange(unit); return true; }
            foreach (var id in unit) pos.Remove(id);
        }
        return false;
    }

    private static bool HasBlockingHard(ValidationResult vr) =>
        vr.HardViolations.Any(v => v.Code is not (
            "student-gap" or "student-late-start" or "placement-count"));

    // Прирост окон/позднего старта от постановки юнита в (d,s) — для выбора клетки.
    private static int GapDelta(
        SchedulingProblem problem, Dictionary<Guid, LessonOccurrence> occById,
        Dictionary<Guid, PlacedLesson> pos, List<Guid> unit, int d, int s)
    {
        int delta = 0;
        foreach (var cid in unit.Select(id => occById[id].ClassId).Distinct())
        {
            var cur = pos.Values
                .Where(p => occById[p.OccurrenceId].ClassId == cid && p.DayIndex == d)
                .Select(p => p.SlotIndex).OrderBy(x => x).ToList();
            int anchor = StudentCompactness.AnchorFor(problem, cid);
            int before = cur.Count == 0 ? 0 :
                StudentCompactness.GapOf(cur) + StudentCompactness.LateExcess(cur, anchor);
            var after = cur.Append(s).OrderBy(x => x).ToList();
            delta += StudentCompactness.GapOf(after) + StudentCompactness.LateExcess(after, anchor) - before;
        }
        return delta;
    }

    private static IEnumerable<(int Day, int Slot)> OrderedCells(
        SchedulingProblem problem, Dictionary<Guid, LessonOccurrence> occById,
        Dictionary<Guid, PlacedLesson> pos, List<Guid> unit, HashSet<(int Day, int Slot)> cells) =>
        cells.Select(c => (c.Day, c.Slot, Gap: GapDelta(problem, occById, pos, unit, c.Day, c.Slot)))
            .OrderBy(t => t.Gap).ThenBy(t => t.Day).ThenBy(t => t.Slot)
            .Select(t => (t.Day, t.Slot));

    // SKIP: разовый прогон выполнен 22.09.2026 (ФИНАЛ выгружен, hard=0).
    // Для регенерации убрать Skip и прогнать тест (долгий: ~2-4 мин).
    [Fact(Skip = "Разовая генерация ФИНАЛ — артефакт уже выгружен.")]
    public void Scratch_Generate_Final()
    {
        var built = BuildAll(11);
        var problem = built.Problem;
        output.WriteLine($"GEN occurrences={problem.Occurrences.Count}");
        var occById = problem.Occurrences.ToDictionary(o => o.Id);

        // 1. best-of-N greedy.
        GreedyPlacement best = GreedyPlacer.Place(problem, 11);
        foreach (int s in new[] { 22, 33, 44, 55, 66, 77, 88, 101, 202, 303, 404 })
        {
            var g = GreedyPlacer.Place(problem, s);
            if (g.Unplaced.Count < best.Unplaced.Count) best = g;
        }
        output.WriteLine($"GEN best-greedy placed={best.Placed.Count}/{problem.Occurrences.Count}");
        var hourId = problem.Subjects.Values.First(
            s => string.Equals(s.Name, "Классный час", StringComparison.OrdinalIgnoreCase)).Id;
        var hourOcc = new HashSet<Guid>(problem.Occurrences
            .Where(o => o.SubjectId == hourId).Select(o => o.Id));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int validations = 0;
        List<PlacedLesson> TryValidate(Dictionary<Guid, PlacedLesson> pos)
        {
            validations++;
            var list = pos.Values.ToList();
            var vr = PlacementValidator.Validate(problem, list);
            return HasBlockingHard(vr) ? null! : list;
        }

        // 2. completion: юниты = sync-группы (пары целиком).
        var pos = best.Placed.Select(kv => new PlacedLesson
        {
            OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
            SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId,
        }).ToDictionary(p => p.OccurrenceId);
        var units = best.Unplaced
            .GroupBy(id => occById[id].SyncGroupId ?? id)
            .Select(g => g.ToList()).ToList();
        output.WriteLine($"GEN unplaced units={units.Count}");

        bool PlaceUnit(List<Guid> unit)
        {
            // Кандидаты: пересечение доменов членов юнита.
            var cells = new HashSet<(int Day, int Slot)>(
                problem.AllowedDays[unit[0]].SelectMany(d =>
                    problem.AllowedSlots[unit[0]].Select(s => (d, s))));
            foreach (var id in unit.Skip(1))
                cells.IntersectWith(problem.AllowedDays[id].SelectMany(d =>
                    problem.AllowedSlots[id].Select(s => (d, s))));
            foreach (var (d, s) in OrderedCells(problem, occById, pos, unit, cells))
            {
                foreach (var id in unit)
                    pos[id] = new PlacedLesson { OccurrenceId = id, DayIndex = d, SlotIndex = s };
                var ok = TryValidate(pos);
                if (ok is not null) return true;
                foreach (var id in unit) pos.Remove(id);
            }
            return false;
        }

        var ordered = units.OrderBy(u =>
        {
            var cells = new HashSet<(int, int)>(problem.AllowedDays[u[0]]
                .SelectMany(d => problem.AllowedSlots[u[0]].Select(s => (d, s))));
            foreach (var id in u.Skip(1))
                cells.IntersectWith(problem.AllowedDays[id]
                    .SelectMany(d => problem.AllowedSlots[id].Select(s => (d, s))));
            return cells.Count;
        }).ToList();
        var failed = new List<List<Guid>>();
        foreach (var u in ordered)
            if (!PlaceUnit(u)) failed.Add(u);
        output.WriteLine($"GEN after direct: failed={failed.Count} validations={validations} " +
            $"ms={sw.ElapsedMilliseconds}");

        // 3. каскад с ТОЧЕЧНЫМ выселением: только конфликтующие
        // (тот же учитель/группа/whole-sub в клетке) + по одному для
        // переполненных day-капов. Глубина 1, часы не трогаем, лимит 8.
        foreach (var u in failed.ToList())
        {
            bool done = false;
            var cells = new HashSet<(int Day, int Slot)>(
                problem.AllowedDays[u[0]].SelectMany(d =>
                    problem.AllowedSlots[u[0]].Select(s => (d, s))));
            foreach (var id in u.Skip(1))
                cells.IntersectWith(problem.AllowedDays[id].SelectMany(d =>
                    problem.AllowedSlots[id].Select(s => (d, s))));
            foreach (var (d, s) in OrderedCells(problem, occById, pos, u, cells))
            {
                if (done) break;
                var evict = new HashSet<Guid>();
                foreach (var mid in u)
                {
                    var mo = occById[mid];
                    Guid mkey = mo.GroupId ?? mo.ClassId;
                    foreach (var p in pos.Values.Where(p => p.DayIndex == d && p.SlotIndex == s))
                    {
                        var o = occById[p.OccurrenceId];
                        if (o.TeacherId == mo.TeacherId) evict.Add(p.OccurrenceId);
                        else if ((o.GroupId ?? o.ClassId) == mkey) evict.Add(p.OccurrenceId);
                        else if (o.ClassId == mo.ClassId &&
                                 (o.GroupId.HasValue != mo.GroupId.HasValue))
                            evict.Add(p.OccurrenceId);
                    }
                    int tcount = pos.Values.Count(p =>
                        occById[p.OccurrenceId].TeacherId == mo.TeacherId && p.DayIndex == d);
                    if (problem.Teachers.TryGetValue(mo.TeacherId, out var tt) &&
                        tcount + 1 > tt.MaxLessonsPerDay)
                    {
                        var one = pos.Values.FirstOrDefault(p =>
                            occById[p.OccurrenceId].TeacherId == mo.TeacherId && p.DayIndex == d);
                        if (one is not null) evict.Add(one.OccurrenceId);
                    }
                    int cslots = pos.Values
                        .Where(p => occById[p.OccurrenceId].ClassId == mo.ClassId && p.DayIndex == d)
                        .Select(p => p.SlotIndex).Append(s).Distinct().Count();
                    if (problem.Classes.TryGetValue(mo.ClassId, out var cc) &&
                        cslots > cc.MaxLessonsPerDay)
                    {
                        var one = pos.Values.FirstOrDefault(p =>
                            occById[p.OccurrenceId].ClassId == mo.ClassId && p.DayIndex == d);
                        if (one is not null) evict.Add(one.OccurrenceId);
                    }
                }
                // sync-партнёры выселяемых — целиком.
                foreach (var id in evict.ToList())
                {
                    var o = occById[id];
                    if (o.SyncGroupId.HasValue)
                        foreach (var m in pos.Values
                                     .Where(p => occById[p.OccurrenceId].SyncGroupId == o.SyncGroupId)
                                     .Select(p => p.OccurrenceId))
                            evict.Add(m);
                }
                if (evict.Any(id => hourOcc.Contains(id)) || evict.Count > 8) continue;
                var saved = evict.ToDictionary(id => id, id => pos[id]);
                foreach (var id in evict) pos.Remove(id);
                foreach (var id in u)
                    pos[id] = new PlacedLesson { OccurrenceId = id, DayIndex = d, SlotIndex = s };
                // Реплейс выгнанных (пары целиком).
                var evUnits = evict.GroupBy(id => occById[id].SyncGroupId ?? id)
                    .Select(g => g.ToList()).ToList();
                bool allBack = true;
                var added = new List<Guid>();
                foreach (var eu in evUnits)
                {
                    if (!PlaceUnitSilent(eu, added)) { allBack = false; break; }
                }
                if (allBack && TryValidate(pos) is not null) { done = true; break; }
                foreach (var id in added.Concat(u).ToList()) pos.Remove(id);
                foreach (var kv in saved) pos[kv.Key] = kv.Value;
            }
            if (done) failed.Remove(u);
        }
        output.WriteLine($"GEN after cascade: failed={failed.Count} validations={validations} " +
            $"ms={sw.ElapsedMilliseconds}");
        foreach (var u in failed.Take(8))
            output.WriteLine("GEN still-out: " + string.Join(";",
                u.Select(id => problem.Classes[occById[id].ClassId].Name + "|" +
                    problem.Subjects[occById[id].SubjectId].Name)));
        // Диагностика блокера: для первого юнита — коды нарушений по клеткам.
        if (failed.Count > 0)
        {
            var u0 = failed[0];
            var codeCount = new Dictionary<string, int>();
            int cellsTotal = 0;
            var cells0 = new HashSet<(int Day, int Slot)>(
                problem.AllowedDays[u0[0]].SelectMany(d =>
                    problem.AllowedSlots[u0[0]].Select(s => (d, s))));
            foreach (var id in u0.Skip(1))
                cells0.IntersectWith(problem.AllowedDays[id].SelectMany(d =>
                    problem.AllowedSlots[id].Select(s => (d, s))));
            foreach (var (d, s) in cells0)
            {
                cellsTotal++;
                foreach (var id in u0)
                    pos[id] = new PlacedLesson { OccurrenceId = id, DayIndex = d, SlotIndex = s };
                var vr0 = PlacementValidator.Validate(problem, pos.Values.ToList());
                foreach (var v in vr0.HardViolations
                             .Where(v => v.Code is not ("student-gap" or "student-late-start"
                                 or "placement-count"))
                             .Take(3))
                    codeCount[v.Code + "::" + v.Message] =
                        codeCount.GetValueOrDefault(v.Code + "::" + v.Message) + 1;
                foreach (var id in u0) pos.Remove(id);
            }
            output.WriteLine($"GEN diag cells={cellsTotal} for " +
                string.Join(";", u0.Select(id => problem.Classes[occById[id].ClassId].Name + "|" +
                    problem.Subjects[occById[id].SubjectId].Name + "|" +
                    problem.Teachers[occById[id].TeacherId].Name)));
            foreach (var kv in codeCount.OrderByDescending(kv => kv.Value).Take(12))
                output.WriteLine($"GEN block x{kv.Value}: {kv.Key}");
        }
        if (failed.Count > 0) return;

        bool PlaceUnitSilent(List<Guid> unit, List<Guid> added)
        {
            var cells = new HashSet<(int Day, int Slot)>(
                problem.AllowedDays[unit[0]].SelectMany(dd =>
                    problem.AllowedSlots[unit[0]].Select(ss => (dd, ss))));
            foreach (var id in unit.Skip(1))
                cells.IntersectWith(problem.AllowedDays[id].SelectMany(dd =>
                    problem.AllowedSlots[id].Select(ss => (dd, ss))));
            foreach (var (d, s) in OrderedCells(problem, occById, pos, unit, cells))
            {
                foreach (var id in unit)
                    pos[id] = new PlacedLesson { OccurrenceId = id, DayIndex = d, SlotIndex = s };
                if (TryValidate(pos) is not null) { added.AddRange(unit); return true; }
                foreach (var id in unit) pos.Remove(id);
            }
            return false;
        }

        // 4. polish + gate + export.
        var repaired = CompactRepair.Repair(problem, pos.Values.ToList());
        var imp = LocalSearch.Improve(problem, repaired.Placements,
            TimeSpan.FromSeconds(120), seed: 11);
        var occMap = problem.Occurrences.ToDictionary(o => o.Id);
        var cur = imp.Placements;
        long curSoft = imp.SoftTotal;
        int curFailed = RuinRecreate.FailedDays(problem, occMap, cur);
        output.WriteLine($"GEN after VND: failedDays={curFailed} soft={imp.SoftTotal}");
        for (int round = 0; round < 40 && curFailed > 0; round++)
        {
            var lns = RuinRecreate.Improve(problem, cur,
                TimeSpan.FromSeconds(30), seed: 100 + round * 17);
            cur = lns.Placements;
            curFailed = RuinRecreate.FailedDays(problem, occMap, cur);
            curSoft = lns.SoftTotal;
            output.WriteLine($"GEN LNS round={round} failedDays={curFailed} " +
                $"soft={lns.SoftTotal} iters={lns.Iterations} accepted={lns.Accepted}");
            if (lns.Accepted > 0)
            {
                var vnd = LocalSearch.Improve(problem, cur,
                    TimeSpan.FromSeconds(30), seed: 1000 + round);
                cur = vnd.Placements;
                curFailed = RuinRecreate.FailedDays(problem, occMap, cur);
                curSoft = vnd.SoftTotal;
                output.WriteLine($"GEN VND round={round} failedDays={curFailed} soft={vnd.SoftTotal}");
            }
        }
        var final = cur;
        var vr = PlacementValidator.Validate(problem, final);
        output.WriteLine($"GEN final hard={vr.HardViolations.Count} " +
            $"soft={SoftEvaluator.Evaluate(problem, final).Total}");
        foreach (var v in vr.HardViolations.Take(10))
            output.WriteLine("GEN hard: " + v.Code + " " + v.Message);
        if (vr.HardViolations.Count > 0) return;

        var fpos = final.ToDictionary(p => p.OccurrenceId);
        int hourOk = 0, hourTotal = 0;
        foreach (var o in problem.Occurrences.Where(o => o.SubjectId == hourId))
        {
            hourTotal++;
            var cls = problem.Classes[o.ClassId];
            int expect = cls.Grade is 6 or 7 ? 7 : 1;
            var pl = fpos[o.Id];
            if (pl.DayIndex == 3 && pl.SlotIndex == expect) hourOk++;
        }
        output.WriteLine($"GEN class-hour pinned {hourOk}/{hourTotal}");
        int maxT = final.GroupBy(p => (p.DayIndex, occById[p.OccurrenceId].TeacherId)).Max(g => g.Count());
        int maxC = final.GroupBy(p => (p.DayIndex, occById[p.OccurrenceId].ClassId)).Max(g => g.Count());
        output.WriteLine($"GEN maxLoad teacher/day={maxT} class/day={maxC}");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? root = null;
        while (dir is not null)
        {
            var c = Path.Combine(dir.FullName, "данные", "тест_реал");
            if (Directory.Exists(c)) { root = c; break; }
            dir = dir.Parent;
        }
        Assert.NotNull(root);
        var outPath = Path.Combine(root!, "Сгенерировано_движком_ФИНАЛ.xlsx");
        using (var fs = File.Create(outPath))
            ScheduleExcelExporter.ExportGrid(problem, final, fs);
        output.WriteLine($"GEN exported: {outPath}");
    }
}
