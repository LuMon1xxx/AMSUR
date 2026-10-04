using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// decision-teacher-gaps §1 + S9-вердикты:
// P1-поверхность: Explainer-имя, QualityRating-коды, Hint, слайдер doubles 0..100, Top-5 min-gaps.
// P3-движок (каталог v7): SoftUnits.DoublesScattered (единый источник),
// SoftEvaluator-терм (×GradeW), SearchIndex-зеркало + паритет, Explainer.CountUnits.
// Стиль — русские комментарии, TDD.
public sealed class DoublesAdjacencyTests
{
    private static PenaltyBreakdown Bd(params (string Code, long Value)[] comps) =>
        new()
        {
            Total = comps.Sum(c => c.Value),
            Components = comps.Select(c => new PenaltyComponent { Code = c.Code, Value = c.Value }).ToList(),
        };

    private static ScheduleCandidate Cand(long soft, params (string Code, long Value)[] comps) =>
        new([], soft, Bd(comps), Guid.NewGuid().ToString("N"), 11, 0, "", DateTime.UtcNow, "B",
            new Dictionary<Guid, string>());

    // --- 1. Explainer-имя (S9: «Разбросанные сдвоенные уроки») ---
    [Fact]
    public void HumanName_Doubles()
    {
        Assert.Equal("Разбросанные сдвоенные уроки",
            QualityExplainer.HumanName("doubles-adjacency"));
    }

    // --- 2. QualityRating знает doubles-adjacency (дефолт §1 = 10) ---
    [Fact]
    public void QualityRating_KnowsDoubles()
    {
        var r = QualityRating.FromBreakdown(Bd(("doubles-adjacency", 30)));
        Assert.Equal("Хорошее", r.Label); // не ученический код — не «Требует внимания»
        Assert.Contains(r.Improvements, s => s.Contains("Разбросанные сдвоенные уроки: 3"));
    }

    // --- 3. QualityRating знает teacher-active-day (сырые дни при весе 0) ---
    [Fact]
    public void QualityRating_KnowsActiveDay()
    {
        var r = QualityRating.FromBreakdown(Bd(("teacher-active-day", 5)));
        Assert.Equal("Хорошее", r.Label);
        Assert.Contains(r.Improvements, s => s.Contains("Занятые дни учителей: 5"));
    }

    // --- 4. Hint'ы обоих кодов (S9-ACCEPTED: слайдер + Hint) ---
    [Fact]
    public void Hints_HaveDoublesAndActiveDay()
    {
        var d = QualityHints.For("doubles-adjacency");
        Assert.Equal("doubles-adjacency", d.Code);
        Assert.Equal("Разбросанные сдвоенные уроки", d.Title);
        Assert.NotEqual("", d.When);
        var a = QualityHints.For("teacher-active-day");
        Assert.Equal("teacher-active-day", a.Code);
        Assert.Equal("Занятые дни учителей", a.Title);
        Assert.NotEqual("", a.When);
    }

