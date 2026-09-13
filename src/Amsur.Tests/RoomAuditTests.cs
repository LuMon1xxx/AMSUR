using Amsur.Domain;
using Amsur.Scheduling.Core;
using Amsur.Scheduling.OrTools;

namespace Amsur.Tests;

// P0.5 §2 Room audit. TDD RED: эти тесты фиксируют ТРЕБУЕМОЕ поведение SPEC
// (Required/Forbidden/capacity влияют на feasibility) — сейчас падают, доказывая gap.
public sealed class RoomAuditTests
{
    private static (SchedulingProblem Problem, Room Room) Bottleneck()
    {
        // 2 класса × 1 урок, сетка 1 день × 1 слот (пересечение по времени вынуждено),
        // 1 кабинет cap=1 → room-реальность: INFEASIBLE.
        var clsA = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5, StudentCount = 25 };
        var clsB = new SchoolClass { AcademicYearId = clsA.AcademicYearId, Name = "5Б", Grade = 5, StudentCount = 25 };
        var tA = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var tB = new Teacher { Name = "Петрова", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Математика", MaxPerDay = 2 };
        var room = new Room { Name = "101", PhysicalCapacity = 30, MaxSimultaneousGroups = 1 };
        var ia = new CurriculumItem { ClassId = clsA.Id, SubjectId = math.Id, TeacherId = tA.Id, HoursPerWeek = 1 };
        var ib = new CurriculumItem { ClassId = clsB.Id, SubjectId = math.Id, TeacherId = tB.Id, HoursPerWeek = 1 };
        var input = new ProblemInput([clsA, clsB], [tA, tB], [math], [ia, ib],
            [], [], [], DaysCount: 1, SlotsPerDay: 1, rooms: [room]);
        var (problem, errors) = ProblemBuilder.Build(input,
            new SolverOptions(MaxTimeSeconds: 10, NumSearchWorkers: 1, RandomSeed: 5));
        Assert.Empty(errors);
        return (problem!, room);
    }

    [Fact]
    public async Task RoomBottleneck_ForcedOverlap_IsInfeasible()
    {
        var (problem, _) = Bottleneck();
        var result = await new OrToolsSolver().SolveAsync(problem);
        // SPEC: один кабинет cap=1 на два вынужденно-одновременных урока → infeasible.
        Assert.Equal(SolverStatus.Infeasible, result.Status);
        Assert.Empty(result.Placements);
    }

    [Fact]
    public async Task RoomsAssigned_WhenAvailable_ValidatorClean()
    {
        var (problem, _) = Bottleneck();
        // Добавляем второй кабинет: задача становится room-feasible.
        var room2 = new Room { Name = "102", PhysicalCapacity = 30, MaxSimultaneousGroups = 1 };
        problem.Rooms[room2.Id] = room2;
        var result = await new OrToolsSolver().SolveAsync(problem);
        Assert.Equal(SolverStatus.Feasible, result.Status);
        Assert.All(result.Placements, p => Assert.NotNull(p.RoomId));
        Assert.True(PlacementValidator.Validate(problem, result.Placements).IsValid);
    }

    [Fact]
    public void ForbiddenRoom_Placement_IsHard()
    {
        var (problem, room) = Bottleneck();
        var occ = problem.Occurrences[0];
        problem.RoomCaps[(room.Id, occ.SubjectId)] = RoomCapabilityKind.Forbidden;
        var placements = new List<PlacedLesson>
        {
            new() { OccurrenceId = problem.Occurrences[0].Id, DayIndex = 0, SlotIndex = 1, RoomId = room.Id },
            new() { OccurrenceId = problem.Occurrences[1].Id, DayIndex = 0, SlotIndex = 1, RoomId = room.Id },
        };
        var r = PlacementValidator.Validate(problem, placements);
        Assert.Contains(r.HardViolations, v => v.Code == "forbidden-room");
    }
}
