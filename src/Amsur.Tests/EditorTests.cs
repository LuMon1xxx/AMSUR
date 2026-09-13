using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// EPIC-B B2 + EPIC-D D1: ManualMove + parity-gate (детерминированный, без solver).
public sealed class EditorTests
{
    private static SchedulingProblem Problem(out List<PlacedLesson> placements)
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5 };
        var t = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var s = new Subject { Name = "Математика", MaxPerDay = 2 };
        var item = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = s.Id, TeacherId = t.Id, HoursPerWeek = 3
        };
        var input = new ProblemInput([cls], [t], [s], [item], [], [], [],
            DaysCount: 2, SlotsPerDay: 4);
        var (problem, errors) = ProblemBuilder.Build(input);
        Assert.Empty(errors);
        var occ = problem!.Occurrences;
        placements =
        [
            new() { OccurrenceId = occ[0].Id, DayIndex = 0, SlotIndex = 1 },
            new() { OccurrenceId = occ[1].Id, DayIndex = 0, SlotIndex = 2 },
            new() { OccurrenceId = occ[2].Id, DayIndex = 1, SlotIndex = 1 },
        ];
        return problem;
    }

    [Fact]
    public void ManualMove_Valid_Allowed()
    {
        var problem = Problem(out var placements);
        var occ = problem.Occurrences[0];
        var eval = IncrementalEvaluator.Evaluate(problem, placements,
            new CandidateMove(occ.Id, DayIndex: 1, SlotIndex: 2, RoomId: null));
        Assert.Equal(EvaluationSeverity.Allowed, eval.Severity);
    }

    [Fact]
    public void ManualMove_Forbidden_TeacherCollision()
    {
        var problem = Problem(out var placements);
        var occ = problem.Occurrences[0];
        // Слот 2 дня 0 занят другим уроком того же учителя.
        var eval = IncrementalEvaluator.Evaluate(problem, placements,
            new CandidateMove(occ.Id, DayIndex: 0, SlotIndex: 2, RoomId: null));
        Assert.Equal(EvaluationSeverity.Forbidden, eval.Severity);
        Assert.Contains(eval.HardViolations, v =>
            v.Code == PhysicalRuleCodes.TeacherCollision || v.Code == PhysicalRuleCodes.GroupCollision);
    }

    [Fact]
    public void ManualMove_SoftPenalty_WarningWithDelta()
    {
        var problem = Problem(out var placements);
        var occ = problem.Occurrences[2];
        // Переезд (1,1) -> (0,3): день 0 станет {1,2,3} (compact), но предмет ×3 за день
        // при норме 2: delta +15 и severity Warning (без окон — D-28).
        var eval = IncrementalEvaluator.Evaluate(problem, placements,
            new CandidateMove(occ.Id, DayIndex: 0, SlotIndex: 3, RoomId: null));
        Assert.Equal(EvaluationSeverity.Warning, eval.Severity);
        Assert.Equal(15, eval.DeltaTotal);
        // Паритет: delta == full recompute.
        var hypo = placements.Where(p => p.OccurrenceId != occ.Id)
            .Concat([new PlacedLesson { OccurrenceId = occ.Id, DayIndex = 0, SlotIndex = 3 }]).ToList();
        var expected = SoftEvaluator.Delta(
            SoftEvaluator.Evaluate(problem, placements),
            SoftEvaluator.Evaluate(problem, hypo));
        Assert.Equal(expected, eval.DeltaTotal);
    }

    [Fact]
    public void ManualMove_SyncHalfAlone_Forbidden()
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
        var (problem, errors) = ProblemBuilder.Build(input);
        Assert.Empty(errors);
        var occ = problem!.Occurrences;
        var placements = new List<PlacedLesson>
        {
            new() { OccurrenceId = occ[0].Id, DayIndex = 0, SlotIndex = 1 },
            new() { OccurrenceId = occ[1].Id, DayIndex = 0, SlotIndex = 1 },
        };
        var eval = IncrementalEvaluator.Evaluate(problem, placements,
            new CandidateMove(occ[0].Id, DayIndex: 0, SlotIndex: 2, RoomId: null));
        Assert.Equal(EvaluationSeverity.Forbidden, eval.Severity);
        Assert.Contains(eval.HardViolations, v => v.Code == PhysicalRuleCodes.SubgroupSync);
    }

    // D1 parity-gate: 200 seed-moves, incremental delta == full recompute (без solver).
    [Fact]
    public void Parity_IncrementalDelta_EqualsFullRecompute_200Moves()
    {
        var problem = Problem(out var placements);
        var rng = new Random(42);
        var occIds = problem.Occurrences.Select(o => o.Id).ToList();
        for (int i = 0; i < 200; i++)
        {
            var occId = occIds[rng.Next(occIds.Count)];
            var move = new CandidateMove(occId, rng.Next(2), rng.Next(1, 5), null);
            var eval = IncrementalEvaluator.Evaluate(problem, placements, move);
            if (eval.Severity == EvaluationSeverity.Forbidden) continue; // hard-путь без дельты
            var hypo = placements.Where(p => p.OccurrenceId != occId)
                .Concat([new PlacedLesson { OccurrenceId = occId, DayIndex = move.DayIndex, SlotIndex = move.SlotIndex }]).ToList();
            var expected = SoftEvaluator.Delta(
                SoftEvaluator.Evaluate(problem, placements),
                SoftEvaluator.Evaluate(problem, hypo));
            Assert.Equal(expected, eval.DeltaTotal);
        }
    }
}
