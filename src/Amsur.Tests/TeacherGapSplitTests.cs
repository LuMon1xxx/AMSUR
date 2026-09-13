using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// S5 P1: split teacher-gap ordinary/cross-shift + настраиваемые веса (постоянные тесты).
public sealed class TeacherGapSplitTests
{
    private static readonly List<ShiftBand> TwoShifts =
        [new ShiftBand(1, 7), new ShiftBand(8, 14)];

    [Theory]
    // slots, ordinary, cross, isCross
    [InlineData(new[] { 1, 3 }, 1, 0, false)]
    [InlineData(new[] { 1, 8 }, 0, 6, true)]
    [InlineData(new[] { 2, 9 }, 0, 6, true)]
    [InlineData(new[] { 6, 9 }, 0, 2, true)]
    [InlineData(new[] { 7, 8 }, 0, 0, true)] // смежные через границу — дыры нет
    [InlineData(new[] { 1, 2, 3 }, 0, 0, false)]
    [InlineData(new[] { 1, 3, 8, 10 }, 2, 4, true)]
    [InlineData(new[] { 1, 2, 8, 9 }, 0, 5, true)]
    [InlineData(new[] { 5 }, 0, 0, false)]
    public void Split_Table(int[] slots, int ord, int cross, bool isCross)
    {
        var (o, c, x) = GapUtils.SplitTeacherDay(slots, TwoShifts);
        Assert.Equal(ord, o);
        Assert.Equal(cross, c);
        Assert.Equal(isCross, x);
    }

    [Fact]
    public void Split_SingleBand_BackwardCompatible()
    {
        // Одна полоса / null bands = старое поведение: всё ordinary.
        var (o1, c1, x1) = GapUtils.SplitTeacherDay([1, 8], [new ShiftBand(1, 14)]);
        Assert.Equal(6, o1); Assert.Equal(0, c1); Assert.False(x1);
        var (o2, c2, _) = GapUtils.SplitTeacherDay([1, 8], null);
        Assert.Equal(6, o2); Assert.Equal(0, c2);
    }

    [Fact]
    public void ShiftBands_Default_ByGrid()
    {
        Assert.Equal(2, ProblemInput.DefaultShiftBands(14).Count);
        Assert.Single(ProblemInput.DefaultShiftBands(7));
        Assert.Single(ProblemInput.DefaultShiftBands(6));
    }

