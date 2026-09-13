using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// TDD RED: ProblemBuilder пока не существует — тесты должны упасть компиляцией/отсутствием.
public sealed class ProblemBuilderTests
{
    private static (SchoolClass cls, Teacher teacher, Subject subject, CurriculumItem item) Mini()
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5 };
        var teacher = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var subject = new Subject { Name = "Математика", MaxPerDay = 1 };
        var item = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = subject.Id, TeacherId = teacher.Id, HoursPerWeek = 2
        };
        return (cls, teacher, subject, item);
    }

    [Fact]
    public void Build_ExpandsHoursIntoOccurrences()
    {
        var (cls, teacher, subject, item) = Mini();
        var input = new ProblemInput(
            [cls], [teacher], [subject], [item],
            [], [], [], DaysCount: 5, SlotsPerDay: 4);
        var (problem, errors) = ProblemBuilder.Build(input);
        Assert.Empty(errors);
        Assert.NotNull(problem);
        Assert.Equal(2, problem!.Occurrences.Count);
        Assert.All(problem.Occurrences, o => Assert.Equal(teacher.Id, o.TeacherId)); // INV-02
    }

    [Fact]
    public void Build_SplitCreatesSyncPairs()
    {
        var (cls, teacher, subject, item) = Mini();
        item.HoursPerWeek = 1;
        item.SplitSubgroups = true;
        var teacherB = new Teacher { Name = "Петрова", MaxLessonsPerDay = 6 };
        var gA = new StudentGroup { ClassId = cls.Id, Name = "A" };
        var gB = new StudentGroup { ClassId = cls.Id, Name = "B" };
        var input = new ProblemInput(
            [cls], [teacher, teacherB], [subject], [item],
            [gA, gB], [], [],
            DaysCount: 5, SlotsPerDay: 4,
            SplitTeachers: new Dictionary<Guid, (Guid, Guid)> { [item.Id] = (teacher.Id, teacherB.Id) });
        var (problem, errors) = ProblemBuilder.Build(input);
        Assert.Empty(errors);
        Assert.Equal(2, problem!.Occurrences.Count);
        Assert.NotNull(problem.Occurrences[0].SyncGroupId);
        Assert.Equal(problem.Occurrences[0].SyncGroupId, problem.Occurrences[1].SyncGroupId);
        Assert.NotEqual(problem.Occurrences[0].TeacherId, problem.Occurrences[1].TeacherId);
    }

    [Fact]
    public void Build_TeacherDayOff_RemovesDay()
    {
        var (cls, teacher, subject, item) = Mini();
        item.HoursPerWeek = 1;
        var input = new ProblemInput(
            [cls], [teacher], [subject], [item],
            [], [new TeacherDayOff { TeacherId = teacher.Id, DayIndex = 0 }], [],
            DaysCount: 2, SlotsPerDay: 3);
        var (problem, errors) = ProblemBuilder.Build(input);
        Assert.Empty(errors);
        var occ = problem!.Occurrences[0];
        Assert.DoesNotContain(0, problem.AllowedDays[occ.Id]);
        Assert.Contains(1, problem.AllowedDays[occ.Id]);
    }

    [Fact]
    public void Build_NoCommonSyncTime_IsError()
    {
        var (cls, teacher, subject, item) = Mini();
        item.HoursPerWeek = 1;
        item.SplitSubgroups = true;
        var teacherB = new Teacher { Name = "Петрова", MaxLessonsPerDay = 6 };
        var gA = new StudentGroup { ClassId = cls.Id, Name = "A" };
        var gB = new StudentGroup { ClassId = cls.Id, Name = "B" };
        var input = new ProblemInput(
            [cls], [teacher, teacherB], [subject], [item],
            [gA, gB],
            [
                new TeacherDayOff { TeacherId = teacher.Id, DayIndex = 1 },
                new TeacherDayOff { TeacherId = teacherB.Id, DayIndex = 0 },
            ],
            [],
            DaysCount: 2, SlotsPerDay: 3,
            SplitTeachers: new Dictionary<Guid, (Guid, Guid)> { [item.Id] = (teacher.Id, teacherB.Id) });
        var (problem, errors) = ProblemBuilder.Build(input);
        Assert.Null(problem);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Build_ThreeGroups_FailsLoud_NoSilentTruncation()
    {
        var (cls, teacher, subject, item) = Mini();
        item.HoursPerWeek = 1;
        item.SplitSubgroups = true;
        var teacherB = new Teacher { Name = "Петрова", MaxLessonsPerDay = 6 };
        var groups = new[]
        {
            new StudentGroup { ClassId = cls.Id, Name = "A" },
            new StudentGroup { ClassId = cls.Id, Name = "B" },
            new StudentGroup { ClassId = cls.Id, Name = "C" },
        };
        var input = new ProblemInput(
            [cls], [teacher, teacherB], [subject], [item],
            groups, [], [],
            DaysCount: 2, SlotsPerDay: 3,
            SplitTeachers: new Dictionary<Guid, (Guid, Guid)> { [item.Id] = (teacher.Id, teacherB.Id) });
        var (problem, errors) = ProblemBuilder.Build(input);
        Assert.Null(problem);
        Assert.Contains(errors, e => e.Contains("Splits v2"));
    }
}
