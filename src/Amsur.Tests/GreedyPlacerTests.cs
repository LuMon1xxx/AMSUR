using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// Greedy-конструктив E12: детерминирован, честно частичен, validator-clean.
public sealed class GreedyPlacerTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static SchedulingProblem Tiny()
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5, StudentCount = 25 };
        var teacher = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Мат", MaxPerDay = 2 };
        var item = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = math.Id, TeacherId = teacher.Id, HoursPerWeek = 2
        };
        var input = new ProblemInput([cls], [teacher], [math], [item],
            [], [], [], DaysCount: 2, SlotsPerDay: 3);
        var (p, e) = ProblemBuilder.Build(input);
        Assert.Empty(e);
        return p!;
    }

    private static List<PlacedLesson> ToPlacements(GreedyPlacement g) =>
        g.Placed.Select(kv => new PlacedLesson
        {
            OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
            SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
        }).ToList();

    [Fact]
    public void Tiny_CompleteAndValidatorClean()
    {
        var p = Tiny();
        var g = GreedyPlacer.Place(p);
        Assert.Empty(g.Unplaced);
        Assert.Equal(p.Occurrences.Count, g.Placed.Count);
        Assert.True(PlacementValidator.Validate(p, ToPlacements(g)).IsValid);
    }

    [Fact]
    public void Overconstrained_PartialHonest()
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5 };
        var teacher = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Мат", MaxPerDay = 2 };
        var item = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = math.Id, TeacherId = teacher.Id, HoursPerWeek = 2
        };
        // Одна клетка на двоих: второй не влезет.
        var input = new ProblemInput([cls], [teacher], [math], [item],
            [], [new TeacherDayOff { TeacherId = teacher.Id, DayIndex = 1 }], [],
            DaysCount: 2, SlotsPerDay: 1);
        var (p, e) = ProblemBuilder.Build(input);
        Assert.Empty(e);
        var g = GreedyPlacer.Place(p!);
        Assert.Single(g.Placed);
        Assert.Single(g.Unplaced);
    }

    [Fact]
    public void Deterministic_TwoRunsEqual()
    {
        var p = Tiny();
        var a = GreedyPlacer.Place(p);
        var b = GreedyPlacer.Place(p);
        Assert.Equal(a.Placed.Count, b.Placed.Count);
        Assert.Equal(
            a.Placed.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList(),
            b.Placed.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList());
    }

    [Fact]
    public void RealSchool_Coverage()
    {
        var problem = RealSchoolStressTests.BuildRealSchool();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Как в solver: best-of-4 starts (детерминировано, те же сиды).
        var g = GreedyPlacer.Place(problem, 11);
        for (int gs = 1; gs < 4; gs++)
        {
            var alt = GreedyPlacer.Place(problem, 11 + gs * 7919);
            if (alt.Unplaced.Count < g.Unplaced.Count) g = alt;
        }
        sw.Stop();
        var placements = ToPlacements(g);
        // Пайплайн solver: greedy → repair. Чистый greedy окнами не обязан быть (D-28).
        var rep = CompactRepair.Repair(problem, placements);
        var validation = PlacementValidator.Validate(problem, rep.Placements);
        foreach (var v in validation.HardViolations.Take(10))
            output.WriteLine("GREEDY-HARD: " + v.Code + " " + v.Message);
        output.WriteLine(
            $"GREEDY real: placed={g.Placed.Count}/{problem.Occurrences.Count} " +
            $"unplaced={g.Unplaced.Count} ms={sw.ElapsedMilliseconds} " +
            $"repaired={rep.RepairedDays} failed={rep.FailedDays} " +
            $"validatorClean={validation.IsValid}");
        Assert.Empty(g.Unplaced);
        // Ремонт — зона CompactRepair/PhaseB-тестов; здесь только измеряем (без assert:
        // greedy чистит частично, доводит пайплайн).
        output.WriteLine($"GREEDY-REPAIR-INFO repaired={rep.RepairedDays} failed={rep.FailedDays}");
        // Gate: построение не роняет и покрывает всё (порог честный — 100% после best-of-4).
        Assert.True(g.Placed.Count == problem.Occurrences.Count);
        // Gate: построение не роняет и покрывает большинство (порог честный, не 100%).
        Assert.True(g.Placed.Count > problem.Occurrences.Count / 2);
    }
}
