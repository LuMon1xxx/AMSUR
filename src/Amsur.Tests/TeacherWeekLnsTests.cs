using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// OWN SYNTHESIS (TeacherWeekLns): never-worsens, детерминизм, week-close, ordering.
// Только unit-уровень на синтетике, быстрые. В конвейер не вшит.
public sealed class TeacherWeekLnsTests
{
    private static SchedulingProblem Tiny(out List<PlacedLesson> placements)
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5, StudentCount = 25 };
        var teacher = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Мат", MaxPerDay = 5 };
        var item = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = math.Id, TeacherId = teacher.Id, HoursPerWeek = 5
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
            new() { OccurrenceId = occ[4].Id, DayIndex = 1, SlotIndex = 3 },
        ];
        return p;
    }

    private static SchedulingProblem TwoTeachers(out List<PlacedLesson> placements,
        out Guid ta, out Guid tb)
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5, StudentCount = 25 };
        var tea = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var teb = new Teacher { Name = "Петров", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Мат", MaxPerDay = 5 };
        var rus = new Subject { Name = "Рус", MaxPerDay = 5 };
        var items = new List<CurriculumItem>
        {
            new() { ClassId = cls.Id, SubjectId = math.Id, TeacherId = tea.Id, HoursPerWeek = 3 },
            new() { ClassId = cls.Id, SubjectId = rus.Id, TeacherId = teb.Id, HoursPerWeek = 3 },
        };
        var input = new ProblemInput([cls], [tea, teb], [math, rus], items,
            [], [], [], DaysCount: 2, SlotsPerDay: 4);
        var (p, e) = ProblemBuilder.Build(input);
        Assert.Empty(e);
        var occ = p!.Occurrences.OrderBy(o => o.StableKey, StringComparer.Ordinal).ToList();
        var mathOcc = occ.Where(o => o.SubjectId == math.Id).ToList();
        var rusOcc = occ.Where(o => o.SubjectId == rus.Id).ToList();
        placements =
        [
            // TA: день 0 {1,4} → 2 дыры; TB: день 1 {2,4} → 1 дыра.
            new() { OccurrenceId = mathOcc[0].Id, DayIndex = 0, SlotIndex = 1 },
            new() { OccurrenceId = mathOcc[1].Id, DayIndex = 0, SlotIndex = 4 },
            new() { OccurrenceId = mathOcc[2].Id, DayIndex = 1, SlotIndex = 1 },
            new() { OccurrenceId = rusOcc[0].Id, DayIndex = 0, SlotIndex = 2 },
            new() { OccurrenceId = rusOcc[1].Id, DayIndex = 1, SlotIndex = 2 },
            new() { OccurrenceId = rusOcc[2].Id, DayIndex = 1, SlotIndex = 4 },
        ];
        ta = tea.Id; tb = teb.Id;
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
        var res = TeacherWeekLns.Improve(p, start, TimeSpan.FromSeconds(5), seed: 11);
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
        var a = TeacherWeekLns.Improve(p, start, TimeSpan.FromSeconds(3), seed: 7);
        var b = TeacherWeekLns.Improve(p, start, TimeSpan.FromSeconds(3), seed: 7);
        Assert.Equal(a.TeacherGaps, b.TeacherGaps);
        Assert.Equal(a.SoftTotal, b.SoftTotal);
        Assert.Equal(a.Accepted, b.Accepted);
    }

    [Fact]
    public void WeekClose_RepairsGappyWeek()
    {
        var p = Tiny(out var placements);
        var occById = p.Occurrences.ToDictionary(o => o.Id);
        Assert.Equal(2, TeacherDayLns.TeacherGridGaps(occById, placements));
        var res = TeacherWeekLns.Improve(p, placements, TimeSpan.FromSeconds(5), seed: 11);
        Assert.Equal(0, res.TeacherGaps);
        Assert.True(res.Accepted > 0);
    }

    [Fact]
    public void Ordering_WorstTeacherFirst()
    {
        var p = TwoTeachers(out var placements, out var ta, out var tb);
        var occById = p.Occurrences.ToDictionary(o => o.Id);
        var worst = TeacherWeekLns.WorstTeachers(occById, placements, topK: 8);
        Assert.Equal(2, worst.Count);
        Assert.Equal(ta, worst[0]);
        Assert.Equal(tb, worst[1]);
    }
}
