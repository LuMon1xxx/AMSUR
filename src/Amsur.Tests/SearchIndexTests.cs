using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// EPIC-H H5.1/H10: паритет SearchIndex vs IncrementalEvaluator (D-08).
// Индекс обязан давать те же allowed/delta на любом (размещение, ход),
// иначе LocalSearch на индексе расходился бы со старой семантикой.
public sealed class SearchIndexTests
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

    private static SchedulingProblem Mid()
    {
        var classes = Enumerable.Range(0, 3).Select(i => new SchoolClass
        {
            AcademicYearId = Guid.NewGuid(), Name = $"К{i}", Grade = 5, StudentCount = 25
        }).ToList();
        var teachers = Enumerable.Range(0, 3).Select(i => new Teacher
        {
            Name = $"У{i}", MaxLessonsPerDay = 6
        }).ToList();
        var subjects = Enumerable.Range(0, 3).Select(i => new Subject
        {
            Name = $"П{i}", MaxPerDay = 2
        }).ToList();
        var curriculum = new List<CurriculumItem>();
        foreach (var c in classes)
            for (int s = 0; s < 3; s++)
                curriculum.Add(new CurriculumItem
                {
                    ClassId = c.Id, SubjectId = subjects[s].Id,
                    TeacherId = teachers[s].Id, HoursPerWeek = 4
                });
        var rooms = Enumerable.Range(0, 2).Select(i => new Room
        {
            Name = $"R{i}", PhysicalCapacity = 30, MaxSimultaneousGroups = 2
        }).ToList();
        // Сплит: ин.яз делят A/B с sync (покрывает sync-ветку индекса).
        var groups = new List<StudentGroup>();
        var splitTeachers = new Dictionary<Guid, (Guid, Guid)>();
        foreach (var c in classes)
        {
            var gA = new StudentGroup { ClassId = c.Id, Name = "A" };
            var gB = new StudentGroup { ClassId = c.Id, Name = "B" };
            groups.Add(gA); groups.Add(gB);
            var item = curriculum.First(x => x.ClassId == c.Id && x.SubjectId == subjects[2].Id);
            item.SplitSubgroups = true;
            splitTeachers[item.Id] = (teachers[2].Id, teachers[0].Id);
        }
        var input = new ProblemInput(classes, teachers, subjects, curriculum,
            groups, [], [], DaysCount: 5, SlotsPerDay: 6,
            SplitTeachers: splitTeachers, rooms: rooms);
        var (p, e) = ProblemBuilder.Build(input);
        Assert.Empty(e);
        return p!;
    }

    // MidSlack: та же структура (сплиты+кабинеты), но у каждого класса СВОИ учителя
    // (нагрузка ~4ч/нед — slack). D-28: parity-тестам со clean-стартом нужен
    // выполнимый без окон экземпляр; учительское голодание Mid — не их тема.
    private static SchedulingProblem MidSlack()
    {
        var classes = Enumerable.Range(0, 3).Select(i => new SchoolClass
        {
            AcademicYearId = Guid.NewGuid(), Name = $"К{i}", Grade = 5, StudentCount = 25
        }).ToList();
        var teachers = Enumerable.Range(0, 12).Select(i => new Teacher
        {
            Name = $"У{i}", MaxLessonsPerDay = 6
        }).ToList();
        var subjects = Enumerable.Range(0, 3).Select(i => new Subject
        {
            Name = $"П{i}", MaxPerDay = 2
        }).ToList();
        var curriculum = new List<CurriculumItem>();
        var groups = new List<StudentGroup>();
        var splitTeachers = new Dictionary<Guid, (Guid, Guid)>();
        foreach (var (c, ci) in classes.Select((c, i) => (c, i)))
        {
            for (int s = 0; s < 3; s++)
                curriculum.Add(new CurriculumItem
                {
                    ClassId = c.Id, SubjectId = subjects[s].Id,
                    TeacherId = teachers[ci * 4 + s].Id, HoursPerWeek = 4
                });
            var gA = new StudentGroup { ClassId = c.Id, Name = "A" };
            var gB = new StudentGroup { ClassId = c.Id, Name = "B" };
            groups.Add(gA); groups.Add(gB);
            var item = curriculum.First(x => x.ClassId == c.Id && x.SubjectId == subjects[2].Id);
            item.SplitSubgroups = true;
            splitTeachers[item.Id] = (teachers[ci * 4 + 2].Id, teachers[ci * 4 + 3].Id);
        }
        // D-28d: 3 кабинета (room-slack): давление кабинетов — тема RoomConstrained,
        // здесь нужен выполнимый без окон экземпляр для parity со clean-старта.
        var rooms = Enumerable.Range(0, 3).Select(i => new Room
        {
            Name = $"R{i}", PhysicalCapacity = 30, MaxSimultaneousGroups = 2
        }).ToList();
        var input = new ProblemInput(classes, teachers, subjects, curriculum,
            groups, [], [], DaysCount: 5, SlotsPerDay: 6,
            SplitTeachers: splitTeachers, rooms: rooms);
        var (p, e) = ProblemBuilder.Build(input);
        Assert.Empty(e);
        return p!;
    }

    private static List<PlacedLesson> GreedyStart(SchedulingProblem p, int seed = 3)
    {
        var g = GreedyPlacer.Place(p, seed);
        Assert.Empty(g.Unplaced);
        return g.Placed.Select(kv => new PlacedLesson
        {
            OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
            SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
        }).ToList();
    }

    private static void ParityOn(SchedulingProblem p, List<PlacedLesson> placements, int moves, int seed)
    {
        var rng = new Random(seed);
        var occIds = p.Occurrences.Select(o => o.Id).ToList();
        var current = placements;
        var index = SearchIndex.Build(p, current);
        for (int i = 0; i < moves; i++)
        {
            var occId = occIds[rng.Next(occIds.Count)];
            var pos = current.First(x => x.OccurrenceId == occId);
            int day = rng.Next(p.DaysCount);
            int slot = rng.Next(1, p.SlotsPerDay + 1);
            var move = new CandidateMove(occId, day, slot, pos.RoomId);
            var ev = IncrementalEvaluator.Evaluate(p, current, move);
            var (allowed, delta, _) = index.TryMove(move);
            // D-28: движок (SearchIndex) осознанно мягче редактора (IncrementalEvaluator):
            // редактор запрещает ходы с окнами/поздним стартом (HARD-preview),
            // движок идёт через них по soft-градиенту к CompactRepair. Паритет-gate:
            // разрешённое редактором разрешено и движком; дельты равны где обе считают.
            bool evAllowed = ev.Severity != EvaluationSeverity.Forbidden;
            Assert.True(!evAllowed || allowed);
            if (evAllowed)
                Assert.Equal(ev.DeltaTotal, delta);
            // Каждый 5-й ход коммитим (если разрешён движком) — паритет и после мутаций.
            if (i % 5 == 0 && allowed)
            {
                index.Commit(move);
                current = current.Where(x => x.OccurrenceId != occId)
                    .Concat([new PlacedLesson
                    {
                        OccurrenceId = occId, DayIndex = day, SlotIndex = slot, RoomId = pos.RoomId
                    }]).ToList();
                long full = SoftEvaluator.Evaluate(p, current).Total;
                long viaIndex = SoftEvaluator.Evaluate(p, index.Snapshot()).Total;
                Assert.Equal(full, viaIndex);
            }
        }
    }

    [Fact]
    public void Parity_Tiny_200Moves() =>
        ParityOn(Tiny(), GreedyStart(Tiny()), moves: 200, seed: 42);

    [Fact]
    public void Parity_MidSplitRooms_500Moves() =>
        ParityOn(Mid(), GreedyStart(Mid()), moves: 500, seed: 42);

    // Комнаты тоже двигаем (room-only ходы): индекс обязан совпадать и там.
    // D-28: стартуем с repaired-clean (на грязном текущем редактор запрещает всё).
    [Fact]
    public void Parity_RoomMoves()
    {
        var p = MidSlack();
        var repaired = CompactRepair.Repair(p, GreedyStart(p));
        Assert.True(PlacementValidator.Validate(p, repaired.Placements).IsValid);
        var current = repaired.Placements;
        var index = SearchIndex.Build(p, current);
        var rng = new Random(7);
        var roomIds = p.Rooms.Values.Select(r => (Guid?)r.Id).Concat([(Guid?)null]).ToList();
        for (int i = 0; i < 200; i++)
        {
            var target = current[rng.Next(current.Count)];
            var move = new CandidateMove(target.OccurrenceId, target.DayIndex,
                target.SlotIndex, roomIds[rng.Next(roomIds.Count)]);
            var ev = IncrementalEvaluator.Evaluate(p, current, move);
            var (allowed, delta, _) = index.TryMove(move);
            Assert.Equal(ev.Severity != EvaluationSeverity.Forbidden, allowed);
            Assert.Equal(ev.DeltaTotal, delta);
        }
    }

    // H5.2: swap-дельта == полному пересчёту, коммит держит FullValidator-clean.
    [Fact]
    public void SwapParity_Mid()
    {
        var p = MidSlack();
        // Пайплайн solver: greedy → repair → LS. Тест стартует с repaired (D-28).
        var repaired = CompactRepair.Repair(p, GreedyStart(p));
        Assert.True(PlacementValidator.Validate(p, repaired.Placements).IsValid);
        var index = SearchIndex.Build(p, repaired.Placements);
        Assert.True(PlacementValidator.Validate(p, index.Snapshot()).IsValid);
        var rng = new Random(11);
        var ids = p.Occurrences.Select(o => o.Id).ToList();
        int committed = 0;
        for (int i = 0; i < 300 && committed < 20; i++)
        {
            var a = ids[rng.Next(ids.Count)];
            var b = ids[rng.Next(ids.Count)];
            var (allowed, delta, _) = index.TrySwap(a, b);
            if (!allowed || delta >= 0) continue;
            long before = SoftEvaluator.Evaluate(p, index.Snapshot()).Total;
            // Коммитим только clean-сохраняющие обмены (движок мягче валидатора, D-28).
            var pa = index.Position(a);
            var pb = index.Position(b);
            var hypo = index.Snapshot().Select(x =>
                x.OccurrenceId == a ? new PlacedLesson { OccurrenceId = a, DayIndex = pb.Day, SlotIndex = pb.Slot, RoomId = pa.Room }
                : x.OccurrenceId == b ? new PlacedLesson { OccurrenceId = b, DayIndex = pa.Day, SlotIndex = pa.Slot, RoomId = pb.Room }
                : x).ToList();
            if (!PlacementValidator.Validate(p, hypo).IsValid) continue;
            index.CommitSwap(a, b);
            var after = index.Snapshot();
            Assert.Equal(before + delta, SoftEvaluator.Evaluate(p, after).Total);
            Assert.True(PlacementValidator.Validate(p, after).IsValid);
            committed++;
        }
    }

    // LS на индексе находит тот же оптимум и не ломает валидность.
    [Fact]
    public void IndexedLS_TinyOptimum()
    {
        var p = Tiny();
        var start = p.Occurrences.Zip([(0, 1), (0, 3), (1, 1)])
            .Select(x => new PlacedLesson
            {
                OccurrenceId = x.First.Id, DayIndex = x.Second.Item1, SlotIndex = x.Second.Item2
            }).ToList();
        var res = LocalSearch.Improve(p, start, TimeSpan.FromSeconds(5), seed: 42);
        Assert.Equal(0, res.SoftTotal);
        Assert.True(PlacementValidator.Validate(p, res.Placements).IsValid);
        Assert.Equal(0, SoftEvaluator.Evaluate(p, res.Placements).Total);
    }

    // Накопленный curSoft обязан совпадать с полным пересчётом в конце.
    [Fact]
    public void IndexedLS_AccumulatedEqualsFull()
    {
        var p = Mid();
        var res = LocalSearch.Improve(p, GreedyStart(p), TimeSpan.FromSeconds(5), seed: 11);
        Assert.Equal(SoftEvaluator.Evaluate(p, res.Placements).Total, res.SoftTotal);
        // LS держит структурную валидность скоупами; компактность (soft-travel, D-28) —
        // зона CompactRepair: здесь допустимы только student-gap/late-start.
        var hard = PlacementValidator.Validate(p, res.Placements).HardViolations;
        Assert.DoesNotContain(hard, v => v.Code != "student-gap" && v.Code != "student-late-start");
    }
}
