using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// Фаза A (ThinDayLns): never-worsens, детерминизм, thin-close, only-thin, costcap.
// Только unit-уровень на синтетике, быстрые.
public sealed class ThinDayLnsTests
{
    private static SchedulingProblem Tiny(int subjectMaxPerDay, out List<PlacedLesson> placements)
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5, StudentCount = 25 };
        var teacher = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Мат", MaxPerDay = subjectMaxPerDay };
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
            new() { OccurrenceId = occ[0].Id, DayIndex = 0, SlotIndex = 2 },
            new() { OccurrenceId = occ[1].Id, DayIndex = 1, SlotIndex = 1 },
            new() { OccurrenceId = occ[2].Id, DayIndex = 1, SlotIndex = 2 },
            new() { OccurrenceId = occ[3].Id, DayIndex = 1, SlotIndex = 3 },
            new() { OccurrenceId = occ[4].Id, DayIndex = 2, SlotIndex = 1 },
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
        var p = Tiny(5, out _);
        var start = GreedyStart(p);
        var occById = p.Occurrences.ToDictionary(o => o.Id);
        int g0 = TeacherDayLns.TeacherGridGaps(occById, start);
        int f0 = RuinRecreate.FailedDays(p, occById, start);
        long s0 = SoftEvaluator.Evaluate(p, start).Total;
        int d0 = ThinDayLns.TeacherDayCount(occById, start);
        var res = ThinDayLns.Improve(p, start, TimeSpan.FromSeconds(5), seed: 11);
        Assert.Equal(start.Count, res.Placements.Count);
        // Дефолт (0,0): строго не хуже по (gaps, failedDays, soft) либо меньше дней
        // без роста цены.
        bool lexOk = (res.TeacherGaps < g0 && res.FailedDays <= f0) ||
            (res.TeacherGaps == g0 && (res.FailedDays < f0 ||
                (res.FailedDays == f0 && res.SoftTotal <= s0)));
        bool dayOk = res.TeacherDays < d0 && res.TeacherGaps <= g0 &&
            res.FailedDays <= f0 && res.SoftTotal <= s0;
        Assert.True(lexOk || dayOk);
    }

    [Fact]
    public void Deterministic_SameSeed()
    {
        var p = Tiny(5, out _);
        var start = GreedyStart(p);
        var a = ThinDayLns.Improve(p, start, TimeSpan.FromSeconds(3), seed: 7);
        var b = ThinDayLns.Improve(p, start, TimeSpan.FromSeconds(3), seed: 7);
        Assert.Equal(a.TeacherGaps, b.TeacherGaps);
        Assert.Equal(a.TeacherDays, b.TeacherDays);
        Assert.Equal(a.SoftTotal, b.SoftTotal);
        Assert.Equal(a.Accepted, b.Accepted);
    }

    [Fact]
    public void ThinClose_ClosesOneLessonDay()
    {
        var p = Tiny(5, out var placements);
        var occById = p.Occurrences.ToDictionary(o => o.Id);
        Assert.Equal(3, ThinDayLns.TeacherDayCount(occById, placements));
        var res = ThinDayLns.Improve(p, placements, TimeSpan.FromSeconds(5), seed: 11);
        // Тощий день 0 ({2}) вселяется в день 2 ({1}→{1,2}): 3 дня → 2.
        Assert.Equal(2, res.TeacherDays);
        Assert.Equal(0, res.TeacherGaps);
    }

    [Fact]
    public void OnlyThin_FullDaysUntouched()
    {
        var p = Tiny(5, out var placements);
        var occById = p.Occurrences.ToDictionary(o => o.Id);
        var worst = ThinDayLns.WorstThinDays(occById, placements, topK: 32);
        // День 1 (3 урока) — не тощий, в списке только дни 0 и 2.
        Assert.Equal(2, worst.Count);
        Assert.DoesNotContain(worst, x => x.Day == 1);
        Assert.Contains(worst, x => x.Day == 0);
    }

    [Fact]
    public void CostCap_GuardsDayPrice()
    {
        // MaxPerDay=1: вселение тощего дня растит soft (+15) — дефолт (0,0)
        // отклоняет, softCap=100 принимает.
        var p = Tiny(1, out var placements);
        var occById = p.Occurrences.ToDictionary(o => o.Id);
        var strict = ThinDayLns.Improve(p, placements, TimeSpan.FromSeconds(5),
            seed: 11, gapsCap: 0, softCap: 0);
        Assert.Equal(3, strict.TeacherDays);
        Assert.Equal(0, strict.Accepted);
        var loose = ThinDayLns.Improve(p, placements, TimeSpan.FromSeconds(5),
            seed: 11, gapsCap: 0, softCap: 100);
        Assert.Equal(2, loose.TeacherDays);
        Assert.True(loose.Accepted > 0);
    }
}
