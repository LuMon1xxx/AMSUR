using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// D-50 шаг ②: цепной LNS по рваным учителе-дням. Гарантии по построению:
// не ухудшает (gaps, failedDays, soft) лексикографически; детерминирован.
public sealed class TeacherDayLnsTests
{
    private static SchedulingProblem Tiny(out List<PlacedLesson> placements)
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5, StudentCount = 25 };
        var teacher = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Мат", MaxPerDay = 2 };
        var item = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = math.Id, TeacherId = teacher.Id, HoursPerWeek = 4
        };
        var input = new ProblemInput([cls], [teacher], [math], [item],
            [], [], [], DaysCount: 2, SlotsPerDay: 4);
        var (p, e) = ProblemBuilder.Build(input);
        Assert.Empty(e);
        var occ = p!.Occurrences;
        placements =
        [
            new() { OccurrenceId = occ[0].Id, DayIndex = 0, SlotIndex = 1 },
            new() { OccurrenceId = occ[1].Id, DayIndex = 0, SlotIndex = 3 },
            new() { OccurrenceId = occ[2].Id, DayIndex = 1, SlotIndex = 1 },
            new() { OccurrenceId = occ[3].Id, DayIndex = 1, SlotIndex = 2 },
        ];
        return p;
    }

    [Fact]
    public void GridGaps_HandCounted()
    {
        var p = Tiny(out var placements);
        var occById = p.Occurrences.ToDictionary(o => o.Id);
        // День 0: {1,3} → 1 дыра; день 1: {1,2} → 0.
        Assert.Equal(1, TeacherDayLns.TeacherGridGaps(occById, placements));
    }

    [Fact]
    public void WorstTeacherDays_Order()
    {
        var p = Tiny(out var placements);
        var occById = p.Occurrences.ToDictionary(o => o.Id);
        var worst = TeacherDayLns.WorstTeacherDays(occById, placements, topK: 16);
        Assert.Single(worst);
        Assert.Equal(0, worst[0].Day);
    }

    [Fact]
    public void NeverWorsens_TinyGreedy()
    {
        var p = Tiny(out _);
        var g = GreedyPlacer.Place(p, 3);
        Assert.Empty(g.Unplaced);
        var start = g.Placed.Select(kv => new PlacedLesson
        {
            OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
            SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
        }).ToList();
        var occById = p.Occurrences.ToDictionary(o => o.Id);
        int g0 = TeacherDayLns.TeacherGridGaps(occById, start);
        int f0 = RuinRecreate.FailedDays(p, occById, start);
        long s0 = SoftEvaluator.Evaluate(p, start).Total;
        var res = TeacherDayLns.Improve(p, start, TimeSpan.FromSeconds(5), seed: 11);
        Assert.Equal(start.Count, res.Placements.Count);
        Assert.True(res.TeacherGaps <= g0);
        Assert.True(res.FailedDays <= f0);
        Assert.True(res.SoftTotal <= s0);
    }

    [Fact]
    public void Deterministic_SameSeed()
    {
        var p = Tiny(out _);
        var g = GreedyPlacer.Place(p, 3);
        var start = g.Placed.Select(kv => new PlacedLesson
        {
            OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
            SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
        }).ToList();
        var a = TeacherDayLns.Improve(p, start, TimeSpan.FromSeconds(3), seed: 7);
        var b = TeacherDayLns.Improve(p, start, TimeSpan.FromSeconds(3), seed: 7);
        Assert.Equal(a.TeacherGaps, b.TeacherGaps);
        Assert.Equal(a.SoftTotal, b.SoftTotal);
        Assert.Equal(a.Accepted, b.Accepted);
    }

    // D-50 шаг ③: compact-старт размещает всё то же (покрытие — приоритет D-28c)
    // и детерминирован.
    [Fact]
    public void CompactGreedy_CoveragePreserved_Tiny()
    {
        var p = Tiny(out _);
        var plain = GreedyPlacer.Place(p, 3);
        var compact = GreedyPlacer.Place(p, 3, compact: true);
        Assert.Empty(plain.Unplaced);
        Assert.Empty(compact.Unplaced);
        Assert.Equal(plain.Placed.Count, compact.Placed.Count);
        var again = GreedyPlacer.Place(p, 3, compact: true);
        Assert.Equal(
            compact.Placed.OrderBy(kv => kv.Key).Select(kv => (kv.Value.Day, kv.Value.Slot)).ToList(),
            again.Placed.OrderBy(kv => kv.Key).Select(kv => (kv.Value.Day, kv.Value.Slot)).ToList());
    }
}
