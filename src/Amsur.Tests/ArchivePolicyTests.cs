using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// E1.1 TDD RED: архива пока нет — тесты фиксируют ТРЕБУЕМУЮ политику приёмки.
public sealed class ArchivePolicyTests
{
    private static SchedulingProblem Problem()
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5 };
        var t = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        // MaxPerDay=1: повторы за день дают clean-ненулевой soft (нужен политике; D-28).
        var s = new Subject { Name = "Математика", MaxPerDay = 1 };
        var item = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = s.Id, TeacherId = t.Id, HoursPerWeek = 2
        };
        var (problem, errors) = ProblemBuilder.Build(
            new ProblemInput([cls], [t], [s], [item], [], [], [], 2, 4));
        Assert.Empty(errors);
        return problem!;
    }

    private static ScheduleCandidate Cand(
        SchedulingProblem problem, (int d, int s) p0, (int d, int s) p1, int seed = 1)
    {
        var occ = problem.Occurrences;
        var placements = new List<PlacedLesson>
        {
            new() { OccurrenceId = occ[0].Id, DayIndex = p0.d, SlotIndex = p0.s },
            new() { OccurrenceId = occ[1].Id, DayIndex = p1.d, SlotIndex = p1.s },
        };
        return ScheduleCandidate.Create(problem, placements, seed, proxy: 0, phase: "test");
    }

    [Fact]
    public void EmptyArchive_AcceptsFirstValid()
    {
        var problem = Problem();
        var archive = new ScheduleCandidateArchive(maxSize: 5);
        Assert.True(archive.TryAdd(Cand(problem, (0, 1), (0, 2))));
        Assert.Single(archive.Members);
    }

    [Fact]
    public void InvalidCandidate_Rejected()
    {
        var problem = Problem();
        var archive = new ScheduleCandidateArchive(maxSize: 5);
        // Оба в одном слоте — teacher+group collision.
        Assert.False(archive.TryAdd(Cand(problem, (0, 1), (0, 1))));
        Assert.Empty(archive.Members);
    }

    [Fact]
    public void WorseAndSimilar_RejectedWhenFull()
    {
        var problem = Problem();
        var archive = new ScheduleCandidateArchive(maxSize: 2, diversityThreshold: 100);
        Assert.True(archive.TryAdd(Cand(problem, (0, 1), (0, 2)))); // повтор: soft 15
        Assert.True(archive.TryAdd(Cand(problem, (0, 1), (1, 1)))); // soft 0, отличается днём
        // Хуже худшего и почти дубликат первого — отклонить.
        Assert.False(archive.TryAdd(Cand(problem, (0, 2), (0, 1), seed: 9)));
        Assert.Equal(2, archive.Members.Count);
    }

    [Fact]
    public void BetterThanWorst_ReplacesWorst()
    {
        var problem = Problem();
        var archive = new ScheduleCandidateArchive(maxSize: 2);
        Assert.True(archive.TryAdd(Cand(problem, (0, 1), (0, 2)))); // повтор: soft 15
        Assert.True(archive.TryAdd(Cand(problem, (0, 1), (1, 1)))); // soft 0
        Assert.True(archive.TryAdd(Cand(problem, (0, 2), (1, 2)))); // soft 0 — лучше худшего
        Assert.Equal(2, archive.Members.Count);
        Assert.DoesNotContain(archive.Members, m => m.SoftTotal == 15);
    }

    [Fact]
    public void DiversityCandidate_KeptDespiteWorseQuality()
    {
        var problem = Problem();
        var archive = new ScheduleCandidateArchive(maxSize: 2, diversityThreshold: 100);
        Assert.True(archive.TryAdd(Cand(problem, (0, 1), (0, 2)))); // день 0, повтор
        Assert.True(archive.TryAdd(Cand(problem, (0, 1), (1, 1)))); // soft 0
        // Не лучше худшего по soft, но далеко от обоих (dist>=100) — diversity.
        Assert.True(archive.TryAdd(Cand(problem, (1, 2), (1, 3), seed: 7)));
        Assert.Equal(2, archive.Members.Count);
    }

    [Fact]
    public void Fingerprint_Distance_Weights()
    {
        var problem = Problem();
        var a = Cand(problem, (0, 1), (0, 2));
        var b = Cand(problem, (0, 1), (0, 2));
        Assert.Equal(0, ScheduleCandidateArchive.Distance(a, b));
        var c = Cand(problem, (1, 1), (0, 2)); // один день отличается
        Assert.True(ScheduleCandidateArchive.Distance(a, c) >= 100);
        var d = Cand(problem, (0, 2), (0, 3)); // один слот отличается (старт со 2-го — чисто)
        long dd = ScheduleCandidateArchive.Distance(a, d);
        Assert.True(dd >= 30 && dd < 100);
    }

    [Fact]
    public void K5_CapRespected()
    {
        var problem = Problem();
        var archive = new ScheduleCandidateArchive(maxSize: 5, diversityThreshold: 0);
        var spots = new (int d, int s)[] { (0, 1), (0, 2), (0, 3), (0, 4), (1, 1), (1, 2), (1, 3) };
        int accepted = 0;
        for (int i = 0; i < spots.Length; i++)
            if (archive.TryAdd(Cand(problem, spots[i], spots[(i + 3) % spots.Length], seed: i)))
                accepted++;
        Assert.True(archive.Members.Count <= 5);
    }
}
