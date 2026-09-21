using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// Реальный пилот СШ №8 (5–11): нагрузка из фото + НАСТОЯЩИЕ учителя из
// сверки (кросс-чек клеток, см. данные/тест_реал/). Непокрытые уроки —
// ВАКАНСИЯ (честные дыры школы: физика 10-х, астрономия 11-х и др.).
// Смены: 5,8,9,10,11 — 1-я (слоты 1–7); 6,7 — 2-я (слоты 8–14).
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
        var bands = data.Classes.ToDictionary(
            c => c.Id,
            c => (IReadOnlyList<int>)(c.Grade is 6 or 7
                ? Enumerable.Range(8, 7).ToList()
                : Enumerable.Range(1, 7).ToList()));
        var input = new ProblemInput(
            data.Classes, data.Teachers, data.Subjects, data.Curriculum,
            data.Groups, data.DaysOff, data.Unavailability,
            DaysCount: 5, SlotsPerDay: 14,
            SplitTeachers: new Dictionary<Guid, (Guid, Guid)>(data.SplitTeachers),
            rooms: data.Rooms,
            classSlots: bands);
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
        // Замер 20.09.2026: 819/893 (91.7%). Ниже плейсхолдерных 878/884 —
        // настоящие учителя конфликтуют (нагрузка, кабинеты), это нормально:
        // 56 вакансий + 18 рассеянных greedy-хвостов (solver доберёт).
        // Порог 800: ниже — регресс покрытия реальных данных.
        Assert.True(g.Placed.Count >= 800,
            $"Greedy разместил {g.Placed.Count} — ниже порога 800.");
    }
}
