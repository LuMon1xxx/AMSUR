using Amsur.Domain;
using Amsur.Scheduling.Core;
using Amsur.Scheduling.OrTools;

namespace Amsur.Tests;

// EPIC-B B1/B3: solver feasibility + статусы + reproducibility (P0 scope).
public sealed class SolverTests
{
    private static SchedulingProblem Tiny(int days = 2, int slots = 4, int hours = 3, int? seed = 1)
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5 };
        var t = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var s = new Subject { Name = "Математика", MaxPerDay = 2 };
        var item = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = s.Id, TeacherId = t.Id, HoursPerWeek = hours
        };
        var input = new ProblemInput([cls], [t], [s], [item], [], [], [], days, slots);
        var (problem, errors) = ProblemBuilder.Build(input,
            new SolverOptions(MaxTimeSeconds: 20, NumSearchWorkers: 1, RandomSeed: seed));
        Assert.Empty(errors);
        return problem!;
    }

    [Fact]
    public async Task TinyFeasible_HardZero_And_ValidatorGate()
    {
        var problem = Tiny();
        var solver = new OrToolsSolver();
        var result = await solver.SolveAsync(problem);
        Assert.Equal(SolverStatus.Feasible, result.Status);
        Assert.Equal(0, result.HardViolations);
        Assert.Equal(3, result.Placements.Count);
        // Gate: независимая проверка тем же FullValidator.
        var check = PlacementValidator.Validate(problem, result.Placements);
        Assert.True(check.IsValid);
        // E12 greedy fast-path может дать feasible за <1мс: ноль — честное значение.
        Assert.True(result.FirstFeasibleMs >= 0);
        Assert.True(result.FirstFeasibleMs <= result.ElapsedMs);
    }

    [Fact]
    public async Task Repro_Workers1Seed_BitIdentical()
    {
        var solver = new OrToolsSolver();
        var a = await solver.SolveAsync(Tiny(seed: 7));
        var b = await solver.SolveAsync(Tiny(seed: 7));
        Assert.Equal(a.Status, b.Status);
        // Occurrence Guids различаются между сборками — сравниваем мультимножество времён.
        var pa = a.Placements.Select(p => (p.DayIndex, p.SlotIndex)).Order().ToList();
        var pb = b.Placements.Select(p => (p.DayIndex, p.SlotIndex)).Order().ToList();
        Assert.Equal(pa, pb);
    }

    [Fact]
    public async Task DenseTiny_Overconstrained_IsInfeasibleOrUnknown_NotFeasible()
    {
        // 1 день × 1 слот, 2 часа одного учителя — решения нет.
        var problem = Tiny(days: 1, slots: 1, hours: 2);
        var solver = new OrToolsSolver();
        var result = await solver.SolveAsync(problem);
        Assert.True(result.Status is SolverStatus.Infeasible or SolverStatus.Unknown,
            $"Unexpected feasible: {result.Status}");
        Assert.Empty(result.Placements);
        var user = PlacementValidator.ToUserStatus(
            result.Status == SolverStatus.Infeasible ? SolverStatus.Infeasible : SolverStatus.Unknown,
            false, false);
        Assert.True(user is UserScheduleStatus.InfeasibleConfirmedByModel
            or UserScheduleStatus.NoSolutionFoundWithinLimit);
    }

    [Fact]
    public async Task CancelledBeforeFeasible_PreservesNothing_ButHonestStatus()
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5 };
        var t = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var s = new Subject { Name = "Математика", MaxPerDay = 2 };
        var item = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = s.Id, TeacherId = t.Id, HoursPerWeek = 3
        };
        var input = new ProblemInput([cls], [t], [s], [item], [], [], [], 2, 4);
        var (problem, errors) = ProblemBuilder.Build(input,
            new SolverOptions(MaxTimeSeconds: 60, NumSearchWorkers: 1, RandomSeed: 1));
        Assert.Empty(errors);
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // отмена до старта
        var solver = new OrToolsSolver();
        var result = await solver.SolveAsync(problem!, cts.Token);
        Assert.True(result.WasCancelled);
        // INV-08: отмена сохраняет best-so-far. Tiny решается мгновенно, поэтому
        // допустимы оба честных исхода; placements обязаны быть валидны или отсутствовать.
        if (result.Placements.Count > 0)
        {
            Assert.True(PlacementValidator.Validate(problem!, result.Placements).IsValid);
            Assert.Equal(UserScheduleStatus.CancelledAfterFeasible,
                PlacementValidator.ToUserStatus(result.Status, true, true));
        }
        else
        {
            Assert.Equal(UserScheduleStatus.CancelledWithoutFeasible,
                PlacementValidator.ToUserStatus(SolverStatus.Unknown, true, false));
        }
    }

    [Fact]
    public async Task SyncSplit_SolverRespectsSharedStart()
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "8А", Grade = 8 };
        var tA = new Teacher { Name = "A", MaxLessonsPerDay = 6 };
        var tB = new Teacher { Name = "B", MaxLessonsPerDay = 6 };
        var s = new Subject { Name = "Английский", MaxPerDay = 2 };
        var item = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = s.Id, TeacherId = tA.Id,
            HoursPerWeek = 1, SplitSubgroups = true
        };
        var gA = new StudentGroup { ClassId = cls.Id, Name = "A" };
        var gB = new StudentGroup { ClassId = cls.Id, Name = "B" };
        var input = new ProblemInput([cls], [tA, tB], [s], [item], [gA, gB], [], [], 2, 4,
            new Dictionary<Guid, (Guid, Guid)> { [item.Id] = (tA.Id, tB.Id) });
        var (problem, errors) = ProblemBuilder.Build(input,
            new SolverOptions(MaxTimeSeconds: 20, NumSearchWorkers: 1, RandomSeed: 3));
        Assert.Empty(errors);
        var p = problem!;
        var result = await new OrToolsSolver().SolveAsync(p);
        Assert.Equal(SolverStatus.Feasible, result.Status);
        Assert.Equal(0, result.HardViolations);
        var times = result.Placements.Select(x => (x.DayIndex, x.SlotIndex)).Distinct().ToList();
        Assert.Single(times); // shared start
    }
}
