using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// P-SANPIN-CHECK — чекер как отчёт: находит, считает distinct-слоты, уважает лимиты.
public sealed class SanPinCheckerTests
{
    private static SchedulingProblem Build(
        string className, int grade, (Subject Subj, int Hours, Teacher Teacher)[] load,
        int days = 2, int slots = 8)
    {
        var cls = new SchoolClass
        {
            AcademicYearId = Guid.NewGuid(), Name = className, Grade = grade, StudentCount = 25
        };
        var subjects = load.Select(x => x.Subj).ToList();
        var teachers = load.Select(x => x.Teacher).ToList();
        var items = load.Select(x => new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = x.Subj.Id, TeacherId = x.Teacher.Id,
            HoursPerWeek = x.Hours
        }).ToList();
        var input = new ProblemInput([cls], teachers, subjects, items,
            [], [], [], DaysCount: days, SlotsPerDay: slots);
        var (p, e) = ProblemBuilder.Build(input,
            new SolverOptions(MaxTimeSeconds: 5, NumSearchWorkers: 1, RandomSeed: 1));
        Assert.Empty(e);
        return p!;
    }

    private static Teacher T(string n) => new() { Name = n, MaxLessonsPerDay = 6 };

    // --- 1. Перегруз дня — ошибка с нормой и NEEDS-CHECK ---
    [Fact]
    public void OverloadDay_ErrorWithNeedsCheck()
    {
        var t = T("Иванов");
        var p = Build("5А", 5, [(new Subject { Name = "Мат", MaxPerDay = 7 }, 7, t)]);
        var placements = p.Occurrences.Select((o, i) => new PlacedLesson
        {
            OccurrenceId = o.Id, DayIndex = 0, SlotIndex = i + 1 // 7 distinct day0, cap 6
        }).ToList();
        var findings = SanPinChecker.Check(p, placements);
        var err = Assert.Single(findings, f => f.IsError);
        Assert.Contains("5А", err.Text);
        Assert.Contains("6/день", err.Text);
        Assert.True(err.NeedsCheck);
    }

    // --- 2. Лимиты — данные: школа перенастраивает без кода ---
    [Fact]
    public void CustomLimits_OverrideRespected()
    {
        var t = T("Иванов");
        var p = Build("5А", 5, [(new Subject { Name = "Мат", MaxPerDay = 7 }, 7, t)]);
        var placements = p.Occurrences.Select((o, i) => new PlacedLesson
        {
            OccurrenceId = o.Id, DayIndex = 0, SlotIndex = i + 1
        }).ToList();
        var findings = SanPinChecker.Check(p, placements,
            SanPinLimits.Default with { Grade56Max = 8 });
        Assert.Empty(findings.Where(f => f.IsError));
    }

    // --- 3. 1-й класс: два 5-урочных дня — ошибка ---
    [Fact]
    public void Grade1_TwoFiveDays_Error()
    {
        var t = T("Иванова");
        var p = Build("1А", 1, [(new Subject { Name = "Чтение", MaxPerDay = 5 }, 10, t)]);
        var placements = p.Occurrences.Select((o, i) => new PlacedLesson
        {
            OccurrenceId = o.Id, DayIndex = i < 5 ? 0 : 1, SlotIndex = (i % 5) + 1
        }).ToList();
        var findings = SanPinChecker.Check(p, placements);
        Assert.Contains(findings,
            f => f.IsError && f.Text.Contains("1А") && f.Text.Contains("не более 1"));
    }

    // --- 4. Физра первым уроком — предупреждение, не ошибка ---
    [Fact]
    public void PeFirst_WarningOnly()
    {
        var t = T("Сидоров");
        var pe = new Subject
        {
            Name = "Физическая культура и здоровье", MaxPerDay = 2, IsPhysicalEducation = true
        };
        var p = Build("5А", 5, [(pe, 2, t)]);
        int anchor = StudentCompactness.AnchorFor(p, p.Classes.Values.Single().Id);
        var placements = p.Occurrences.Select((o, i) => new PlacedLesson
        {
            OccurrenceId = o.Id, DayIndex = 0, SlotIndex = i == 0 ? anchor : anchor + 1
        }).ToList();
        var findings = SanPinChecker.Check(p, placements);
        Assert.DoesNotContain(findings, f => f.IsError);
        Assert.Contains(findings,
            f => !f.IsError && f.Text.Contains("первым уроком") && f.NeedsCheck);
    }

    // --- 5. Чистое расписание — пусто ---
    [Fact]
    public void CleanSchedule_Empty()
    {
        var t = T("Иванов");
        var p = Build("5А", 5, [(new Subject { Name = "Мат", MaxPerDay = 2 }, 2, t)],
            days: 2, slots: 3);
        var placements = p.Occurrences.Select((o, i) => new PlacedLesson
        {
            OccurrenceId = o.Id, DayIndex = 0, SlotIndex = i + 1
        }).ToList();
        Assert.Empty(SanPinChecker.Check(p, placements));
    }
}
