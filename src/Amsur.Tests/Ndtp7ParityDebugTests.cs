using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// Временная диагностика паритета: по одному новому терму.
public sealed class Ndtp7ParityDebugTests
{
    private static SchedulingProblem BuildProblem()
    {
        var year = Guid.NewGuid();
        var c = new SchoolClass { AcademicYearId = year, Name = "9А", Grade = 9, MaxLessonsPerDay = 7 };
        var t1 = new Teacher { Name = "Т1", MaxLessonsPerDay = 6 };
        var t2 = new Teacher { Name = "Т2", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Математика", MaxPerDay = 5, Difficulty = 8 };
        var pe = new Subject
        {
            Name = "Физическая культура и здоровье", MaxPerDay = 2, Difficulty = 3,
            IsPhysicalEducation = true
        };
        var rus = new Subject { Name = "Русский язык", MaxPerDay = 5, Difficulty = 7 };
        var cur = new List<CurriculumItem>
        {
            new() { ClassId = c.Id, SubjectId = math.Id, TeacherId = t1.Id, HoursPerWeek = 3 },
            new() { ClassId = c.Id, SubjectId = pe.Id, TeacherId = t2.Id, HoursPerWeek = 2 },
            new() { ClassId = c.Id, SubjectId = rus.Id, TeacherId = t1.Id, HoursPerWeek = 2 },
        };
        var (p, errors) = ProblemBuilder.Build(new ProblemInput(
            [c], [t1, t2], [math, pe, rus], cur, [], [], [],
            DaysCount: 5, SlotsPerDay: 5, flex: FlexSettings.Neutral));
        Assert.NotNull(p);
        Assert.Empty(errors);
        return p!;
    }

    private static EffectiveRuleSet OnlyNew(string code, long w)
    {
        var ov = new Dictionary<string, long>
        {
            // student-late-start НЕ трогаем: StudentCompactness.LatePenalty игнорирует
            // вес из правил (старое поведение ядра) — обнуление ломает паритет само по себе.
            ["student-gap"] = 0,
            ["teacher-gap"] = 0, ["teacher-cross-shift-gap"] = 0,
            ["heavy-edge"] = 0, ["subject-maxperday"] = 0,
            ["room-preference"] = 0, ["room-crowding"] = 0,
            ["teacher-split"] = 0, ["teacher-active-day"] = 0,
            ["doubles-adjacency"] = 0,
            ["peak-days"] = 0, ["edge-once"] = 0, ["alternation"] = 0, ["pe-consecutive"] = 0,
            ["strict-student-gap"] = 50, ["strict-teacher-gap"] = 20,
        };
        ov[code] = w;
        return RuleResolver.Resolve("STANDARD", ov,
            new HashSet<string> { "student-gap" });
    }

    private static int CheckParity(string code, long w, out string first)
    {
        var p = BuildProblem();
        var rs = OnlyNew(code, w);
        var rnd = new Random(7);
        var placements = p.Occurrences
            .Select(o => new PlacedLesson { OccurrenceId = o.Id, DayIndex = rnd.Next(5), SlotIndex = rnd.Next(1, 6) })
            .ToList();
        var idx = SearchIndex.Build(p, placements, rs);
        first = "";
        int bad = 0;
        for (int i = 0; i < 60; i++)
        {
            var occ = p.Occurrences[rnd.Next(p.Occurrences.Count)];
            var mv = new CandidateMove(occ.Id, rnd.Next(5), rnd.Next(1, 6), null);
            var (allowed, delta, _) = idx.TryMove(mv);
            if (!allowed) continue;
            var before = SoftEvaluator.Evaluate(p, idx.Snapshot(), rs).Total;
            var hypo = idx.Snapshot().Select(x =>
                x.OccurrenceId == occ.Id
                    ? new PlacedLesson { OccurrenceId = x.OccurrenceId, DayIndex = mv.DayIndex, SlotIndex = mv.SlotIndex }
                    : x).ToList();
            var after = SoftEvaluator.Evaluate(p, hypo, rs).Total;
            if (after - before != delta)
            {
                bad++;
                if (bad == 1)
                    first = $"move#{i} occ={occ.StableKey} full={after - before} delta={delta}";
            }
            idx.Commit(mv);
        }
        return bad;
    }

    [Fact]
    public void Parity_OnlyPeak() { int n = CheckParity("peak-days", 5, out var f); Assert.True(n == 0, $"peak bad={n} {f}"); }

    [Fact]
    public void Parity_OnlyEdgeOnce() { int n = CheckParity("edge-once", 10, out var f); Assert.True(n == 0, $"edge bad={n} {f}"); }

    [Fact]
    public void Parity_OnlyAlternation() { int n = CheckParity("alternation", 5, out var f); Assert.True(n == 0, $"alt bad={n} {f}"); }

    [Fact]
    public void Parity_OnlyPe() { int n = CheckParity("pe-consecutive", 25, out var f); Assert.True(n == 0, $"pe bad={n} {f}"); }

    [Fact]
    public void Parity_DefaultWeights_Swaps_Breakdown()
    {
        var year = Guid.NewGuid();
        var c = new SchoolClass { AcademicYearId = year, Name = "5А", Grade = 5, MaxLessonsPerDay = 7 };
        var t1 = new Teacher { Name = "Иванов", MaxLessonsPerDay = 10 };
        var t2 = new Teacher { Name = "Петров", MaxLessonsPerDay = 10 };
        var math = new Subject { Name = "Мат", MaxPerDay = 2 };
        var rus = new Subject { Name = "Рус", MaxPerDay = 2 };
        var (p, errors) = ProblemBuilder.Build(new ProblemInput([c], [t1, t2], [math, rus],
            [
                new() { ClassId = c.Id, SubjectId = math.Id, TeacherId = t1.Id, HoursPerWeek = 4 },
                new() { ClassId = c.Id, SubjectId = rus.Id, TeacherId = t2.Id, HoursPerWeek = 2 },
            ],
            [], [], [], DaysCount: 3, SlotsPerDay: 5));
        Assert.NotNull(p);
        Assert.Empty(errors);
        var rs = EffectiveRuleSet.Default;
        var rnd = new Random(42);
        var current = p!.Occurrences.Zip([(0, 1), (0, 2), (1, 1), (1, 2), (2, 1), (2, 2)])
            .Select(x => new PlacedLesson
            {
                OccurrenceId = x.First.Id, DayIndex = x.Second.Item1, SlotIndex = x.Second.Item2,
            }).ToList();
        var idx = SearchIndex.Build(p, current, rs);
        var occIds = p.Occurrences.Select(o => o.Id).ToList();
        int bad = 0;
        string first = "";
        // Исчерпывающе: все пары на фиксированном раскладе (без run-in).
        foreach (var a in occIds)
            foreach (var b in occIds)
            {
                if (a == b) continue;
                var (allowed, delta, _) = idx.TrySwap(a, b);
                if (!allowed) continue;
                var bb = SoftEvaluator.Evaluate(p, idx.Snapshot(), rs);
                var pa = idx.Position(a);
                var pb = idx.Position(b);
                var hypo = idx.Snapshot().Select(x =>
                    x.OccurrenceId == a ? new PlacedLesson
                        { OccurrenceId = a, DayIndex = pb.Day, SlotIndex = pb.Slot }
                    : x.OccurrenceId == b ? new PlacedLesson
                        { OccurrenceId = b, DayIndex = pa.Day, SlotIndex = pa.Slot }
                    : x).ToList();
                var ba = SoftEvaluator.Evaluate(p, hypo, rs);
                if (bb.Total + delta != ba.Total)
                {
                    bad++;
                    if (bad <= 3)
                        first += $"| swap a={St(p, a)}@{pa} b={St(p, b)}@{pb} d={delta} full={ba.Total - bb.Total} [" + string.Join(",",
                            ba.Components.Join(bb.Components, x => x.Code, y => y.Code,
                                (x, y) => x.Code + ":" + y.Value + "->" + x.Value)) + "]";
                }
            }
        Assert.True(bad == 0, $"swap bad={bad} {first}");
    }

    private static string St(SchedulingProblem p, Guid id)
    {
        var o = p.Occurrences.Single(x => x.Id == id);
        return p.Subjects[o.SubjectId].Name + "#" + o.StableKey[^1];
    }
}
