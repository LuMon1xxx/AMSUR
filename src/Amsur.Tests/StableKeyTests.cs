using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// E3.1 TDD RED: стабильный кросс-запусковый identity пока отсутствует.
public sealed class StableKeyTests
{
    private static ProblemInput Input()
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5 };
        var tA = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var tB = new Teacher { Name = "Петрова", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Математика", MaxPerDay = 2 };
        var eng = new Subject { Name = "Английский", MaxPerDay = 2 };
        var i1 = new CurriculumItem { ClassId = cls.Id, SubjectId = math.Id, TeacherId = tA.Id, HoursPerWeek = 2 };
        var i2 = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = eng.Id, TeacherId = tA.Id,
            HoursPerWeek = 1, SplitSubgroups = true
        };
        var groups = new[]
        {
            new StudentGroup { ClassId = cls.Id, Name = "A" },
            new StudentGroup { ClassId = cls.Id, Name = "B" },
        };
        return new ProblemInput([cls], [tA, tB], [math, eng], [i1, i2],
            groups, [], [], DaysCount: 2, SlotsPerDay: 3,
            SplitTeachers: new Dictionary<Guid, (Guid, Guid)> { [i2.Id] = (tA.Id, tB.Id) });
    }

    [Fact]
    public void SameInput_TwoBuilds_SameOccurrenceIds()
    {
        var (p1, e1) = ProblemBuilder.Build(Input());
        var (p2, e2) = ProblemBuilder.Build(Input());
        Assert.Empty(e1);
        Assert.Empty(e2);
        // E11: Id детерминированы StableKey → персист переживает пересборку.
        var ids1 = p1!.Occurrences.OrderBy(o => o.StableKey).Select(o => o.Id).ToList();
        var ids2 = p2!.Occurrences.OrderBy(o => o.StableKey).Select(o => o.Id).ToList();
        Assert.Equal(ids1, ids2);
    }

    [Fact]
    public void DuplicateClassNames_FailLoudNotCrash()
    {
        var cls1 = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5 };
        var cls2 = new SchoolClass { AcademicYearId = cls1.AcademicYearId, Name = "5А", Grade = 5 };
        var teacher = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Мат", MaxPerDay = 2 };
        var input = new ProblemInput([cls1, cls2], [teacher], [math],
            [new CurriculumItem { ClassId = cls1.Id, SubjectId = math.Id, TeacherId = teacher.Id, HoursPerWeek = 1 },
             new CurriculumItem { ClassId = cls2.Id, SubjectId = math.Id, TeacherId = teacher.Id, HoursPerWeek = 1 }],
            [], [], [], DaysCount: 2, SlotsPerDay: 3);
        var (p, errors) = ProblemBuilder.Build(input);
        Assert.Null(p);
        Assert.Contains(errors, e => e.Contains("Duplicate StableKey"));
    }

    [Fact]
    public void SameInput_TwoBuilds_SameStableKeys()
    {
        var (p1, e1) = ProblemBuilder.Build(Input());
        var (p2, e2) = ProblemBuilder.Build(Input());
        Assert.Empty(e1);
        Assert.Empty(e2);
        var k1 = p1!.Occurrences.Select(o => o.StableKey).Order().ToList();
        var k2 = p2!.Occurrences.Select(o => o.StableKey).Order().ToList();
        Assert.NotEmpty(k1);
        Assert.All(k1, k => Assert.False(string.IsNullOrEmpty(k)));
        Assert.Equal(k1, k2); // детерминированность между сборками
        Assert.Equal(k1.Count, k1.Distinct().Count()); // уникальность внутри задачи
    }

    [Fact]
    public void Fingerprint_EqualAcrossRuns()
    {
        var (p1, _) = ProblemBuilder.Build(Input());
        var (p2, _) = ProblemBuilder.Build(Input());
        // Одинаковые размещения разных сборок → одинаковый fingerprint.
        // Слоты разведены (math#0→1, math#1→2, split-пары→3), иначе коллизии.
        var keys1 = p1!.Occurrences.ToDictionary(o => o.Id, o => o.StableKey);
        var keys2 = p2!.Occurrences.ToDictionary(o => o.Id, o => o.StableKey);
        List<PlacedLesson> Place(SchedulingProblem problem) =>
            problem.Occurrences.Select(o => new PlacedLesson
            {
                OccurrenceId = o.Id,
                DayIndex = 0,
                SlotIndex = o.StableKey.Contains("Английский") ? 3
                    : o.StableKey.EndsWith("#0") ? 1 : 2,
            }).ToList();
        var pl1 = Place(p1);
        var pl2 = Place(p2);
        Assert.True(PlacementValidator.Validate(p1, pl1).IsValid);
        Assert.Equal(
            ScheduleCandidate.BuildFingerprint(keys1, pl1),
            ScheduleCandidate.BuildFingerprint(keys2, pl2));
    }

    [Fact]
    public void CrossRun_DistanceZero_ForIdenticalPlacements()
    {
        var (p1, _) = ProblemBuilder.Build(Input());
        var (p2, _) = ProblemBuilder.Build(Input());
        List<PlacedLesson> Place(SchedulingProblem problem) =>
            problem.Occurrences.Select(o => new PlacedLesson
            {
                OccurrenceId = o.Id,
                DayIndex = 0,
                SlotIndex = o.StableKey.Contains("Английский") ? 3
                    : o.StableKey.EndsWith("#0") ? 1 : 2,
            }).ToList();
        // Кандидаты из РАЗНЫХ сборок, размещения совпадают по stable-ключам.
        var c1 = ScheduleCandidate.Create(p1!, Place(p1!), seed: 11, proxy: 0, phase: "t");
        var c2 = ScheduleCandidate.Create(p2!, Place(p2!), seed: 22, proxy: 0, phase: "t");
        Assert.NotNull(c1);
        Assert.NotNull(c2);
        Assert.Equal(c1!.Fingerprint, c2!.Fingerprint);
        Assert.Equal(0, ScheduleCandidateArchive.Distance(c1, c2));
    }
}
