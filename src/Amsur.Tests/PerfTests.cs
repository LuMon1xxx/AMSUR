using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// EPIC-D D4: замеры validator/evaluator без solver (быстрые, детерминированные).
public sealed class PerfTests
{
    private static SchedulingProblem Problem300(out List<PlacedLesson> placements)
    {
        var classes = Enumerable.Range(0, 10).Select(i => new SchoolClass
        {
            AcademicYearId = Guid.NewGuid(), Name = $"К{i}", Grade = 5
        }).ToList();
        var teachers = Enumerable.Range(0, 10).Select(i => new Teacher
        {
            Name = $"У{i}", MaxLessonsPerDay = 7
        }).ToList();
        var subjects = Enumerable.Range(0, 10).Select(i => new Subject
        {
            Name = $"П{i}", MaxPerDay = 2
        }).ToList();
        var curriculum = new List<CurriculumItem>();
        foreach (var c in classes)
            for (int s = 0; s < 10; s++)
                curriculum.Add(new CurriculumItem
                {
                    ClassId = c.Id, SubjectId = subjects[s].Id,
                    TeacherId = teachers[s].Id, HoursPerWeek = 3
                });
        var (problem, errors) = ProblemBuilder.Build(
            new ProblemInput(classes, teachers, subjects, curriculum, [], [], [], 5, 7));
        Assert.Empty(errors);
        Assert.Equal(300, problem!.Occurrences.Count);
        // Размещение round-robin без коллизий: учитель s ведёт только свой предмет,
        // классы разные — конфликты только по учителю при совпадении времени.
        // Для замера валидности не требуется: меряем скорость, не чистоту.
        var rng = new Random(9);
        placements = problem.Occurrences.Select(o => new PlacedLesson
        {
            OccurrenceId = o.Id, DayIndex = rng.Next(5), SlotIndex = rng.Next(1, 8)
        }).ToList();
        return problem;
    }

    [Fact]
    public void Measure_Validator_SoftEvaluator_Incremental_300occ()
    {
        var problem = Problem300(out var placements);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var validation = PlacementValidator.Validate(problem, placements);
        sw.Stop();
        long validateMs = sw.ElapsedMilliseconds;

        sw.Restart();
        var breakdown = SoftEvaluator.Evaluate(problem, placements);
        sw.Stop();
        long softMs = sw.ElapsedMilliseconds;

        var move = new CandidateMove(problem.Occurrences[0].Id, 0, 1, null);
        sw.Restart();
        const int iters = 100;
        for (int i = 0; i < iters; i++)
            IncrementalEvaluator.Evaluate(problem, placements, move);
        sw.Stop();
        double evalPerMs = sw.Elapsed.TotalMilliseconds / iters;

        // MEASURED baseline (пороговые gate с запасом против регрессий):
        Assert.True(validateMs < 5000, $"validator {validateMs}ms");
        Assert.True(softMs < 5000, $"soft {softMs}ms");
        Assert.True(evalPerMs < 50, $"incremental {evalPerMs:F2}ms/move");
        Assert.True(breakdown.Total == breakdown.Components.Sum(c => c.Value));
    }
}
