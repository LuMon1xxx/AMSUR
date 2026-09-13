using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// LocalSearch E12: только улучшает (или держит), валидность не ломает, детерминирован.
public sealed class LocalSearchTests
{
    private static SchedulingProblem Tiny()
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5, StudentCount = 25 };
        var teacher = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Мат", MaxPerDay = 2 };
        var item = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = math.Id, TeacherId = teacher.Id, HoursPerWeek = 3
        };
        var input = new ProblemInput([cls], [teacher], [math], [item],
            [], [], [], DaysCount: 2, SlotsPerDay: 3);
        var (p, e) = ProblemBuilder.Build(input);
        Assert.Empty(e);
        return p!;
    }

    private static List<PlacedLesson> Place(SchedulingProblem p, params (int Day, int Slot)[] at) =>
        p.Occurrences.Zip(at).Select(x => new PlacedLesson
        {
            OccurrenceId = x.First.Id, DayIndex = x.Second.Day, SlotIndex = x.Second.Slot
        }).ToList();

    [Fact]
    public void NeverWorse_NeverInvalid()
    {
        var p = Tiny();
        // Разрыв {1,3} + (1,1): soft = 100+10 (+0) = 110 (веса v2, D-28).
        var start = Place(p, (0, 1), (0, 3), (1, 1));
        long before = SoftEvaluator.Evaluate(p, start).Total;
        Assert.Equal(110, before);
        var res = LocalSearch.Improve(p, start, TimeSpan.FromSeconds(5), seed: 42);
        Assert.True(res.SoftTotal <= before);
        Assert.True(PlacementValidator.Validate(p, res.Placements).IsValid);
        // Оптимум здесь 0 (все три подряд): LS обязан его найти.
        Assert.Equal(0, res.SoftTotal);
    }

    [Fact]
    public void Deterministic_SameSeedSameResult()
    {
        var p = Tiny();
        var start = Place(p, (0, 1), (0, 3), (1, 1));
        var a = LocalSearch.Improve(p, start, TimeSpan.FromSeconds(5), seed: 7);
        var b = LocalSearch.Improve(p, start, TimeSpan.FromSeconds(5), seed: 7);
        Assert.Equal(a.SoftTotal, b.SoftTotal);
        Assert.Equal(a.AcceptedMoves, b.AcceptedMoves);
    }

    [Fact]
    public void ZeroBudget_ReturnsStart()
    {
        var p = Tiny();
        var start = Place(p, (0, 1), (0, 3), (1, 1));
        var res = LocalSearch.Improve(p, start, TimeSpan.Zero, seed: 1);
        Assert.Equal(SoftEvaluator.Evaluate(p, start).Total, res.SoftTotal);
        Assert.Equal(0, res.AcceptedMoves);
    }

    // EPIC-H H10: аудит не меняет поведение, счётчики согласованы.
    [Fact]
    public void Audit_Consistent_NoBehaviorChange()
    {
        var p = Tiny();
        var start = Place(p, (0, 1), (0, 3), (1, 1));
        var plain = LocalSearch.Improve(p, start, TimeSpan.FromSeconds(5), seed: 42);
        var audit = new LocalSearch.Audit();
        var withAudit = LocalSearch.Improve(p, start, TimeSpan.FromSeconds(5), seed: 42, audit: audit);
        Assert.Equal(plain.SoftTotal, withAudit.SoftTotal);
        Assert.Equal(plain.AcceptedMoves, withAudit.AcceptedMoves);
        Assert.Equal(audit.Attempts, audit.Forbidden + audit.NonImproving + withAudit.AcceptedMoves);
        Assert.Equal(withAudit.AcceptedMoves, audit.AcceptedPerSweep.Sum());
        Assert.Equal(withAudit.SoftTotal, audit.Trajectory[^1].Soft);
        for (int i = 1; i < audit.Trajectory.Count; i++)
            Assert.True(audit.Trajectory[i].Soft <= audit.Trajectory[i - 1].Soft);
        Assert.True(audit.Sweeps >= 1);
        Assert.True(audit.EvalMs <= withAudit.ElapsedMs);
    }
}
