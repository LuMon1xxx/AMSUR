using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// P0 фикстуры корректности (детерминированные, без solver).
// SPEC §31: TinyFeasible, TeacherConflict, ClassConflict, Subgroups2, StudentWindow...
public sealed class ValidatorTests
{
    private static (SchedulingProblem, Dictionary<Guid, LessonOccurrence>) Tiny(
        bool split = false, Guid? syncId = null, Guid? teacherB = null)
    {
        var classId = Guid.NewGuid();
        var teacherA = Guid.NewGuid();
        var subject = Guid.NewGuid();
        var occ1 = new LessonOccurrence
        {
            ClassId = classId, SubjectId = subject, TeacherId = teacherA,
            CurriculumItemId = Guid.NewGuid(), SyncGroupId = syncId
        };
        var occ2 = new LessonOccurrence
        {
            ClassId = classId, SubjectId = subject,
            TeacherId = teacherB ?? teacherA,
            CurriculumItemId = Guid.NewGuid(), SyncGroupId = syncId
        };
        if (split)
        {
            var gA = Guid.NewGuid(); var gB = Guid.NewGuid();
            occ1.GroupId = gA; occ2.GroupId = gB;
        }
        var problem = new SchedulingProblem
        {
            Occurrences = [occ1, occ2],
            Classes = new() { [classId] = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А" } },
            Teachers = new()
            {
                [teacherA] = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 },
            },
            Subjects = new() { [subject] = new Subject { Name = "Математика" } },
            AllowedSlots = new() { [occ1.Id] = [1, 2, 3], [occ2.Id] = [1, 2, 3] },
            AllowedDays = new() { [occ1.Id] = [0], [occ2.Id] = [0] },
        };
        if (teacherB.HasValue)
            problem.Teachers[teacherB.Value] = new Teacher { Name = "Петрова", MaxLessonsPerDay = 6 };
        return (problem, new() { [occ1.Id] = occ1, [occ2.Id] = occ2 });
    }

    [Fact]
    public void TinyFeasible_NoHardViolations()
    {
        var (problem, _) = Tiny();
        var occ = problem.Occurrences;
        var placements = new List<PlacedLesson>
        {
            new() { OccurrenceId = occ[0].Id, DayIndex = 0, SlotIndex = 1 },
            new() { OccurrenceId = occ[1].Id, DayIndex = 0, SlotIndex = 2 },
        };
        Assert.Empty(InputValidator.Validate(problem));
        var r = PlacementValidator.Validate(problem, placements);
        Assert.True(r.IsValid);
    }

    [Fact]
    public void TeacherConflict_IsHard()
    {
        var (problem, _) = Tiny(); // один учитель оба occurrence
        var occ = problem.Occurrences;
        var placements = new List<PlacedLesson>
        {
            new() { OccurrenceId = occ[0].Id, DayIndex = 0, SlotIndex = 1 },
            new() { OccurrenceId = occ[1].Id, DayIndex = 0, SlotIndex = 1 },
        };
        var r = PlacementValidator.Validate(problem, placements);
        Assert.Contains(r.HardViolations, v => v.Code == PhysicalRuleCodes.TeacherCollision);
    }

    [Fact]
    public void ClassConflict_IsHard()
    {
        var tB = Guid.NewGuid();
        var (problem, _) = Tiny(teacherB: tB); // разные учителя, один класс, один слот
        var occ = problem.Occurrences;
        var placements = new List<PlacedLesson>
        {
            new() { OccurrenceId = occ[0].Id, DayIndex = 0, SlotIndex = 1 },
            new() { OccurrenceId = occ[1].Id, DayIndex = 0, SlotIndex = 1 },
        };
        var r = PlacementValidator.Validate(problem, placements);
        Assert.Contains(r.HardViolations, v => v.Code == PhysicalRuleCodes.GroupCollision);
    }

    [Fact]
    public void Subgroups2_SameTime_DifferentTeachers_IsValid()
    {
        var sync = Guid.NewGuid();
        var tB = Guid.NewGuid();
        var (problem, _) = Tiny(split: true, syncId: sync, teacherB: tB);
        var occ = problem.Occurrences;
        var placements = new List<PlacedLesson>
        {
            new() { OccurrenceId = occ[0].Id, DayIndex = 0, SlotIndex = 1 },
            new() { OccurrenceId = occ[1].Id, DayIndex = 0, SlotIndex = 1 },
        };
        Assert.Empty(InputValidator.Validate(problem));
        var r = PlacementValidator.Validate(problem, placements);
        Assert.True(r.IsValid);
    }

    [Fact]
    public void Subgroups2_SyncSplit_IsHard()
    {
        var sync = Guid.NewGuid();
        var tB = Guid.NewGuid();
        var (problem, _) = Tiny(split: true, syncId: sync, teacherB: tB);
        var occ = problem.Occurrences;
        var placements = new List<PlacedLesson>
        {
            new() { OccurrenceId = occ[0].Id, DayIndex = 0, SlotIndex = 1 },
            new() { OccurrenceId = occ[1].Id, DayIndex = 0, SlotIndex = 2 },
        };
        var r = PlacementValidator.Validate(problem, placements);
        Assert.Contains(r.HardViolations, v => v.Code == PhysicalRuleCodes.SubgroupSync);
    }

    [Fact]
    public void StudentWindow_IsHard_D28()
    {
        var (problem, _) = Tiny();
        var occ = problem.Occurrences;
        var placements = new List<PlacedLesson>
        {
            new() { OccurrenceId = occ[0].Id, DayIndex = 0, SlotIndex = 1 },
            new() { OccurrenceId = occ[1].Id, DayIndex = 0, SlotIndex = 3 }, // окно
        };
        var r = PlacementValidator.Validate(problem, placements);
        Assert.Contains(r.HardViolations, v => v.Code == "student-gap"); // D-28: окно — hard
        var b = SoftEvaluator.Evaluate(problem, placements);
        Assert.Equal(RuleCatalog.StudentGap, b.Components.First(c => c.Code == "student-gap").Value);
    }

    [Fact]
    public void PhysicalRule_CannotBeSoft()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PhysicalRuleCodes.EnsureSeverity(PhysicalRuleCodes.TeacherCollision, RuleSeverity.Soft));
    }

    [Fact]
    public void TeacherMaxPerDay_FrozenAsHard_D4()
    {
        var (problem, _) = Tiny();
        var teacherId = problem.Occurrences[0].TeacherId;
        problem.Teachers[teacherId].MaxLessonsPerDay = 1;
        var occ = problem.Occurrences;
        var placements = new List<PlacedLesson>
        {
            new() { OccurrenceId = occ[0].Id, DayIndex = 0, SlotIndex = 1 },
            new() { OccurrenceId = occ[1].Id, DayIndex = 0, SlotIndex = 2 },
        };
        var r = PlacementValidator.Validate(problem, placements);
        Assert.Contains(r.HardViolations, v => v.Code == "teacher-maxperday");
    }

    [Fact]
    public void StatusMapper_Timeout_IsNotInfeasible()
    {
        Assert.Equal(UserScheduleStatus.NoSolutionFoundWithinLimit,
            PlacementValidator.ToUserStatus(SolverStatus.Unknown, false, false));
        Assert.Equal(UserScheduleStatus.CancelledAfterFeasible,
            PlacementValidator.ToUserStatus(SolverStatus.Unknown, true, true));
        Assert.Equal(UserScheduleStatus.InfeasibleConfirmedByModel,
            PlacementValidator.ToUserStatus(SolverStatus.Infeasible, false, false));
    }
}
