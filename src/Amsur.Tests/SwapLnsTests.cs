using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// Арсенал S1 (SwapLns): never-worsens, детерминизм, обмен с улучшением.
// Только unit-уровень на синтетике, быстрые.
public sealed class SwapLnsTests
{
    // TA день 0 {1,3} → 1 дыра; классы pupil-чистые (обмен сохраняет
    // мультимножество клеток — старт обязан быть чистым, иначе всё заблокировано).
    // Обмен TA@(5Б,0,3) ↔ TB@(5А,1,2): оба дня компактится → 0 дыр.
    private static SchedulingProblem Tiny(out List<PlacedLesson> placements)
    {
        var cA = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5, StudentCount = 25 };
        var cB = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5Б", Grade = 5, StudentCount = 25 };
        var ta = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var tb = new Teacher { Name = "Петров", MaxLessonsPerDay = 6 };
        var tc = new Teacher { Name = "Сидоров", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Мат", MaxPerDay = 2 };
        var rus = new Subject { Name = "Рус", MaxPerDay = 2 };
        var bio = new Subject { Name = "Био", MaxPerDay = 2 };
        var items = new List<CurriculumItem>
        {
            new() { ClassId = cA.Id, SubjectId = math.Id, TeacherId = ta.Id, HoursPerWeek = 1 },
            new() { ClassId = cB.Id, SubjectId = math.Id, TeacherId = ta.Id, HoursPerWeek = 2 },
            new() { ClassId = cB.Id, SubjectId = rus.Id, TeacherId = tb.Id, HoursPerWeek = 2 },
            new() { ClassId = cA.Id, SubjectId = rus.Id, TeacherId = tb.Id, HoursPerWeek = 2 },
            new() { ClassId = cA.Id, SubjectId = bio.Id, TeacherId = tc.Id, HoursPerWeek = 1 },
        };
        var input = new ProblemInput([cA, cB], [ta, tb, tc], [math, rus, bio], items,
            [], [], [], DaysCount: 2, SlotsPerDay: 4);
        var (p, e) = ProblemBuilder.Build(input);
        Assert.Empty(e);
        var occ = p!.Occurrences.OrderBy(o => o.StableKey, StringComparer.Ordinal).ToList();
        PlacedLesson At(Guid classId, Guid subjectId, Guid teacherId, int n, int day, int slot)
        {
            var o = occ.Where(x => x.ClassId == classId && x.SubjectId == subjectId &&
                    x.TeacherId == teacherId).OrderBy(x => x.StableKey, StringComparer.Ordinal).ToList()[n];
            return new PlacedLesson { OccurrenceId = o.Id, DayIndex = day, SlotIndex = slot };
        }
        placements =
        [
            At(cA.Id, math.Id, ta.Id, 0, 0, 1),
            At(cB.Id, math.Id, ta.Id, 0, 0, 3),
            At(cB.Id, math.Id, ta.Id, 1, 1, 1),
            At(cB.Id, rus.Id, tb.Id, 0, 0, 1),
            At(cB.Id, rus.Id, tb.Id, 1, 0, 2),
            At(cA.Id, rus.Id, tb.Id, 0, 1, 1),
            At(cA.Id, rus.Id, tb.Id, 1, 1, 2),
            At(cA.Id, bio.Id, tc.Id, 0, 0, 2),
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
        long s0 = SoftEvaluator.Evaluate(p, start).Total;
        var res = SwapLns.Improve(p, start, TimeSpan.FromSeconds(5), seed: 11);
        Assert.Equal(start.Count, res.Placements.Count);
        Assert.True(res.TeacherGaps <= g0);
        Assert.True(res.SoftTotal <= s0);
        // Жадный старт не обязан быть pupil-чистым — только не хуже старта.
        Assert.True(OverloadLns.BlockingHard(p, res.Placements) <=
            OverloadLns.BlockingHard(p, start));
        Assert.True(OverloadLns.PupilHard(p, res.Placements) <=
            OverloadLns.PupilHard(p, start));
    }

    [Fact]
    public void Deterministic_SameSeed()
    {
        var p = Tiny(out _);
        var start = GreedyStart(p);
        var a = SwapLns.Improve(p, start, TimeSpan.FromSeconds(3), seed: 7);
        var b = SwapLns.Improve(p, start, TimeSpan.FromSeconds(3), seed: 7);
        Assert.Equal(a.TeacherGaps, b.TeacherGaps);
        Assert.Equal(a.SoftTotal, b.SoftTotal);
        Assert.Equal(a.Accepted, b.Accepted);
    }

    [Fact]
    public void SwapImproves_GappyPair()
    {
        var p = Tiny(out var placements);
        var occById = p.Occurrences.ToDictionary(o => o.Id);
        Assert.Equal(1, TeacherDayLns.TeacherGridGaps(occById, placements));
        Assert.Equal(0, OverloadLns.PupilHard(p, placements));
        var res = SwapLns.Improve(p, placements, TimeSpan.FromSeconds(5), seed: 11);
        // Любой принятый обмен строго снижает дыры (1→0); дальше улучшать нечего.
        Assert.Equal(0, res.TeacherGaps);
        Assert.True(res.Accepted > 0);
    }
}
