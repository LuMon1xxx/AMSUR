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

    private sealed record Built(SchoolData Data, SchedulingProblem Problem);

    private static Built BuildAll(int seed)
    {
        using var fs = File.OpenRead(FindWorkbook());
        var rows = ExcelLoadExchange.ImportLoad(fs);
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows, daysCount: 5, slotsPerDay: 14);
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
        AddWhole(cls10, "Физика", "Александрович В.А.", 2);
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
        AddWhole(cls11, "Физика", "Александрович В.А.", 2);
        AddWhole(cls11, "Астрономия", "Александрович В.А.", 1);
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
