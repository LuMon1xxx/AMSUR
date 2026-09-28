using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// Фаза 2 (SyncLns): never-worsens + детерминизм (+ нацеливание на sync-группы
// внутри never-worsens). Только unit-уровень на синтетике, быстрые.
public sealed class SyncLnsTests
{
    private static SchedulingProblem TinySync()
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5, StudentCount = 25 };
        var ta = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var tb = new Teacher { Name = "Петров", MaxLessonsPerDay = 6 };
        var tc = new Teacher { Name = "Сидоров", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Мат", MaxPerDay = 5 };
        var eng = new Subject { Name = "Англ", MaxPerDay = 5 };
        var gA = new StudentGroup { ClassId = cls.Id, Name = "5А-A" };
        var gB = new StudentGroup { ClassId = cls.Id, Name = "5А-B" };
        var sync = Guid.NewGuid();
        var items = new List<CurriculumItem>
        {
            new() { ClassId = cls.Id, SubjectId = math.Id, TeacherId = ta.Id, HoursPerWeek = 4 },
            new() { ClassId = cls.Id, SubjectId = eng.Id, TeacherId = tb.Id, HoursPerWeek = 1, GroupId = gA.Id, SyncGroupId = sync },
            new() { ClassId = cls.Id, SubjectId = eng.Id, TeacherId = tc.Id, HoursPerWeek = 1, GroupId = gB.Id, SyncGroupId = sync },
        };
        var input = new ProblemInput([cls], [ta, tb, tc], [math, eng], items,
            [gA, gB], [], [], DaysCount: 2, SlotsPerDay: 4);
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
        var p = TinySync();
        var start = GreedyStart(p);
        var occById = p.Occurrences.ToDictionary(o => o.Id);
        int g0 = TeacherDayLns.TeacherGridGaps(occById, start);
        int f0 = RuinRecreate.FailedDays(p, occById, start);
        long s0 = SoftEvaluator.Evaluate(p, start).Total;
        var res = SyncLns.Improve(p, start, TimeSpan.FromSeconds(5), seed: 11);
        Assert.Equal(start.Count, res.Placements.Count);
        Assert.True(res.TeacherGaps <= g0);
        Assert.True(res.FailedDays <= f0);
        Assert.True(res.SoftTotal <= s0);
        // Sync-целостность: пары не рвутся.
        var vr = PlacementValidator.Validate(p, res.Placements);
        Assert.DoesNotContain(vr.HardViolations, v => v.Code == "subgroup-sync");
    }

    [Fact]
    public void Deterministic_SameSeed()
    {
        var p = TinySync();
        var start = GreedyStart(p);
        var a = SyncLns.Improve(p, start, TimeSpan.FromSeconds(3), seed: 7);
        var b = SyncLns.Improve(p, start, TimeSpan.FromSeconds(3), seed: 7);
        Assert.Equal(a.TeacherGaps, b.TeacherGaps);
        Assert.Equal(a.FailedDays, b.FailedDays);
        Assert.Equal(a.SoftTotal, b.SoftTotal);
        Assert.Equal(a.Accepted, b.Accepted);
    }
}
