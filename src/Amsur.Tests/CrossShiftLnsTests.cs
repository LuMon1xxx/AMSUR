using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// Фаза E (CrossShiftLns): never-worsens, детерминизм, cross-close, детект.
// Только unit-уровень на синтетике, быстрые. В конвейер не вшит.
public sealed class CrossShiftLnsTests
{
    // SlotsPerDay=14 → две смены [(1,7),(8,14)] из ProblemBuilder.
    private static SchedulingProblem Tiny(out List<PlacedLesson> placements)
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5, StudentCount = 25 };
        var teacher = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Мат", MaxPerDay = 5 };
        var item = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = math.Id, TeacherId = teacher.Id, HoursPerWeek = 4
        };
        var input = new ProblemInput([cls], [teacher], [math], [item],
            [], [], [], DaysCount: 2, SlotsPerDay: 14);
        var (p, e) = ProblemBuilder.Build(input);
        Assert.Empty(e);
        var occ = p!.Occurrences;
        placements =
        [
            // День 0 через смену {6,8}: 1 дыра; день 1 плотный {1,2}.
            new() { OccurrenceId = occ[0].Id, DayIndex = 0, SlotIndex = 6 },
            new() { OccurrenceId = occ[1].Id, DayIndex = 0, SlotIndex = 8 },
            new() { OccurrenceId = occ[2].Id, DayIndex = 1, SlotIndex = 1 },
            new() { OccurrenceId = occ[3].Id, DayIndex = 1, SlotIndex = 2 },
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
        var res = CrossShiftLns.Improve(p, start, TimeSpan.FromSeconds(5), seed: 11);
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
        var a = CrossShiftLns.Improve(p, start, TimeSpan.FromSeconds(3), seed: 7);
        var b = CrossShiftLns.Improve(p, start, TimeSpan.FromSeconds(3), seed: 7);
        Assert.Equal(a.TeacherGaps, b.TeacherGaps);
        Assert.Equal(a.SoftTotal, b.SoftTotal);
        Assert.Equal(a.Accepted, b.Accepted);
    }

    [Fact]
    public void CrossClose_PacksIntoOneShift()
    {
        var p = Tiny(out var placements);
        var occById = p.Occurrences.ToDictionary(o => o.Id);
        Assert.Equal(1, TeacherDayLns.TeacherGridGaps(occById, placements));
        var cross = CrossShiftLns.WorstCrossDays(occById, placements, p.ShiftBands, topK: 16);
        Assert.Single(cross);
        var res = CrossShiftLns.Improve(p, placements, TimeSpan.FromSeconds(5), seed: 11);
        Assert.Equal(0, res.TeacherGaps);
        Assert.True(res.Accepted > 0);
    }

    [Fact]
    public void Detection_OnlyGappyCrossDays()
    {
        var p = Tiny(out var placements);
        var occById = p.Occurrences.ToDictionary(o => o.Id);
        // День 1 плотный {1,2} — не кросс; день 0 {6,8} — кросс с дырой.
        var cross = CrossShiftLns.WorstCrossDays(occById, placements, p.ShiftBands, topK: 16);
        Assert.Single(cross);
        Assert.Equal(0, cross[0].Day);
        // Кросс-день без дыр ({7,8}) — не цель.
        var tight = placements.Select(pl =>
            pl.DayIndex == 0 && pl.SlotIndex == 6
                ? new PlacedLesson { OccurrenceId = pl.OccurrenceId, DayIndex = 0, SlotIndex = 7, RoomId = pl.RoomId }
                : pl).ToList();
        Assert.Empty(CrossShiftLns.WorstCrossDays(occById, tight, p.ShiftBands, topK: 16));
    }
}
