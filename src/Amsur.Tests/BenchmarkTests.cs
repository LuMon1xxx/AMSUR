using Amsur.Domain;
using Amsur.Scheduling.Core;
using Amsur.Scheduling.OrTools;

namespace Amsur.Tests;

// P0 Small benchmark: 5 классов × 6 предметов × 3ч = 90 occ, 5 дней × 6 слотов.
// Фиксирует time-to-first-feasible (D4 baseline). workers=1+seed (D-09).
public sealed class BenchmarkTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task SmallSchool90_Feasible_RecordsTimings()
    {
        var rng = new Random(1234);
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
        var input = new ProblemInput(classes, teachers, subjects, curriculum,
            [], [], [], DaysCount: 5, SlotsPerDay: 6);
        var (problem, errors) = ProblemBuilder.Build(input,
            new SolverOptions(MaxTimeSeconds: 60, NumSearchWorkers: 1, RandomSeed: 11));
        Assert.Empty(errors);
        Assert.Equal(90, problem!.Occurrences.Count);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await new OrToolsSolver().SolveAsync(problem);
        sw.Stop();

        Assert.Equal(SolverStatus.Feasible, result.Status);
        Assert.Equal(0, result.HardViolations);
        Assert.True(PlacementValidator.Validate(problem, result.Placements).IsValid);
        // MEASURED baseline для BENCHMARKS.md (не gate на скорость — только фиксация):
        output.WriteLine(
            $"Small90: elapsed={result.ElapsedMs}ms firstFeasible={result.FirstFeasibleMs}ms " +
            $"objective={result.ObjectiveValue} solutions={result.SolutionsFound} wall={sw.ElapsedMilliseconds}ms");
    }
}
