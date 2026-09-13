using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// E3.2–E3.3 unit: merger dedup + PoolDiagnostics + perturbation bans (без solver).
public sealed class PoolExpansionUnitTests
{
    private static SchedulingProblem Problem()
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5 };
        var t = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var s = new Subject { Name = "Математика", MaxPerDay = 2 };
        var item = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = s.Id, TeacherId = t.Id, HoursPerWeek = 2
        };
        var (problem, errors) = ProblemBuilder.Build(
            new ProblemInput([cls], [t], [s], [item], [], [], [], 2, 4));
        Assert.Empty(errors);
        return problem!;
    }

    private static ScheduleCandidate Cand(SchedulingProblem problem, int d0, int s0, int d1, int s1)
    {
        var occ = problem.Occurrences;
        return ScheduleCandidate.Create(problem,
        [
            new() { OccurrenceId = occ[0].Id, DayIndex = d0, SlotIndex = s0 },
            new() { OccurrenceId = occ[1].Id, DayIndex = d1, SlotIndex = s1 },
        ], seed: 1, proxy: 0, phase: "t")!;
    }

    [Fact]
    public void Merge_Dedupes_And_PreservesBest()
    {
        var problem = Problem();
        var a = Cand(problem, 0, 1, 0, 2); // soft 0
        var b = Cand(problem, 0, 1, 0, 2); // exact dup
        var c = Cand(problem, 0, 2, 0, 3); // другой слот, чисто (старт со 2-го)
        var merged = CandidatePoolMerger.Merge([a, b, c]);
        Assert.Equal(2, CandidatePoolMerger.UniqueFingerprints([a, b, c]));
        Assert.True(merged.Members.Count <= 3);
        Assert.Contains(merged.Members, m => m.SoftTotal == 0); // best сохранён
    }

    [Fact]
    public void PoolDiagnostics_Distinguishes_Small_vs_Limited()
    {
        Assert.Equal("POOL_SUFFICIENT",
            PoolDiagnostics.Summarize(SolverStatus.Feasible, false, 5, 5));
        Assert.Equal("GENUINELY_SMALL",
            PoolDiagnostics.Summarize(SolverStatus.Feasible, true, 1, 5));
        Assert.Equal("SEARCH_LIMITED",
            PoolDiagnostics.Summarize(SolverStatus.Feasible, false, 2, 5));
        Assert.Equal("SEARCH_LIMITED",
            PoolDiagnostics.Summarize(SolverStatus.Unknown, false, 0, 5));
    }

    [Fact]
    public void Perturbation_Bans_ExactPair()
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5 };
        var t = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var s = new Subject { Name = "Математика", MaxPerDay = 2 };
        var item = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = s.Id, TeacherId = t.Id, HoursPerWeek = 1
        };
        var input = new ProblemInput([cls], [t], [s], [item], [], [], [], 1, 3);
        var (clean, e0) = ProblemBuilder.Build(input);
        Assert.Empty(e0);
        string key = clean!.Occurrences[0].StableKey;
        // Баним 2 из 3 пар — остаток ровно 1 пара.
        var banned = new Dictionary<string, IReadOnlySet<(int Day, int Slot)>>
        {
            [key] = new HashSet<(int Day, int Slot)> { (0, 1), (0, 2) }
        };
        var input2 = new ProblemInput([cls], [t], [s], [item], [], [], [], 1, 3,
            excludedPairs: banned);
        var (problem, errors) = ProblemBuilder.Build(input2);
        Assert.Empty(errors);
        Assert.True(problem!.BannedTimes.ContainsKey(problem.Occurrences[0].Id));
        // Бан всех пар — громкая ошибка, а не пустой домен молча.
        var bannedAll = new Dictionary<string, IReadOnlySet<(int Day, int Slot)>>
        {
            [key] = new HashSet<(int Day, int Slot)> { (0, 1), (0, 2), (0, 3) }
        };
        var input3 = new ProblemInput([cls], [t], [s], [item], [], [], [], 1, 3,
            excludedPairs: bannedAll);
        var (p3, e3) = ProblemBuilder.Build(input3);
        Assert.Null(p3);
        Assert.NotEmpty(e3);
    }
}
