using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// GrindLite: доводка не ломает валидность, дни уплотняет, детерминирована.
public sealed class GrindLiteTests
{
    private static SchedulingProblem ThinProblem()
    {
        var year = Guid.NewGuid();
        var c = new SchoolClass { AcademicYearId = year, Name = "5А", Grade = 5, MaxLessonsPerDay = 7 };
        var t = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Математика", MaxPerDay = 2, Difficulty = 8 };
        var rus = new Subject { Name = "Русский язык", MaxPerDay = 2, Difficulty = 5 };
        var cur = new List<CurriculumItem>
        {
            new() { ClassId = c.Id, SubjectId = math.Id, TeacherId = t.Id, HoursPerWeek = 3 },
            new() { ClassId = c.Id, SubjectId = rus.Id, TeacherId = t.Id, HoursPerWeek = 2 },
        };
        var (p, errors) = ProblemBuilder.Build(new ProblemInput(
            [c], [t], [math, rus], cur, [], [], [],
            DaysCount: 5, SlotsPerDay: 5, flex: FlexSettings.Neutral));
        Assert.NotNull(p);
        Assert.Empty(errors);
        return p!;
    }

    private static int TeacherDays(SchedulingProblem p, List<PlacedLesson> pl)
    {
        var occById = p.Occurrences.ToDictionary(o => o.Id);
        return pl.GroupBy(x => (occById[x.OccurrenceId].TeacherId, x.DayIndex)).Count();
    }

    [Fact]
    public void Polish_PreservesValidityAndPacksDays()
    {
        var p = ThinProblem();
        // Тощие дни: по уроку в день пять дней подряд.
        var start = p.Occurrences.OrderBy(o => o.StableKey).Select((o, i) => new PlacedLesson
        {
            OccurrenceId = o.Id, DayIndex = i, SlotIndex = 1
        }).ToList();
        Assert.True(PlacementValidator.Validate(p, start).IsValid);
        int daysBefore = TeacherDays(p, start);
        Assert.Equal(5, daysBefore);
        var res = GrindLite.Polish(p, start, EffectiveRuleSet.Default,
            TimeSpan.FromSeconds(8), seed: 11);
        var vr = PlacementValidator.Validate(p, res.Placements);
        Assert.True(vr.IsValid);
        Assert.True(TeacherDays(p, res.Placements) <= daysBefore);
        long before = SoftEvaluator.Evaluate(p, start, EffectiveRuleSet.Default).Total;
        Assert.True(res.SoftTotal <= before);
        Assert.NotEmpty(res.Log);
    }

    [Fact]
    public void Polish_Deterministic()
    {
        var p = ThinProblem();
        var start = p.Occurrences.OrderBy(o => o.StableKey).Select((o, i) => new PlacedLesson
        {
            OccurrenceId = o.Id, DayIndex = i, SlotIndex = 1
        }).ToList();
        var a = GrindLite.Polish(p, start, EffectiveRuleSet.Default,
            TimeSpan.FromSeconds(8), seed: 7);
        var b = GrindLite.Polish(p, start, EffectiveRuleSet.Default,
            TimeSpan.FromSeconds(8), seed: 7);
        Assert.Equal(a.SoftTotal, b.SoftTotal);
        Assert.Equal(a.TeacherWindows, b.TeacherWindows);
    }
}
