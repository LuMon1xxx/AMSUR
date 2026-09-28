using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// Арсенал S2 (PairWeekLns): never-worsens, детерминизм, нацеливание на пару,
// пустой случай без sync. Только unit-уровень на синтетике, быстрые.
// В конвейер не вшит.
public sealed class PairWeekLnsTests
{
    private static SchedulingProblem TinySync(out List<PlacedLesson> placements,
        out Guid ta, out Guid tb)
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5, StudentCount = 25 };
        var tea = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var teb = new Teacher { Name = "Петров", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Мат", MaxPerDay = 5 };
        var eng = new Subject { Name = "Англ", MaxPerDay = 5 };
        var gA = new StudentGroup { ClassId = cls.Id, Name = "5А-A" };
        var gB = new StudentGroup { ClassId = cls.Id, Name = "5А-B" };
        var sync = Guid.NewGuid();
        var items = new List<CurriculumItem>
        {
            new() { ClassId = cls.Id, SubjectId = math.Id, TeacherId = tea.Id, HoursPerWeek = 2 },
            new() { ClassId = cls.Id, SubjectId = eng.Id, TeacherId = tea.Id, HoursPerWeek = 1, GroupId = gA.Id, SyncGroupId = sync },
            new() { ClassId = cls.Id, SubjectId = eng.Id, TeacherId = teb.Id, HoursPerWeek = 1, GroupId = gB.Id, SyncGroupId = sync },
        };
        var input = new ProblemInput([cls], [tea, teb], [math, eng], items,
            [gA, gB], [], [], DaysCount: 2, SlotsPerDay: 4);
        var (p, e) = ProblemBuilder.Build(input);
        Assert.Empty(e);
        var occ = p!.Occurrences.OrderBy(o => o.StableKey, StringComparer.Ordinal).ToList();
        var mathOcc = occ.Where(o => o.SubjectId == math.Id).ToList();
        var engA = occ.First(o => o.TeacherId == tea.Id && o.GroupId == gA.Id);
        var engB = occ.First(o => o.TeacherId == teb.Id && o.GroupId == gB.Id);
        placements =
        [
            // TA день 0 {1,4} → 2 дыры (+sync-половина в слоте 2); TB день 0 {2} → 0.
            new() { OccurrenceId = mathOcc[0].Id, DayIndex = 0, SlotIndex = 1 },
            new() { OccurrenceId = mathOcc[1].Id, DayIndex = 0, SlotIndex = 4 },
            new() { OccurrenceId = engA.Id, DayIndex = 0, SlotIndex = 2 },
            new() { OccurrenceId = engB.Id, DayIndex = 0, SlotIndex = 2 },
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
        var p = TinySync(out _, out _, out _);
        var start = GreedyStart(p);
        var occById = p.Occurrences.ToDictionary(o => o.Id);
        int g0 = TeacherDayLns.TeacherGridGaps(occById, start);
        int f0 = RuinRecreate.FailedDays(p, occById, start);
        long s0 = SoftEvaluator.Evaluate(p, start).Total;
        var res = PairWeekLns.Improve(p, start, TimeSpan.FromSeconds(5), seed: 11);
        Assert.Equal(start.Count, res.Placements.Count);
        Assert.True(res.TeacherGaps <= g0);
        Assert.True(res.FailedDays <= f0);
        Assert.True(res.SoftTotal <= s0);
        var vr = PlacementValidator.Validate(p, res.Placements);
        Assert.DoesNotContain(vr.HardViolations, v => v.Code == "subgroup-sync");
    }

    [Fact]
    public void Deterministic_SameSeed()
    {
        var p = TinySync(out _, out _, out _);
        var start = GreedyStart(p);
        var a = PairWeekLns.Improve(p, start, TimeSpan.FromSeconds(3), seed: 7);
        var b = PairWeekLns.Improve(p, start, TimeSpan.FromSeconds(3), seed: 7);
        Assert.Equal(a.TeacherGaps, b.TeacherGaps);
        Assert.Equal(a.SoftTotal, b.SoftTotal);
        Assert.Equal(a.Accepted, b.Accepted);
    }

    [Fact]
    public void PairTargeting_LinkedGappyPairFirst()
    {
        var p = TinySync(out var placements, out var ta, out var tb);
        var occById = p.Occurrences.ToDictionary(o => o.Id);
        var pairs = PairWeekLns.WorstLinkedPairs(p, occById, placements, topK: 8);
        Assert.Single(pairs);
        // TA дырявее (2 vs 0) — ведущий.
        Assert.Equal(ta, pairs[0].First);
        Assert.Equal(tb, pairs[0].Second);
    }

    [Fact]
    public void NoSync_NoPairsNoChanges()
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5, StudentCount = 25 };
        var teacher = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Мат", MaxPerDay = 5 };
        var item = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = math.Id, TeacherId = teacher.Id, HoursPerWeek = 2
        };
        var input = new ProblemInput([cls], [teacher], [math], [item],
            [], [], [], DaysCount: 2, SlotsPerDay: 4);
        var (p, e) = ProblemBuilder.Build(input);
        Assert.Empty(e);
        var start = GreedyStart(p!);
        var occById = p!.Occurrences.ToDictionary(o => o.Id);
        Assert.Empty(PairWeekLns.WorstLinkedPairs(p, occById, start, topK: 8));
        var res = PairWeekLns.Improve(p, start, TimeSpan.FromSeconds(3), seed: 11);
        Assert.Equal(0, res.Accepted);
        Assert.Equal(
            TeacherDayLns.TeacherGridGaps(occById, start), res.TeacherGaps);
    }
}