    private static SchedulingProblem TwoShiftProblem()
    {
        var year = Guid.NewGuid();
        var c1 = new SchoolClass
        {
            AcademicYearId = year, Name = "1А", Grade = 1, StudentCount = 25,
            MaxLessonsPerDay = 5,
        };
        var c2 = new SchoolClass
        {
            AcademicYearId = year, Name = "5А", Grade = 5, StudentCount = 25,
            MaxLessonsPerDay = 6,
        };
        // Один учитель ведёт в обеих сменах.
        var teacher = new Teacher { Name = "Классная", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Математика", MaxPerDay = 2 };
        var rus = new Subject { Name = "Русский язык", MaxPerDay = 2 };
        var curriculum = new List<CurriculumItem>
        {
            new() { ClassId = c1.Id, SubjectId = math.Id, TeacherId = teacher.Id, HoursPerWeek = 2 },
            new() { ClassId = c2.Id, SubjectId = rus.Id, TeacherId = teacher.Id, HoursPerWeek = 2 },
        };
        var classSlots = new Dictionary<Guid, IReadOnlyList<int>>
        {
            [c1.Id] = Enumerable.Range(1, 7).ToList(),
            [c2.Id] = Enumerable.Range(8, 14).ToList(),
        };
        var input = new ProblemInput([c1, c2], [teacher], [math, rus], curriculum,
            [], [], [], DaysCount: 2, SlotsPerDay: 14, classSlots: classSlots);
        var (p, e) = ProblemBuilder.Build(input);
        Assert.Empty(e);
        Assert.Equal(2, p!.ShiftBands.Count);
        return p;
    }

    [Fact]
    public void Breakdown_SplitVisibleAndSummed()
    {
        var problem = TwoShiftProblem();
        var occ = problem.Occurrences.OrderBy(o => o.StableKey).ToList();
        // День 0: уроки учителя в 1 и 8 (cross 6) + 3 (ordinary: S1={1,3} gap 1).
        var placements = new List<PlacedLesson>
        {
            new() { OccurrenceId = occ[0].Id, DayIndex = 0, SlotIndex = 1 },
            new() { OccurrenceId = occ[1].Id, DayIndex = 0, SlotIndex = 3 },
            new() { OccurrenceId = occ[2].Id, DayIndex = 0, SlotIndex = 8 },
            new() { OccurrenceId = occ[3].Id, DayIndex = 1, SlotIndex = 8 },
        };
        var bd = SoftEvaluator.Evaluate(problem, placements);
        var comps = bd.Components.ToDictionary(c => c.Code, c => c.Value);
        // Учитель день 0: D={1,3,8}: total=(8-1+1)-3=5, S1={1,3}→1, cross=4.
        Assert.Equal(1 * RuleCatalog.TeacherGap, comps["teacher-gap"]);
        Assert.Equal(4 * RuleCatalog.TeacherCrossShiftGap, comps["teacher-cross-shift-gap"]);
        Assert.Equal(comps.Values.Sum(), bd.Total);
    }

    [Fact]
    public void Parity_IndexVsFull_UnderCustomRules()
    {
        var problem = TwoShiftProblem();
        var rulesList = new[]
        {
            EffectiveRuleSet.Default,
            RuleResolver.Resolve("TEACHER_FRIENDLY"),
            RuleResolver.Resolve("CUSTOM", new Dictionary<string, long> { ["teacher-cross-shift-gap"] = 5 }),
        };
        var rng = new Random(42);
        var occIds = problem.Occurrences.Select(o => o.Id).ToList();
        // Старт: всё в допустимых клетках (день 0/1 × слоты класса).
        var start = problem.Occurrences.Select(o =>
        {
            var slots = problem.AllowedSlots[o.Id];
            return new PlacedLesson
            {
                OccurrenceId = o.Id, DayIndex = 0, SlotIndex = slots[0],
            };
        }).ToList();
        foreach (var rules in rulesList)
        {
            var index = SearchIndex.Build(problem, start, rules);
            long full = SoftEvaluator.Evaluate(problem, start, rules).Total;
            long cur = full;
            for (int i = 0; i < 200; i++)
            {
                var occId = occIds[rng.Next(occIds.Count)];
                var slots = problem.AllowedSlots[occId];
                var days = problem.AllowedDays[occId];
                var mv = new CandidateMove(occId,
                    days[rng.Next(days.Count)], slots[rng.Next(slots.Count)], null);
                var (allowed, delta, _) = index.TryMove(mv);
                // D-28 gate: редактор строже движка (HARD-preview окон); паритет —
                // «разрешённое редактором разрешено и движком; дельты равны где обе считают».
                var inc = IncrementalEvaluator.Evaluate(problem, start, mv, rules);
                bool evAllowed = inc.Severity != EvaluationSeverity.Forbidden;
                Assert.True(!evAllowed || allowed);
                if (!evAllowed) continue;
                if (allowed)
                {
                    var hypo = start.Where(p => p.OccurrenceId != occId)
                        .Concat([new PlacedLesson
                        {
                            OccurrenceId = occId, DayIndex = mv.DayIndex,
                            SlotIndex = mv.SlotIndex, RoomId = null,
                        }]).ToList();
                    long expect = SoftEvaluator.Evaluate(problem, hypo, rules).Total - cur;
                    Assert.Equal(expect, delta);
                    var snap = index.Snapshot().ToDictionary(p => p.OccurrenceId);
                    // Коммит каждого 7-го для проверки накопления.
                    if (i % 7 == 0)
                    {
                        index.Commit(mv);
                        start = hypo;
                        cur += delta;
                        Assert.Equal(cur, SoftEvaluator.Evaluate(problem, start, rules).Total);
                        _ = snap;
                    }
                }
            }
        }
    }

    [Fact]
    public void Profiles_ResolveAndValidate()
    {
        var std = RuleResolver.Resolve("STANDARD");
        Assert.Equal("STANDARD", std.ProfileName);
        Assert.Equal(RuleCatalog.Version, std.CatalogVersion);
        Assert.Equal(10, std.Weight("teacher-gap"));
        Assert.Equal(2, std.Weight("teacher-cross-shift-gap"));
        var tf = RuleResolver.Resolve("TEACHER_FRIENDLY");
        Assert.Equal(20, tf.Weight("teacher-gap"));
        Assert.Equal(5, tf.Weight("teacher-cross-shift-gap"));
        // Student-HARD веса одинаковы во всех профилях (gate един).
        Assert.Equal(std.Weight("student-gap"), tf.Weight("student-gap"));
        var custom = RuleResolver.Resolve("CUSTOM",
            new Dictionary<string, long> { ["teacher-cross-shift-gap"] = 0 });
        Assert.Equal("CUSTOM", custom.ProfileName);
        Assert.Equal(0, custom.Weight("teacher-cross-shift-gap"));
        Assert.Throws<ArgumentOutOfRangeException>(() => RuleResolver.Resolve("CUSTOM",
            new Dictionary<string, long> { ["teacher-cross-shift-gap"] = 51 }));
        Assert.Throws<ArgumentException>(() => RuleResolver.Resolve("UNKNOWN_PROFILE"));
    }

    [Fact]
    public void HumanScale_Labels()
    {
        Assert.Equal("Стандарт", HumanScale.Label("teacher-gap", 10));
        Assert.Equal("Не важно", HumanScale.Label("teacher-gap", 0));
        Assert.Equal("Очень важно", HumanScale.Label("teacher-gap", 30));
        Assert.Equal("Стандарт", HumanScale.Label("teacher-cross-shift-gap", 2));
    }
}
