using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// D-50 teacher-active-day: цена занятого учителе-дня (bin-packing-давление).
// Дефолт 0 = поведение не меняется; паритет SearchIndex vs полный пересчёт
// при ненулевом весе — гейт (как D-08).
public sealed class ActiveDayTests
{
    private static SchedulingProblem Tiny()
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5, StudentCount = 25 };
        var teacher = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Мат", MaxPerDay = 2 };
        var item = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = math.Id, TeacherId = teacher.Id, HoursPerWeek = 3
        };
        var input = new ProblemInput([cls], [teacher], [math], [item],
            [], [], [], DaysCount: 2, SlotsPerDay: 3);
        var (p, e) = ProblemBuilder.Build(input);
        Assert.Empty(e);
        return p!;
    }

    private static List<PlacedLesson> Start(SchedulingProblem p)
    {
        var occ = p.Occurrences;
        return
        [
            new() { OccurrenceId = occ[0].Id, DayIndex = 0, SlotIndex = 1 },
            new() { OccurrenceId = occ[1].Id, DayIndex = 0, SlotIndex = 2 },
            new() { OccurrenceId = occ[2].Id, DayIndex = 1, SlotIndex = 1 },
        ];
    }

    [Fact]
    public void ActiveDay_Counted_WhenWeightNonzero()
    {
        var p = Tiny();
        var placements = Start(p); // учитель занят в 2 днях
        var rules = RuleResolver.Resolve("CUSTOM",
            new Dictionary<string, long> { ["teacher-active-day"] = 5 });
        var bd = SoftEvaluator.Evaluate(p, placements, rules);
        Assert.Equal(10, bd.Components.First(c => c.Code == "teacher-active-day").Value);
    }

    [Fact]
    public void ActiveDay_ZeroByDefault_NoBehaviorChange()
    {
        var p = Tiny();
        var placements = Start(p);
        var bd = SoftEvaluator.Evaluate(p, placements);
        Assert.Equal(0, bd.Components.First(c => c.Code == "teacher-active-day").Value);
        Assert.Equal(EffectiveRuleSet.Default.Weight("teacher-active-day"), 0);
    }

    [Fact]
    public void ActiveDay_Resolver_Range()
    {
        var ok = RuleResolver.Resolve("CUSTOM",
            new Dictionary<string, long> { ["teacher-active-day"] = 7 });
        Assert.Equal(7, ok.Weight("teacher-active-day"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RuleResolver.Resolve("CUSTOM",
                new Dictionary<string, long> { ["teacher-active-day"] = 101 }));
    }

    [Fact]
    public void ActiveDay_Explainer_HumanName() =>
        Assert.Equal("Занятые дни учителей", QualityExplainer.HumanName("teacher-active-day"));

    // Паритет при ненулевом весе: индекс vs IncrementalEvaluator (оба с теми же rules).
    [Fact]
    public void ActiveDay_Parity_IndexVsIncremental_200Moves()
    {
        var p = Tiny();
        var rules = RuleResolver.Resolve("CUSTOM",
            new Dictionary<string, long> { ["teacher-active-day"] = 7 });
        var current = Start(p);
        var index = SearchIndex.Build(p, current, rules);
        var rng = new Random(42);
        var occIds = p.Occurrences.Select(o => o.Id).ToList();
        for (int i = 0; i < 200; i++)
        {
            var occId = occIds[rng.Next(occIds.Count)];
            var pos = current.First(x => x.OccurrenceId == occId);
            var move = new CandidateMove(occId, rng.Next(2), rng.Next(1, 4), pos.RoomId);
            var ev = IncrementalEvaluator.Evaluate(p, current, move, rules);
            var (allowed, delta, _) = index.TryMove(move);
            bool evAllowed = ev.Severity != EvaluationSeverity.Forbidden;
            Assert.True(!evAllowed || allowed);
            if (evAllowed)
                Assert.Equal(ev.DeltaTotal, delta);
            if (i % 5 == 0 && allowed)
            {
                index.Commit(move);
                current = current.Where(x => x.OccurrenceId != occId)
                    .Concat([new PlacedLesson
                    {
                        OccurrenceId = occId, DayIndex = move.DayIndex,
                        SlotIndex = move.SlotIndex, RoomId = pos.RoomId
                    }]).ToList();
            }
        }
        // Итог сошёлся: индексный снимок == полный пересчёт с теми же rules.
        Assert.Equal(
            SoftEvaluator.Evaluate(p, current, rules).Total,
            SoftEvaluator.Evaluate(p, index.Snapshot(), rules).Total);
    }
}
