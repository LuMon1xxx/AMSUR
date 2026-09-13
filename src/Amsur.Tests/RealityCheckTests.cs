using Amsur.Domain;
using Amsur.Scheduling.Core;
using Amsur.Scheduling.OrTools;

namespace Amsur.Tests;

// P0.5 Reality Check §1: откуда берутся ~46 секунд Small90.
public sealed class RealityCheckTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task Small90_Breakdown()
    {
        var classes = Enumerable.Range(0, 5).Select(i => new SchoolClass
        {
            AcademicYearId = Guid.NewGuid(), Name = $"5{(char)('А' + i)}", Grade = 5
        }).ToList();
        var teachers = Enumerable.Range(0, 6).Select(i => new Teacher
        {
            Name = $"Учитель{i}", MaxLessonsPerDay = 6
        }).ToList();
        var subjects = Enumerable.Range(0, 6).Select(i => new Subject
        {
            Name = $"Предмет{i}", MaxPerDay = 2
        }).ToList();
        var curriculum = new List<CurriculumItem>();
        foreach (var c in classes)
            for (int s = 0; s < 6; s++)
                curriculum.Add(new CurriculumItem
                {
                    ClassId = c.Id, SubjectId = subjects[s].Id,
                    TeacherId = teachers[s].Id, HoursPerWeek = 3
                });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var input = new ProblemInput(classes, teachers, subjects, curriculum,
            [], [], [], DaysCount: 5, SlotsPerDay: 6);
        var (problem, errors) = ProblemBuilder.Build(input,
            new SolverOptions(MaxTimeSeconds: 60, NumSearchWorkers: 1, RandomSeed: 11));
        sw.Stop();
        long builderMs = sw.ElapsedMilliseconds;
        Assert.Empty(errors);

        var result = await new OrToolsSolver().SolveAsync(problem!);

        sw.Restart();
        var validation = PlacementValidator.Validate(problem!, result.Placements);
        sw.Stop();
        long validatorMs = sw.ElapsedMilliseconds;
        sw.Restart();
        var breakdown = SoftEvaluator.Evaluate(problem!, result.Placements);
        sw.Stop();
        long softMs = sw.ElapsedMilliseconds;

        output.WriteLine(
            $"BREAKDOWN Small90: builder={builderMs}ms " +
            $"modelBuildA={result.PhaseMs.GetValueOrDefault("modelBuildA")}ms " +
            $"phaseA={result.PhaseMs.GetValueOrDefault("phaseA")}ms " +
            $"modelBuildB={result.PhaseMs.GetValueOrDefault("modelBuildB")}ms " +
            $"phaseB={result.PhaseMs.GetValueOrDefault("phaseB")}ms " +
            $"validator={validatorMs}ms soft={softMs}ms " +
            $"TTFF={result.FirstFeasibleMs}ms total={result.ElapsedMs}ms " +
            $"incumbents={result.SolutionsFound} objective={result.ObjectiveValue} " +
            $"peakMb={(result.ModelStats is not null ? result.ModelStats.GetValueOrDefault("peakMemoryMb") : -1)} " +
            $"vars={(result.ModelStats is not null ? $"{result.ModelStats.GetValueOrDefault("intVars")}/{result.ModelStats.GetValueOrDefault("boolVars")}/{result.ModelStats.GetValueOrDefault("constraints")}" : "-")} " +
            $"diags=[{string.Join(" | ", result.Diagnostics)}]");

        Assert.Equal(SolverStatus.Feasible, result.Status);
        Assert.True(validation.IsValid);
    }

    // P0.5 §3 → E12: БЫЛО измерено, что solver выбирал proxy-оптимум A=(1,3) с soft=25,
    // хотя существует валидное B=(2,3) с soft=0 (D-15 KEEP_PROXY).
    // СТАЛО (E12, D-24): LocalSearch оптимизирует настоящий soft → solver обязан
    // достичь оптимума B. Старое утверждение инвертировано осознанно (§13).
    [Fact]
    public async Task ProxyPrefersA_SoftPrefersB()
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5 };
        var tA = new Teacher { Name = "A", MaxLessonsPerDay = 6 };
        var tB = new Teacher { Name = "B", MaxLessonsPerDay = 6 };
        var s1 = new Subject { Name = "Мат", MaxPerDay = 2 };
        var s2 = new Subject { Name = "Рус", MaxPerDay = 2 };
        var i1 = new CurriculumItem { ClassId = cls.Id, SubjectId = s1.Id, TeacherId = tA.Id, HoursPerWeek = 1 };
        var i2 = new CurriculumItem { ClassId = cls.Id, SubjectId = s2.Id, TeacherId = tB.Id, HoursPerWeek = 1 };
        var unav = new List<TeacherUnavailability>
        {
            new() { TeacherId = tA.Id, DayIndex = 0, SlotIndex = 3, Kind = AvailabilityKind.Forbidden },
            new() { TeacherId = tB.Id, DayIndex = 0, SlotIndex = 1, Kind = AvailabilityKind.Forbidden },
            new() { TeacherId = tB.Id, DayIndex = 0, SlotIndex = 2, Kind = AvailabilityKind.Forbidden },
        };
        var input = new ProblemInput([cls], [tA, tB], [s1, s2], [i1, i2],
            [], [], unav, DaysCount: 1, SlotsPerDay: 3);
        var (problem, errors) = ProblemBuilder.Build(input,
            new SolverOptions(MaxTimeSeconds: 20, NumSearchWorkers: 1, RandomSeed: 2));
        Assert.Empty(errors);

        var occ1 = problem!.Occurrences.First(o => o.TeacherId == tA.Id);
        var occ2 = problem.Occurrences.First(o => o.TeacherId == tB.Id);
        var altB = new List<PlacedLesson>
        {
            new() { OccurrenceId = occ1.Id, DayIndex = 0, SlotIndex = 2 },
            new() { OccurrenceId = occ2.Id, DayIndex = 0, SlotIndex = 3 },
        };
        Assert.True(PlacementValidator.Validate(problem, altB).IsValid);
        long softB = SoftEvaluator.Evaluate(problem, altB).Total;
        Assert.Equal(0, softB);

        var result = await new OrToolsSolver().SolveAsync(problem);
        Assert.Equal(SolverStatus.Feasible, result.Status);
        Assert.True(PlacementValidator.Validate(problem, result.Placements).IsValid);
        long softResult = SoftEvaluator.Evaluate(problem, result.Placements).Total;
        var slots = result.Placements.OrderBy(p => p.SlotIndex).Select(p => p.SlotIndex).ToList();
        output.WriteLine($"PROXY-AUDIT: solver slots=[{string.Join(",", slots)}] soft={softResult} (alt B soft=0)");
        // E12: solver доводит до оптимума настоящим soft (LS), а не proxy.
        Assert.Equal([2, 3], slots);
        Assert.Equal(0, softResult);
    }
}