    // --- 5. Слайдер doubles 0..100 в редакторе (21 опция суммарно: 9 строгих + 12 пожеланий) ---
    [Fact]
    public void Editor_HasDoublesSlider_0to100()
    {
        var ed = QualitySettingsEditor.FromRules(EffectiveRuleSet.Default);
        var o = ed.Options.Single(x => x.Code == "doubles-adjacency");
        Assert.Equal(0, o.Min);
        Assert.Equal(100, o.Max);
        Assert.Equal(10, o.DefaultWeight);
        Assert.Equal(2, o.Level); // дефолт = Стандарт
        // Roundtrip уровень→вес→уровень внутри 0..100.
        foreach (int lv in new[] { 0, 1, 2, 3, 4 })
            Assert.Equal(lv, QualitySettingsEditor.WeightToLevel("doubles-adjacency",
                QualitySettingsEditor.LevelToWeight("doubles-adjacency", lv)));
        ed.SetWeight("doubles-adjacency", 50);
        Assert.Equal(50, o.Weight);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ed.SetWeight("doubles-adjacency", 101));
        // P3 (каталог v7): вес слайдера применяется движком — резолвер код знает.
        var rs = RuleResolver.Resolve("CUSTOM", ed.GetOverrides());
        Assert.Equal(50, rs.Weight("doubles-adjacency"));
        Assert.Equal(9, rs.CatalogVersion);
    }

    // --- 6. Top-5: min-gaps вместо min-soft (Best-of-3, §3) ---
    [Fact]
    public void Top5_MinGaps_BeatsMinSoft()
    {        // A: мягко лучше (soft 100), но рваный (окон 50);
        // B: мягко хуже (soft 200), но плотный (окон 10) → лучший по gaps.
        var a = Cand(100, ("teacher-gap", 50));
        var b = Cand(200, ("teacher-gap", 10));
        var archive = new ScheduleCandidateArchive();
        Assert.True(archive.TryAdd(a));
        Assert.True(archive.TryAdd(b));
        var panel = Top5PanelModel.FromArchive(archive);
        Assert.Equal(2, panel.Cards.Count);
        Assert.Same(b, panel.Best!.Candidate); // min-gaps, не min-soft
        Assert.Equal("Лучший", panel.Best.Badge);
        Assert.True(panel.Best.CanAccept);
        // Cross-shift окна тоже считаются gaps, не soft-придатком.
        var c = Cand(50, ("teacher-cross-shift-gap", 4));
        var archive2 = new ScheduleCandidateArchive();
        archive2.TryAdd(a);
        archive2.TryAdd(c);
        Assert.Same(c, Top5PanelModel.FromArchive(archive2).Best!.Candidate);
    }

    // === P3-движок: 7 краевых кейсов логики (S4 §краевых cp-logic) ===

    // Ручная фикстура: полный контроль GroupId/SyncGroupId (билдер их не даёт точечно).
    // Возвращает задачу + occurrence целых; tweak правит изъяны (сплит/синхрон).
    private static (SchedulingProblem P, List<LessonOccurrence> Occ) NewProblem(
        int wholeHours, int maxPerDay = 2, int grade = 5,
        bool gradePriority = false, Action<List<LessonOccurrence>>? tweak = null)
    {
        var cls = new SchoolClass
        {
            AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = grade,
            StudentCount = 25, MaxLessonsPerDay = 7,
        };
        var teacher = new Teacher { Name = "Иванов", MaxLessonsPerDay = 10 };
        var subj = new Subject { Name = "Мат", MaxPerDay = maxPerDay };
        var item = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = subj.Id, TeacherId = teacher.Id,
            HoursPerWeek = wholeHours,
        };
        var occ = Enumerable.Range(0, wholeHours).Select(i => new LessonOccurrence
        {
            CurriculumItemId = item.Id, ClassId = cls.Id, SubjectId = subj.Id,
            TeacherId = teacher.Id, StableKey = $"5А|Мат|Иванов||#{i}",
        }).ToList();
        tweak?.Invoke(occ);
        var flex = gradePriority ? new FlexSettings() : FlexSettings.Neutral;
        var p = new SchedulingProblem
        {
            Occurrences = occ,
            Classes = new Dictionary<Guid, SchoolClass> { [cls.Id] = cls },
            Teachers = new Dictionary<Guid, Teacher> { [teacher.Id] = teacher },
            Subjects = new Dictionary<Guid, Subject> { [subj.Id] = subj },
            DaysCount = 5, SlotsPerDay = 7, Flex = flex,
        };
        return (p, occ);
    }

    private static List<PlacedLesson> PlaceAll(
        List<LessonOccurrence> occ, params (int Day, int Slot)[] at) =>
        occ.Zip(at).Select(x => new PlacedLesson
        {
            OccurrenceId = x.First.Id, DayIndex = x.Second.Day, SlotIndex = x.Second.Slot,
        }).ToList();

    private static long DoublesOf(SchedulingProblem p, List<PlacedLesson> pl) =>
        SoftEvaluator.Evaluate(p, pl).Components
            .Single(c => c.Code == "doubles-adjacency").Value;

    // Кейс 1: дубль рядом {1,2} — 0 (не разбросан).
    [Fact]
    public void AdjacentDouble_NoPenalty()
    {
        var (p, occ) = NewProblem(2);
        Assert.Equal(0, DoublesOf(p, PlaceAll(occ, (0, 1), (0, 2))));
    }

    // Кейс 2: дубль врозь {1,3} — 1 единица × 10 × gw(5-й кл = 1) = 10.
    [Fact]
    public void ScatteredDouble_Penalized10()
    {
        var (p, occ) = NewProblem(2);
        Assert.Equal(10, DoublesOf(p, PlaceAll(occ, (0, 1), (0, 3))));
    }

    // Кейс 3 (S9: сплит-кейс REJECTED — поведение верно по INV-D1/D2):
    // 2 целых врозь + сплит-пара того же предмета тем же днём —
    // сплит исключён (INV-D1), целые считаются (eligCount==2) → 1 единица = 10.
    [Fact]
    public void SplitPair_Excluded_WholesCounted()
    {
        var splitA = Guid.NewGuid();
        var (p, occ) = NewProblem(4, tweak: list =>
        {
            list[2].GroupId = splitA;
            list[3].GroupId = Guid.NewGuid();
        });
        Assert.Equal(10, DoublesOf(p, PlaceAll(occ, (0, 1), (0, 3), (0, 2), (0, 2))));
    }

    // Кейс 4: синхронная пара {1,3} (SyncGroupId) — исключена (INV-D1) → 0.
    [Fact]
    public void SyncPair_Excluded()
    {
        var sync = Guid.NewGuid();
        var (p, occ) = NewProblem(2, tweak: list =>
        {
            list[0].GroupId = Guid.NewGuid();
            list[1].GroupId = Guid.NewGuid();
            list[0].SyncGroupId = sync;
            list[1].SyncGroupId = sync;
        });
        Assert.Equal(0, DoublesOf(p, PlaceAll(occ, (0, 1), (0, 3))));
    }

    // Кейс 5: тройка целых {1,2,4} — eligCount==3 → 0 (INV-D2);
    // subject-maxperday при этом считает независимо (excess 1 → 15, INV-D4).
    [Fact]
    public void Triple_NoDoubles_SubjectMaxPerDayCounts()
    {
        var (p, occ) = NewProblem(3);
        var pl = PlaceAll(occ, (0, 1), (0, 2), (0, 4));
        var bd = SoftEvaluator.Evaluate(p, pl);
        Assert.Equal(0, bd.Components.Single(c => c.Code == "doubles-adjacency").Value);
        Assert.Equal(15, bd.Components.Single(c => c.Code == "subject-maxperday").Value);
    }

    // Кейс 6: аддитивность без guards (INV-D4) — MaxPerDay=1, дубль врозь:
    // subject-maxperday (excess 1 → 15) И doubles (1 → 10) считают одновременно.
    [Fact]
    public void Additivity_WithSubjectMaxPerDay_NoGuards()
    {
        var (p, occ) = NewProblem(2, maxPerDay: 1);
        var bd = SoftEvaluator.Evaluate(p, PlaceAll(occ, (0, 1), (0, 3)));
        Assert.Equal(15, bd.Components.Single(c => c.Code == "subject-maxperday").Value);
        Assert.Equal(10, bd.Components.Single(c => c.Code == "doubles-adjacency").Value);
    }

    // Кейс 7: только GradeW (INV-D8) — 11-й класс, приоритет вкл (gw=3):
    // дубль врозь = 1 × 10 × 3 = 30. Учителя/кабинеты не взвешиваются.
    [Fact]
    public void GradeWeight_OnlyGradeW()
    {
        var (p, occ) = NewProblem(2, grade: 11, gradePriority: true);
        Assert.Equal(30, DoublesOf(p, PlaceAll(occ, (0, 1), (0, 3))));
    }

    // Ядро единицы (INV-D2/D3): счёт, distinct, смежность.
    [Fact]
    public void DoublesUnits_Core()
    {
        Assert.Equal(1, SoftUnits.DoublesUnits([1, 3]));
        Assert.Equal(0, SoftUnits.DoublesUnits([1, 2])); // рядом
        Assert.Equal(0, SoftUnits.DoublesUnits([2, 2])); // один слот дважды
        Assert.Equal(0, SoftUnits.DoublesUnits([1])); // одиночка
        Assert.Equal(0, SoftUnits.DoublesUnits([1, 2, 4])); // тройка
        Assert.Equal(0, SoftUnits.DoublesUnits([]));
    }

    // INV-D6: room-only ход (тот же день+слот, другой кабинет) — дельта 0.
    [Fact]
    public void RoomOnlyMove_DeltaZero()
    {
        var r1 = new Room { Name = "101" };
        var r2 = new Room { Name = "102" };
        var (p, occ) = NewProblem(2);
        p.Rooms[r1.Id] = r1;
        p.Rooms[r2.Id] = r2;
        var pl = PlaceAll(occ, (0, 1), (0, 3));
        var withRooms = pl.Select((x, i) => new PlacedLesson
        {
            OccurrenceId = x.OccurrenceId, DayIndex = x.DayIndex,
            SlotIndex = x.SlotIndex, RoomId = r1.Id,
        }).ToList();
        var index = SearchIndex.Build(p, withRooms);
        var move = new CandidateMove(occ[1].Id, 0, 3, r2.Id);
        var (allowed, delta, _) = index.TryMove(move);
        Assert.True(allowed);
        Assert.Equal(0, delta);
        var hypo = withRooms.Select(x => x.OccurrenceId == occ[1].Id
            ? new PlacedLesson
            {
                OccurrenceId = x.OccurrenceId, DayIndex = 0, SlotIndex = 3, RoomId = r2.Id,
            }
            : x).ToList();
        Assert.Equal(
            SoftEvaluator.Evaluate(p, withRooms).Total,
            SoftEvaluator.Evaluate(p, hypo).Total);
    }

    // Паритет индекс/полный (S9-пакет 1: 200 ходов + 100 свопов).
    // Дельта индекса == дельте IncrementalEvaluator (полный пересчёт) где разрешено;
    // своп: before+delta == полный пересчёт после; симметрия swap (INV-D7).
    [Fact]
    public void Parity_IndexVsFull_200Moves_100Swaps()
    {
        var cls = new SchoolClass
        {
            AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5,
            StudentCount = 25, MaxLessonsPerDay = 7,
        };
        var t1 = new Teacher { Name = "Иванов", MaxLessonsPerDay = 10 };
        var t2 = new Teacher { Name = "Петров", MaxLessonsPerDay = 10 };
        var math = new Subject { Name = "Мат", MaxPerDay = 2 };
        var rus = new Subject { Name = "Рус", MaxPerDay = 2 };
        var input = new ProblemInput([cls], [t1, t2], [math, rus],
            [
                new() { ClassId = cls.Id, SubjectId = math.Id, TeacherId = t1.Id, HoursPerWeek = 4 },
                new() { ClassId = cls.Id, SubjectId = rus.Id, TeacherId = t2.Id, HoursPerWeek = 2 },
            ],
            [], [], [], DaysCount: 3, SlotsPerDay: 5);
        var (p, errors) = ProblemBuilder.Build(input);
        Assert.Empty(errors);
        var rules = RuleResolver.Resolve("STANDARD"); // doubles=10, каталог v7
        Assert.Equal(10, rules.Weight("doubles-adjacency"));
        var current = p!.Occurrences.Zip([(0, 1), (0, 2), (1, 1), (1, 2), (2, 1), (2, 2)])
            .Select(x => new PlacedLesson
            {
                OccurrenceId = x.First.Id, DayIndex = x.Second.Item1, SlotIndex = x.Second.Item2,
            }).ToList();
        var index = SearchIndex.Build(p, current, rules);
        var rng = new Random(42);
        var occIds = p.Occurrences.Select(o => o.Id).ToList();
        int compared = 0, nonzero = 0;
        for (int i = 0; i < 200; i++)
        {
            var occId = occIds[rng.Next(occIds.Count)];
            var pos = current.First(x => x.OccurrenceId == occId);
            var move = new CandidateMove(occId, rng.Next(3), rng.Next(1, 6), pos.RoomId);
            var ev = IncrementalEvaluator.Evaluate(p, current, move, rules);
            var (allowed, delta, _) = index.TryMove(move);
            bool evAllowed = ev.Severity != EvaluationSeverity.Forbidden;
            Assert.True(!evAllowed || allowed);
            if (!evAllowed) continue;
            compared++;
            Assert.Equal(ev.DeltaTotal, delta);
            if (delta != 0) nonzero++;
            if (i % 5 == 0 && allowed)
            {
                index.Commit(move);
                current = current.Where(x => x.OccurrenceId != occId)
                    .Concat([new PlacedLesson
                    {
                        OccurrenceId = occId, DayIndex = move.DayIndex,
                        SlotIndex = move.SlotIndex, RoomId = pos.RoomId,
                    }]).ToList();
            }
        }
        Assert.True(compared > 10, $"слишком мало сравнений: {compared}");
        Assert.True(nonzero > 0, "терм ни разу не двинулся — паритет вырожден");
        Assert.Equal(
            SoftEvaluator.Evaluate(p, current, rules).Total,
            SoftEvaluator.Evaluate(p, index.Snapshot(), rules).Total);

        // 100 свопов: before+delta == полный пересчёт; симметрия TrySwap(a,b)==TrySwap(b,a).
        int swapCompared = 0;
        for (int i = 0; i < 100; i++)
        {
            var a = occIds[rng.Next(occIds.Count)];
            var b = occIds[rng.Next(occIds.Count)];
            if (a == b) continue;
            var (allowed, delta, _) = index.TrySwap(a, b);
            var (allowed2, delta2, _) = index.TrySwap(b, a);
            Assert.Equal(allowed, allowed2);
            if (!allowed) continue;
            Assert.Equal(delta, delta2); // INV-D7: симметрия обмена
            long before = SoftEvaluator.Evaluate(p, index.Snapshot(), rules).Total;
            var pa = index.Position(a);
            var pb = index.Position(b);
            var hypo = index.Snapshot().Select(x =>
                x.OccurrenceId == a ? new PlacedLesson
                    { OccurrenceId = a, DayIndex = pb.Day, SlotIndex = pb.Slot, RoomId = pa.Room }
                : x.OccurrenceId == b ? new PlacedLesson
                    { OccurrenceId = b, DayIndex = pa.Day, SlotIndex = pa.Slot, RoomId = pb.Room }
                : x).ToList();
            Assert.Equal(before + delta, SoftEvaluator.Evaluate(p, hypo, rules).Total);
            swapCompared++;
            if (swapCompared % 5 == 0) index.CommitSwap(a, b);
        }
        Assert.True(swapCompared > 0, "ни один своп не разрешён — паритет вырожден");
        Assert.Equal(
            SoftEvaluator.Evaluate(p, index.Snapshot(), rules).Total,
            SoftEvaluator.Evaluate(p, index.Snapshot(), rules).Total);
    }

    // Compare-тест CountUnits через публичный API (S9-ACCEPTED):
    // best — дубль рядом, other — дубль врозь без окон (между ними другой урок).
    [Fact]
    public void Compare_DoublesUnits_ViaPublicApi()
    {
        var cls = new SchoolClass
        {
            AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5,
            StudentCount = 25, MaxLessonsPerDay = 7,
        };
        var t1 = new Teacher { Name = "Иванов", MaxLessonsPerDay = 10 };
        var t2 = new Teacher { Name = "Петров", MaxLessonsPerDay = 10 };
        var math = new Subject { Name = "Мат", MaxPerDay = 2 };
        var rus = new Subject { Name = "Рус", MaxPerDay = 2 };
        var input = new ProblemInput([cls], [t1, t2], [math, rus],
            [
                new() { ClassId = cls.Id, SubjectId = math.Id, TeacherId = t1.Id, HoursPerWeek = 2 },
                new() { ClassId = cls.Id, SubjectId = rus.Id, TeacherId = t2.Id, HoursPerWeek = 1 },
            ],
            [], [], [], DaysCount: 3, SlotsPerDay: 5);
        var (p, errors) = ProblemBuilder.Build(input);
        Assert.Empty(errors);
        var mathOcc = p!.Occurrences.Where(o => o.SubjectId == math.Id).ToList();
        var rusOcc = p.Occurrences.Single(o => o.SubjectId == rus.Id);
        PlacedLesson At(LessonOccurrence o, int day, int slot) =>
            new() { OccurrenceId = o.Id, DayIndex = day, SlotIndex = slot };
        // best: мат {1,2} + рус {3} — дубль рядом, окон нет.
        var best = ScheduleCandidate.Create(p,
            [At(mathOcc[0], 0, 1), At(mathOcc[1], 0, 2), At(rusOcc, 0, 3)], 1, 0, "A");
        // other: мат {1,3} + рус {2} — дубль врозь, окон нет (distinct {1,2,3}).
        var other = ScheduleCandidate.Create(p,
            [At(mathOcc[0], 0, 1), At(mathOcc[1], 0, 3), At(rusOcc, 0, 2)], 2, 0, "A");
        Assert.NotNull(best);
        Assert.NotNull(other);
        Assert.Equal(0, best!.Breakdown.Components
            .Single(c => c.Code == "doubles-adjacency").Value);
        Assert.Equal(10, other!.Breakdown.Components
            .Single(c => c.Code == "doubles-adjacency").Value);
        var lines = QualityExplainer.Compare(p, best, other);
        Assert.Contains(lines, l => l.Contains("хуже") && l.Contains("разбросанных сдвоенных"));
        var explain = QualityExplainer.Explain(p, other);
        Assert.Contains(explain, l => l.Contains("разбросан") && l.Contains("день 1"));
    }
}
