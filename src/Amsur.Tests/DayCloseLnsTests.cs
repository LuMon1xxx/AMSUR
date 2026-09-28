using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// Фаза 1 (DayCloseLns): never-worsens, детерминизм, day-close кейс.
// Только unit-уровень на синтетике, быстрые.
public sealed class DayCloseLnsTests
{
    private static SchedulingProblem Tiny(out List<PlacedLesson> placements)
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
        var occ = p!.Occurrences;
        placements =
        [
            new() { OccurrenceId = occ[0].Id, DayIndex = 0, SlotIndex = 1 },
            new() { OccurrenceId = occ[1].Id, DayIndex = 0, SlotIndex = 4 },
            new() { OccurrenceId = occ[2].Id, DayIndex = 1, SlotIndex = 1 },
            new() { OccurrenceId = occ[3].Id, DayIndex = 1, SlotIndex = 2 },
            new() { OccurrenceId = occ[4].Id, DayIndex = 2, SlotIndex = 1 },
            new() { OccurrenceId = occ[5].Id, DayIndex = 2, SlotIndex = 2 },
        ];
        return p;
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
        var p = Tiny(out _);
        var start = GreedyStart(p);
        var occById = p.Occurrences.ToDictionary(o => o.Id);
        int g0 = TeacherDayLns.TeacherGridGaps(occById, start);
        int f0 = RuinRecreate.FailedDays(p, occById, start);
        long s0 = SoftEvaluator.Evaluate(p, start).Total;
        var res = DayCloseLns.Improve(p, start, TimeSpan.FromSeconds(5), seed: 11);
        Assert.Equal(start.Count, res.Placements.Count);
        Assert.True(res.TeacherGaps <= g0);
        Assert.True(res.FailedDays <= f0);
        Assert.True(res.SoftTotal <= s0);
    }

    [Fact]
    public void Deterministic_SameSeed()
    {
        var p = Tiny(out _);
        var start = GreedyStart(p);
        var a = DayCloseLns.Improve(p, start, TimeSpan.FromSeconds(3), seed: 7);
        var b = DayCloseLns.Improve(p, start, TimeSpan.FromSeconds(3), seed: 7);
        Assert.Equal(a.TeacherGaps, b.TeacherGaps);
        Assert.Equal(a.FailedDays, b.FailedDays);
        Assert.Equal(a.SoftTotal, b.SoftTotal);
        Assert.Equal(a.Accepted, b.Accepted);
    }

    [Fact]
    public void DayClose_ClosesGappyDay()
    {
        var p = Tiny(out var placements);
        var occById = p.Occurrences.ToDictionary(o => o.Id);
        // День 0: {1,4} → 2 дыры; дни 1–2 плотные. Reuse должен выселить день 0.
        Assert.Equal(2, TeacherDayLns.TeacherGridGaps(occById, placements));
        var res = DayCloseLns.Improve(p, placements, TimeSpan.FromSeconds(5), seed: 11);
        Assert.Equal(0, res.TeacherGaps);
        Assert.True(res.Accepted > 0);
    }
}
