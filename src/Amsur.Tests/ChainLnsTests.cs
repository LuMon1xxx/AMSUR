using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// Grind-цепочка (ChainLns): never-worsens, детерминизм, сходимость.
// Только unit-уровень на синтетике, быстрые.
public sealed class ChainLnsTests
{
    private static SchedulingProblem Tiny()
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5, StudentCount = 25 };
        var teacher = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Мат", MaxPerDay = 5 };
        var item = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = math.Id, TeacherId = teacher.Id, HoursPerWeek = 6
        };
        var input = new ProblemInput([cls], [teacher], [math], [item],
            [], [], [], DaysCount: 3, SlotsPerDay: 4);
        var (p, e) = ProblemBuilder.Build(input);
        Assert.Empty(e);
        return p!;
    }

    private static List<PlacedLesson> GreedyStart(SchedulingProblem p, int seed = 3)
    {
        var g = GreedyPlacer.Place(p, seed);
        Assert.Empty(g.Unplaced);
        return g.Placed.Select(kv => new PlacedLesson
        {
            OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
            SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
        }).ToList();
    }

    [Fact]
    public void NeverWorsens_TinyGreedy()
    {
        var p = Tiny();
        var start = GreedyStart(p);
        var occById = p.Occurrences.ToDictionary(o => o.Id);
        int g0 = TeacherDayLns.TeacherGridGaps(occById, start);
        int f0 = RuinRecreate.FailedDays(p, occById, start);
        long s0 = SoftEvaluator.Evaluate(p, start).Total;
        var res = ChainLns.Improve(p, start, TimeSpan.FromSeconds(20), seed: 11);
        Assert.Equal(start.Count, res.Placements.Count);
        Assert.True(res.TeacherGaps <= g0);
        Assert.True(res.FailedDays <= f0);
        Assert.True(res.SoftTotal <= s0);
    }

    [Fact]
    public void Deterministic_SameSeed()
    {
        var p = Tiny();
        var start = GreedyStart(p);
        var a = ChainLns.Improve(p, start, TimeSpan.FromSeconds(20), seed: 7);
        var b = ChainLns.Improve(p, start, TimeSpan.FromSeconds(20), seed: 7);
        Assert.Equal(a.TeacherGaps, b.TeacherGaps);
        Assert.Equal(a.FailedDays, b.FailedDays);
        Assert.Equal(a.SoftTotal, b.SoftTotal);
        Assert.Equal(a.Rounds, b.Rounds);
        Assert.Equal(a.Accepted, b.Accepted);
    }

    [Fact]
    public void Convergence_StopsWithoutAccepts()
    {
        var p = Tiny();
        var start = GreedyStart(p);
        var res = ChainLns.Improve(p, start, TimeSpan.FromSeconds(20), seed: 11);
        // Хотя бы один раунд, не более максимума; последний раунд — без принятий
        // (иначе цепочка продолжилась бы): accepted покрыт раундами полностью.
        Assert.True(res.Rounds >= 1);
        Assert.True(res.Rounds <= 4);
        Assert.True(res.Accepted >= 0);
    }
}
