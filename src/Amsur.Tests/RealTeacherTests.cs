using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// Реальный пилот СШ №8 (5–11): нагрузка из фото + НАСТОЯЩИЕ учителя из
// сверки (кросс-чек клеток, см. данные/тест_реал/). Непокрытые уроки —
// ВАКАНСИЯ (честные дыры школы: физика 10-х, астрономия 11-х и др.).
// Смены: 5,8,9,10,11 — 1-я (слоты 1–7); 6,7 — 2-я (слоты 6–12).
// Классный час — Чт слот 1 / слот 7 через CommonLesson (первым уроком).
// Учителя — из заполненные/Учителя-*.xlsx (FIX 22.09.2026, вакансий 0:
// английский/информатика — пары, физика/астрономия 10–11 — Денискин).
public sealed class RealTeacherTests(Xunit.Abstractions.ITestOutputHelper output)
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
        throw new FileNotFoundException("РеальнаяНагрузка_5-11_нагрузка.xlsx не найден.");
    }

    private static SchoolData LoadSchool()
    {
        using var fs = File.OpenRead(FindWorkbook());
        var rows = ExcelLoadExchange.ImportLoad(fs);
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows,
            daysCount: 5, slotsPerDay: 14);
        foreach (var c in data.Classes) c.MaxLessonsPerDay = 8;
        // Реальные нагрузки: до 39 ч/нед (Александрович) — лимит дня 10.
        // ВАКАНСИЯ — не человек: лимит 30, чтобы артефакт капа не прятал
        // настоящие проблемы; сами дыры видны списком вакансий (Import-тест).
        foreach (var t in data.Teachers)
            t.MaxLessonsPerDay = t.Name.StartsWith("ВАКАНСИЯ") ? 100 : 10;
        return data;
    }

    private static SchedulingProblem BuildProblem(SchoolData data)
    {
        // FIX 22.09.2026: реальные смены из заполненные/Ученики_5-11_расписание.xlsx:
        // 1-я (5,8,9,10,11) — слоты 1–7; 2-я (6,7) — слоты 6–12 (было 8–14 —
        // резало 2 слота и давало скипы). Классный час — Чт: слот 1 для 1-й
        // смены, слот 7 для 2-й (в допуске пользователя «6 или 7»; совпадает
        // с реальными 6-ми классами).
        var bands = data.Classes.ToDictionary(
            c => c.Id,
            c => (IReadOnlyList<int>)(c.Grade is 6 or 7
                ? Enumerable.Range(6, 7).ToList()
                : Enumerable.Range(1, 7).ToList()));
        // Классный час из нагрузки → ClassTeacherId + CommonLesson-пин
        // (иначе час плавает по неделе — жалоба 21.09). Строки к/ч из
        // curriculum убираем (синтез создаст свои occurrence).
        var hourName = "Классный час";
        var hourSubjId = data.Subjects.First(
            s => string.Equals(s.Name, hourName, StringComparison.OrdinalIgnoreCase)).Id;
        var hourItems = data.Curriculum.Where(i => i.SubjectId == hourSubjId).ToList();
        foreach (var item in hourItems)
        {
            var cls = data.Classes.First(c => c.Id == item.ClassId);
            cls.ClassTeacherId = item.TeacherId;
        }
        var curriculum = data.Curriculum.Where(i => i.SubjectId != hourSubjId).ToList();
        var input = new ProblemInput(
            data.Classes, data.Teachers, data.Subjects, curriculum,
            data.Groups, data.DaysOff, data.Unavailability,
            DaysCount: 5, SlotsPerDay: 14,
            SplitTeachers: new Dictionary<Guid, (Guid, Guid)>(data.SplitTeachers),
            rooms: data.Rooms,
            classSlots: bands,
            commonLesson: new CommonLesson
            {
                Enabled = true, DayIndex = 3, SlotIndex = 1, SlotIndexShift2 = 7,
                GradesCsv = "5,6,7,8,9,10,11", UseOwnRooms = true,
            });
        var (problem, errors) = ProblemBuilder.Build(input,
            new SolverOptions(MaxTimeSeconds: 8, NumSearchWorkers: 1,
                RandomSeed: 11, PresolveInPhaseA: false));
        Assert.Empty(errors);
        return problem!;
    }

    [Fact]
    public void RealTeachers_Import_Shape()
    {
        var data = LoadSchool();
        output.WriteLine(data.Notes[^1]);
        Assert.Equal(24, data.Classes.Count);
        Assert.Equal(396, data.Curriculum.Count);
        Assert.Contains(data.Teachers, t => t.Name == "Бадеева Е.В.");
        Assert.Contains(data.Teachers, t => t.Name == "Жилко Л.В.");
        Assert.DoesNotContain(data.Teachers, t => t.Name.Contains("·"));
        int vacRows = data.Curriculum.Count(i =>
            data.Teachers.First(t => t.Id == i.TeacherId).Name.StartsWith("ВАКАНСИЯ"));
        output.WriteLine($"REAL vacancy rows: {vacRows}");
    }

    [Fact]
    public void RealTeachers_Greedy_Coverage()
    {
        var data = LoadSchool();
        var problem = BuildProblem(data);
        var g = GreedyPlacer.Place(problem, 11);
        output.WriteLine($"REAL greedy: placed {g.Placed.Count}/{problem.Occurrences.Count}");
        var bySubject = g.Unplaced
            .Select(id => problem.Occurrences.First(o => o.Id == id))
            .GroupBy(o => problem.Subjects[o.SubjectId].Name)
            .Select(grp => (Name: grp.Key, Count: grp.Count()))
            .OrderByDescending(x => x.Count).ToList();
        foreach (var (name, count) in bySubject.Take(12))
            output.WriteLine($"REAL greedy unplaced: {name} — {count}");
        var byTeacher = g.Unplaced
            .Select(id => problem.Occurrences.First(o => o.Id == id))
            .GroupBy(o => problem.Teachers[o.TeacherId].Name)
            .Select(grp => (Name: grp.Key, Count: grp.Count()))
            .OrderByDescending(x => x.Count).ToList();
        foreach (var (name, count) in byTeacher.Take(12))
            output.WriteLine($"REAL greedy unplaced teacher: {name} — {count}");
        var byClass = g.Unplaced
            .Select(id => problem.Occurrences.First(o => o.Id == id))
            .GroupBy(o => problem.Classes[o.ClassId].Name + "|" +
                problem.Subjects[o.SubjectId].Name)
            .Select(grp => (Name: grp.Key, Count: grp.Count()))
            .OrderByDescending(x => x.Count).ToList();
        foreach (var (name, count) in byClass.Take(14))
            output.WriteLine($"REAL greedy unplaced cell: {name} — {count}");
        var vacLeft = g.Unplaced
            .Select(id => problem.Occurrences.First(o => o.Id == id))
            .Count(o => problem.Teachers[o.TeacherId].Name.StartsWith("ВАКАНСИЯ"));
        output.WriteLine($"REAL greedy unplaced vacancy: {vacLeft}/{g.Unplaced.Count}");
        // FIX 22.09: классный час обязан стоять Чт-1 (1-я смена) / Чт-7 (2-я).
        // Проверка уровня build (детерминирована): у всех часов Allowed один.
        var hourId = problem.Subjects.Values
            .First(s => string.Equals(s.Name, "Классный час", StringComparison.OrdinalIgnoreCase)).Id;
        var hours = problem.Occurrences.Where(o => o.SubjectId == hourId).ToList();
        output.WriteLine($"REAL class-hour occurrences: {hours.Count}");
        Assert.Equal(24, hours.Count);
        foreach (var h in hours)
        {
            var cls = problem.Classes[h.ClassId];
            int expectSlot = cls.Grade is 6 or 7 ? 7 : 1;
            Assert.Equal([3], problem.AllowedDays[h.Id]);
            Assert.Equal([expectSlot], problem.AllowedSlots[h.Id]);
        }
        var hourUnplaced = g.Unplaced.Count(id =>
            problem.Occurrences.First(o => o.Id == id).SubjectId == hourId);
        output.WriteLine($"REAL greedy unplaced class-hour: {hourUnplaced}");
        Assert.Equal(0, hourUnplaced);
        var placements = g.Placed.Select(kv => new PlacedLesson
        {
            OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
            SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
        }).ToList();
        var repaired = CompactRepair.Repair(problem, placements);
        var vrRaw = PlacementValidator.Validate(problem, placements);
        var vrRep = PlacementValidator.Validate(problem, repaired.Placements);
        int GapCount(ValidationResult r) => r.HardViolations.Count(v =>
            v.Code is "student-gap" or "student-late-start");
        output.WriteLine($"REAL gaps raw={GapCount(vrRaw)} repaired={GapCount(vrRep)}");
        Assert.True(GapCount(vrRep) < GapCount(vrRaw), "Repair не уменьшил окна.");
        // Замер 22.09.2026 вечер (FIX: настоящие учителя из заполненные/Учителя,
        // смены 6–12, к/ч пин Чт-1/Чт-7, Бесфамильно/Денискин разданы,
        // ХимП 10А + ХимБ 11А → Липницкая, матем 11А 5→4):
        // 864/890 (97.1%), вакансий 0/26, к/ч 24/24 на месте.
        // Остаток 26 — greedy-хвосты (профильные пары 10А/11А + рассеянные).
        // ВАЖНО: 10А и 11А при лимите класса 8/день (40 слотов) структурно
        // не влезали независимыми occurrence (47/51): их профильные пары «X/Y»
        // формат Load выразить не может (Splits v2 — backlog, см.
        // PhotoSchoolTests). 100% всего расписания получено ручным профильным
        // спариванием 10А/11А в коде (разовый прогон ScratchGen 22.09.2026:
        // 888/888, hard=0, soft=2196, файл Сгенерировано_движком_ФИНАЛ.xlsx).
        // Порог 800: ниже — регресс покрытия реальных данных.
        Assert.True(g.Placed.Count >= 800,
            $"Greedy разместил {g.Placed.Count} — ниже порога 800.");
    }
}
