using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// Причина «нет расписания» должна быть названа цифрами, а не молчанием.
public sealed class CapacityDiagnosticsTests
{
    private static SchedulingProblem Build(
        int classes, int slotsPerDay, int days, int hoursPerClass,
        int classLimit = 7, int teacherLimit = 6, int teachersPerClass = 1)
    {
        var cls = Enumerable.Range(1, classes).Select(i => new SchoolClass
        {
            AcademicYearId = Guid.NewGuid(), Name = $"К{i}", Grade = 5, StudentCount = 25,
            MaxLessonsPerDay = classLimit
        }).ToList();
        var teachers = Enumerable.Range(0, classes * teachersPerClass).Select(i => new Teacher
        {
            Name = $"У{i}", MaxLessonsPerDay = teacherLimit
        }).ToList();
        var subject = new Subject { Name = "Предмет", MaxPerDay = 8 };
        var curriculum = new List<CurriculumItem>();
        for (int c = 0; c < classes; c++)
            curriculum.Add(new CurriculumItem
            {
                ClassId = cls[c].Id, SubjectId = subject.Id,
                TeacherId = teachers[c * teachersPerClass].Id, HoursPerWeek = hoursPerClass
            });

        var input = new ProblemInput(cls, teachers, [subject], curriculum,
            [], [], [], DaysCount: days, SlotsPerDay: slotsPerDay);
        var (problem, _) = ProblemBuilder.Build(input);
        return problem!;
    }

    [Fact]
    public void OverloadedClass_IsNamedWithNumbers()
    {
        // 40 уроков на класс при лимите 7 × 5 = 35. Учитель не узкое место (8 × 5 = 40).
        var problem = Build(classes: 2, slotsPerDay: 7, days: 5, hoursPerClass: 40,
            teacherLimit: 8);
        var lines = CapacityDiagnostics.Analyze(problem);
        var line = Assert.Single(lines, l => l.Contains("К1"));
        Assert.Contains("40", line);
        Assert.Contains("35", line);
    }

    [Fact]
    public void FitsExactly_NoLines()
    {
        var problem = Build(classes: 2, slotsPerDay: 7, days: 5, hoursPerClass: 35,
            teacherLimit: 8);
        Assert.Empty(CapacityDiagnostics.Analyze(problem));
        Assert.Null(CapacityDiagnostics.EmptyReason(problem));
    }

    [Fact]
    public void OverloadedTeacher_IsNamed()
    {
        // 40 ч у одного учителя при лимите 6 × 5 = 30. Класс вмещает (8 × 5 = 40).
        var problem = Build(classes: 1, slotsPerDay: 8, days: 5, hoursPerClass: 40,
            classLimit: 8, teacherLimit: 6);
        var lines = CapacityDiagnostics.Analyze(problem);
        var line = Assert.Single(lines);
        Assert.Contains("Учитель", line);
        Assert.Contains("40", line);
        Assert.Contains("30", line);
    }

    [Fact]
    public void RelaxedClassLimit_NotReported()
    {
        var problem = Build(classes: 1, slotsPerDay: 7, days: 5, hoursPerClass: 40);
        var relaxed = EffectiveRuleSet.Default with
        {
            RelaxedStrict = new HashSet<string>(StringComparer.Ordinal) { "class-maxperday" }
        };
        Assert.DoesNotContain(CapacityDiagnostics.Analyze(problem, relaxed),
            l => l.StartsWith("К1"));
    }

    [Fact]
    public void EmptyReason_MentionsImpossibility()
    {
        var problem = Build(classes: 1, slotsPerDay: 6, days: 5, hoursPerClass: 40);
        var reason = CapacityDiagnostics.EmptyReason(problem);
        Assert.NotNull(reason);
        Assert.Contains("не помещается", reason);
        Assert.Contains("40", reason);
    }

    [Fact]
    public void PanelHeader_ShowsReasonWhenEmpty()
    {
        var panel = new Amsur.Application.Top5PanelModel
        {
            TotalFound = 0,
            EmptyHint = "Расписание не помещается в учебную неделю — Класс 10А: 47.",
        };
        Assert.Contains("не помещается", panel.HeaderText);
        Assert.Contains("10А", panel.HeaderText);
    }

    [Fact]
    public void ManyOverloads_LimitedAndSortedBySeverity()
    {
        var problem = Build(classes: 8, slotsPerDay: 6, days: 5, hoursPerClass: 40,
            classLimit: 6);
        var lines = CapacityDiagnostics.Analyze(problem);
        Assert.Equal(CapacityDiagnostics.MaxLines, lines.Count);
    }

    [Fact]
    public void PanelHeader_StaysShortWithoutHint()
    {
        var panel = new Amsur.Application.Top5PanelModel { TotalFound = 0 };
        Assert.Equal("Подходящих вариантов пока нет", panel.HeaderText);
    }
}

