using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// НДТП-7 (02.10.2026): 7 требований школы. Каждый пункт — хотя бы один тест.
public sealed class Ndtp7Tests
{
    private static (SchedulingProblem P, SchoolClass C, Teacher T, Subject Math, Subject Extra)
        TwoSubjectProblem(int days = 5, int slots = 5)
    {
        var year = Guid.NewGuid();
        var c = new SchoolClass { AcademicYearId = year, Name = "9А", Grade = 9, StudentCount = 25, MaxLessonsPerDay = 7 };
        var t = new Teacher { Name = "Учитель", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Математика", MaxPerDay = 5, Difficulty = 8 };
        var extra = new Subject { Name = "Классный час", MaxPerDay = 1, Difficulty = 1, IsNonLesson = true };
        var cur = new List<CurriculumItem>
        {
            new() { ClassId = c.Id, SubjectId = math.Id, TeacherId = t.Id, HoursPerWeek = 3 },
            new() { ClassId = c.Id, SubjectId = extra.Id, TeacherId = t.Id, HoursPerWeek = 1 },
        };
        var (p, errors) = ProblemBuilder.Build(new ProblemInput(
            [c], [t], [math, extra], cur, [], [], [],
            DaysCount: days, SlotsPerDay: slots, flex: FlexSettings.Neutral));
        Assert.NotNull(p);
        Assert.Empty(errors);
        foreach (var o in p!.Occurrences.Where(o => o.SubjectId == extra.Id))
            o.IsExtra = true;
        return (p!, c, t, math, extra);
    }

    private static PlacedLesson At(Guid occId, int day, int slot) =>
        new() { OccurrenceId = occId, DayIndex = day, SlotIndex = slot };

    // 1. Классный час: занимает слот, но не создаёт окон и поздних стартов.
    [Fact]
    public void Extra_NoGapNoLateStart()
    {
        var (p, c, _, math, extra) = TwoSubjectProblem();
        var mathOccs = p.Occurrences.Where(o => o.SubjectId == math.Id).ToList();
        var extraOcc = Assert.Single(p.Occurrences.Where(o => o.SubjectId == extra.Id));
        var placements = new List<PlacedLesson>
        {
            At(extraOcc.Id, 0, 1),
            At(mathOccs[0].Id, 0, 2),
            At(mathOccs[1].Id, 1, 1),
            At(mathOccs[2].Id, 2, 1),
        };
        var vr = PlacementValidator.Validate(p, placements);
        Assert.True(vr.IsValid);
        var bd = SoftEvaluator.Evaluate(p, placements);
        Assert.Equal(0, bd.Components.Single(x => x.Code == "student-gap").Value);
        Assert.Equal(0, bd.Components.Single(x => x.Code == "student-late-start").Value);
    }

    // 1б. Коллизия с внеурочкой — всё равно Hard (слот занят).
    [Fact]
    public void Extra_CollisionStillHard()
    {
        var (p, _, _, math, extra) = TwoSubjectProblem();
        var mathOcc = p.Occurrences.First(o => o.SubjectId == math.Id);
        var extraOcc = Assert.Single(p.Occurrences.Where(o => o.SubjectId == extra.Id));
        var placements = new List<PlacedLesson>
        {
            At(extraOcc.Id, 0, 1),
            At(mathOcc.Id, 0, 1),
        };
        var vr = PlacementValidator.Validate(p, placements);
        Assert.Contains(vr.HardViolations, v =>
            v.Code == PhysicalRuleCodes.TeacherCollision || v.Code == PhysicalRuleCodes.GroupCollision);
    }

    // 2. Пик Вт/Ср/Пт: тяжёлый в Пн — единица; во Вт — ноль.
    [Fact]
    public void PeakDays_HeavyOutsidePeakCounts()
    {
        var (p, _, _, math, _) = TwoSubjectProblem();
        var occs = p.Occurrences.Where(o => o.SubjectId == math.Id).ToList();
        var mon = new List<PlacedLesson> { At(occs[0].Id, 0, 1) };
        var tue = new List<PlacedLesson> { At(occs[0].Id, 1, 1) };
        var rs = EffectiveRuleSet.Default;
        Assert.True(SoftEvaluator.Evaluate(p, mon, rs).Components.Single(x => x.Code == "peak-days").Value > 0);
        Assert.Equal(0, SoftEvaluator.Evaluate(p, tue, rs).Components.Single(x => x.Code == "peak-days").Value);
    }

    // 3. Физра 3 дня подряд — Hard; 2 дня — чисто; relax — Warning.
    [Fact]
    public void PeConsecutive_ThreeDaysHard_TwoDaysClean()
    {
        var year = Guid.NewGuid();
        var c = new SchoolClass { AcademicYearId = year, Name = "5А", Grade = 5, MaxLessonsPerDay = 6 };
        var t = new Teacher { Name = "Физрук", MaxLessonsPerDay = 6 };
        var pe = new Subject
        {
            Name = "Физическая культура и здоровье",
            MaxPerDay = 1, Difficulty = 3, IsPhysicalEducation = true
        };
        var cur = new List<CurriculumItem>
        {
            new() { ClassId = c.Id, SubjectId = pe.Id, TeacherId = t.Id, HoursPerWeek = 3 },
        };
        var (p, errors) = ProblemBuilder.Build(new ProblemInput(
            [c], [t], [pe], cur, [], [], [], DaysCount: 5, SlotsPerDay: 5, flex: FlexSettings.Neutral));
        Assert.NotNull(p);
        Assert.Empty(errors);
        var occs = p!.Occurrences.ToList();
        List<PlacedLesson> On(params int[] days) =>
            occs.Select((o, i) => At(o.Id, days[i % days.Length], 1)).ToList();
        var bad = PlacementValidator.Validate(p!, On(0, 1, 2));
        Assert.Contains(bad.HardViolations, v => v.Code == "sanpin-pe-spacing");
        var good = PlacementValidator.Validate(p!, On(0, 1, 3));
        Assert.DoesNotContain(good.HardViolations, v => v.Code == "sanpin-pe-spacing");
        var relaxed = RuleResolver.Resolve("STANDARD",
            new Dictionary<string, long> { ["sanpin-pe-spacing"] = 10 },
            new HashSet<string> { "sanpin-pe-spacing" });
        var warned = PlacementValidator.Validate(p!, On(0, 1, 2), relaxed);
        Assert.DoesNotContain(warned.HardViolations, v => v.Code == "sanpin-pe-spacing");
        Assert.Contains(warned.Warnings, v => v.Code == "sanpin-pe-spacing");
    }

    // 3б. PeSpacingRepair: тройка чинится без новых окон.
    [Fact]
    public void PeRepair_FixesTripleWithoutGaps()
    {
        var year = Guid.NewGuid();
        var c = new SchoolClass { AcademicYearId = year, Name = "5А", Grade = 5, MaxLessonsPerDay = 6 };
        var t = new Teacher { Name = "Физрук", MaxLessonsPerDay = 6 };
        var t2 = new Teacher { Name = "Математик", MaxLessonsPerDay = 6 };
        var pe = new Subject
        {
            Name = "Физическая культура и здоровье", MaxPerDay = 2, Difficulty = 1,
            IsPhysicalEducation = true
        };
        var math = new Subject { Name = "Математика", MaxPerDay = 2, Difficulty = 8 };
        var cur = new List<CurriculumItem>
        {
            new() { ClassId = c.Id, SubjectId = pe.Id, TeacherId = t.Id, HoursPerWeek = 3 },
            new() { ClassId = c.Id, SubjectId = math.Id, TeacherId = t2.Id, HoursPerWeek = 2 },
        };
        var (p, errors) = ProblemBuilder.Build(new ProblemInput(
            [c], [t, t2], [pe, math], cur, [], [], [], DaysCount: 5, SlotsPerDay: 5,
            flex: FlexSettings.Neutral));
        Assert.NotNull(p);
        Assert.Empty(errors);
        var pes = p!.Occurrences.Where(o => o.SubjectId == pe.Id).ToList();
        var maths = p.Occurrences.Where(o => o.SubjectId == math.Id).ToList();
        var start = new List<PlacedLesson>
        {
            At(pes[0].Id, 0, 1), At(pes[1].Id, 1, 1), At(pes[2].Id, 2, 1),
            At(maths[0].Id, 0, 2), At(maths[1].Id, 3, 1),
        };
        Assert.Single(PlacementValidator.Validate(p, start).HardViolations,
            v => v.Code == "sanpin-pe-spacing");
        var fixed_ = PeSpacingRepair.Repair(p, start);
        var vr = PlacementValidator.Validate(p, fixed_);
        Assert.True(vr.IsValid);
    }

    // 4б. Край считается ПОПРЕДМЕТНО (04.10.2026, уточнение школы):
    // математика в Пн на краю + физика во Вт на краю — чисто;
    // математика дважды — превышение.
    [Fact]
    public void EdgeOnce_PerSubject()
    {
        var year = Guid.NewGuid();
        var c = new SchoolClass { AcademicYearId = year, Name = "9А", Grade = 9, MaxLessonsPerDay = 7 };
        var t = new Teacher { Name = "Учитель", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Математика", MaxPerDay = 5, Difficulty = 8 };
        var phys = new Subject { Name = "Физика", MaxPerDay = 5, Difficulty = 8 };
        var music = new Subject { Name = "Музыка", MaxPerDay = 5, Difficulty = 2 };
        var cur = new List<CurriculumItem>
        {
            new() { ClassId = c.Id, SubjectId = math.Id, TeacherId = t.Id, HoursPerWeek = 2 },
            new() { ClassId = c.Id, SubjectId = phys.Id, TeacherId = t.Id, HoursPerWeek = 1 },
            new() { ClassId = c.Id, SubjectId = music.Id, TeacherId = t.Id, HoursPerWeek = 4 },
        };
        var (p, errors) = ProblemBuilder.Build(new ProblemInput(
            [c], [t], [math, phys, music], cur, [], [], [],
            DaysCount: 5, SlotsPerDay: 5, flex: FlexSettings.Neutral));
        Assert.NotNull(p);
        Assert.Empty(errors);
        var rs = EffectiveRuleSet.Default;
        var maths = p!.Occurrences.Where(o => o.SubjectId == math.Id).ToList();
        var ph = p.Occurrences.Single(o => o.SubjectId == phys.Id);
        var mus = p.Occurrences.Where(o => o.SubjectId == music.Id).ToList();
        // Разные предметы в середине дня: краёв 0 (музыка некраевая вообще).
        var ok = new List<PlacedLesson>
        {
            At(mus[0].Id, 0, 1), At(maths[0].Id, 0, 2), At(mus[1].Id, 0, 3),
            At(mus[2].Id, 1, 1), At(ph.Id, 1, 2), At(mus[3].Id, 1, 3),
            At(maths[1].Id, 2, 2),
        };
        Assert.Equal(0, SoftEvaluator.Evaluate(p, ok, rs).Components.Single(x => x.Code == "edge-once").Value);
        // Тот же предмет на краю дважды: превышение есть.
        var bad = new List<PlacedLesson>
        {
            At(maths[0].Id, 0, 1), At(mus[0].Id, 0, 2), At(mus[1].Id, 0, 3),
            At(mus[2].Id, 1, 1), At(mus[3].Id, 1, 2), At(maths[1].Id, 1, 3),
            At(ph.Id, 2, 2),
        };
        Assert.True(SoftEvaluator.Evaluate(p, bad, rs).Components.Single(x => x.Code == "edge-once").Value > 0);
    }

    [Fact]
    public void EdgeOnce_TwiceCounts_OnceClean()
    {
        var (p, _, _, math, _) = TwoSubjectProblem();
        var occs = p.Occurrences.Where(o => o.SubjectId == math.Id).ToList();
        Assert.True(occs.Count >= 2);
        var twice = new List<PlacedLesson>
        {
            At(occs[0].Id, 0, 1), At(occs[0].Id, 0, 2), // день 0: край слот 1
            At(occs[1].Id, 1, 1), At(occs[1].Id, 1, 3), // день 1: край слот 1
        };
        // Два крайних урока в разные дни недели: превышение нормы «1 раз».
        var single = new List<PlacedLesson> { At(occs[0].Id, 0, 3) };
        var rs = EffectiveRuleSet.Default;
        Assert.True(SoftEvaluator.Evaluate(p, twice, rs).Components.Single(x => x.Code == "edge-once").Value > 0);
        Assert.Equal(0, SoftEvaluator.Evaluate(p, single, rs).Components.Single(x => x.Code == "edge-once").Value);
    }

    // 5. Чередование: Т-Т-Л — единица; Т-Л-Т — ноль.
    [Fact]
    public void Alternation_SameNeighboursCount()
    {
        Assert.Equal(1, SoftUnits.AlternationBreaks([true, true, false]));
        Assert.Equal(0, SoftUnits.AlternationBreaks([true, false, true]));
        Assert.Equal(2, SoftUnits.AlternationBreaks([false, false, false]));
    }

    // 6. Норма 25 ч/нед: 26 — ошибка чекера; 25 + внеурочка — чисто.
    [Fact]
    public void TeacherWeekly25_OverLimitFlagged_ExtraExcluded()
    {
        var year = Guid.NewGuid();
        var c = new SchoolClass { AcademicYearId = year, Name = "9А", Grade = 9, MaxLessonsPerDay = 7 };
        var t = new Teacher { Name = "Перегруженный", MaxLessonsPerDay = 9 };
        var math = new Subject { Name = "Математика", MaxPerDay = 9, Difficulty = 8 };
        var extra = new Subject { Name = "ВОВ", MaxPerDay = 9, Difficulty = 1, IsNonLesson = true };
        var occs = new List<LessonOccurrence>();
        for (int i = 0; i < 26; i++)
            occs.Add(new LessonOccurrence
            {
                ClassId = c.Id, SubjectId = math.Id, TeacherId = t.Id,
                StableKey = $"9А|Математика|Перегруженный|Whole#{i}"
            });
        var extraOcc = new LessonOccurrence
        {
            ClassId = c.Id, SubjectId = extra.Id, TeacherId = t.Id, IsExtra = true,
            StableKey = "9А|ВОВ|Перегруженный|Whole#0"
        };
        occs.Add(extraOcc);
        var p = new SchedulingProblem
        {
            Occurrences = occs,
            Classes = new() { [c.Id] = c },
            Teachers = new() { [t.Id] = t },
            Subjects = new() { [math.Id] = math, [extra.Id] = extra },
            DaysCount = 5, SlotsPerDay = 7,
        };
        var placed = occs.Select((o, i) =>
            At(o.Id, (i / 7) % 5, i % 7 + 1)).ToList();
        var over = SanPinChecker.Check(p, placed);
        Assert.Contains(over, f => f.Text.Contains("26 ч/нед"));
        // Только норма: 25 уроков + 5 внеурочек — чисто.
        var okOccs = occs.Take(25).ToList();
        for (int i = 0; i < 5; i++)
            okOccs.Add(new LessonOccurrence
            {
                ClassId = c.Id, SubjectId = extra.Id, TeacherId = t.Id, IsExtra = true,
                StableKey = $"9А|ВОВ|Перегруженный|Whole#x{i}"
            });
        p.Occurrences.Clear();
        p.Occurrences.AddRange(okOccs);
        var okPlaced = okOccs.Select((o, i) => At(o.Id, (i / 7) % 5, i % 7 + 1)).ToList();
        Assert.DoesNotContain(SanPinChecker.Check(p, okPlaced), f => f.Text.Contains("ч/нед"));
    }

    // 7. ВОВ из нагрузки: IsExtra + только край смены.
    [Fact]
    public void VovLoadDriven_ExtraAndEdgeOnly()
    {
        var rows = new List<LoadRow>
        {
            new("9А", "Математика", 2, "Учитель", false, null, null),
            new("9А", "ВОВ", 1, "Классная", false, null, null),
        };
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows);
        var vov = data.Subjects.Single(s => s.Name == "ВОВ");
        Assert.True(vov.IsNonLesson);
        var (p, errors) = ProblemBuilder.Build(data.ToProblemInput());
        Assert.NotNull(p);
        Assert.Empty(errors);
        var vovOcc = Assert.Single(p!.Occurrences.Where(o => o.SubjectId == vov.Id));
        Assert.True(vovOcc.IsExtra);
        var allowed = p!.AllowedSlots[vovOcc.Id];
        // Край смены класса 9А (первый/последний слот смены) — не более 2 слотов.
        Assert.True(allowed.Count <= 2);
    }

    // 7б. CommonLesson-синтез: классный час — внеурочка.
    [Fact]
    public void CommonLesson_SynthesizedIsExtra()
    {
        var year = Guid.NewGuid();
        var a = new SchoolClass
        {
            AcademicYearId = year, Name = "5А", Grade = 5, StudentCount = 25, ClassTeacherId = null
        };
        var ta = new Teacher { Name = "КласснаяА", MaxLessonsPerDay = 6 };
        a.ClassTeacherId = ta.Id;
        var math = new Subject { Name = "Мат", MaxPerDay = 2 };
        var cur = new List<CurriculumItem>
        {
            new() { ClassId = a.Id, SubjectId = math.Id, TeacherId = ta.Id, HoursPerWeek = 2 },
        };
        var common = new CommonLesson
        {
            Enabled = true, DayIndex = 3, SlotIndex = 1, GradesCsv = "5", UseOwnRooms = true
        };
        var (p, errors) = ProblemBuilder.Build(new ProblemInput(
            [a], [ta], [math], cur, [], [], [],
            DaysCount: 5, SlotsPerDay: 5, commonLesson: common, flex: FlexSettings.Default));
        Assert.NotNull(p);
        Assert.Empty(errors);
        var commons = p!.Occurrences.Where(o => p.Subjects[o.SubjectId].Name == "Классный час").ToList();
        Assert.NotEmpty(commons);
        Assert.All(commons, o => Assert.True(o.IsExtra));
        Assert.True(p.Subjects[commons[0].SubjectId].IsNonLesson);
    }

    // 8. Импортёр метит внеурочку по именам.
    [Fact]
    public void Importer_NonLessonNamesFlagged()
    {
        Assert.True(SubjectTiers.IsNonLessonName("Классный час"));
        Assert.True(SubjectTiers.IsNonLessonName("ВОВ"));
        Assert.True(SubjectTiers.IsNonLessonName("Великая Отечественная война"));
        Assert.True(SubjectTiers.IsNonLessonName("Факультатив по математике"));
        Assert.False(SubjectTiers.IsNonLessonName("Математика"));
        var rows = new List<LoadRow>
        {
            new("9А", "Факультатив по физике", 1, "Физик", false, null, null),
        };
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows);
        Assert.True(data.Subjects.Single(s => s.Name == "Факультатив по физике").IsNonLesson);
    }

    // 9. Паритет индекса с новыми термами: дельта == полный пересчёт.
    [Fact]
    public void SearchIndex_ParityWithNewTerms()
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
        var rs = EffectiveRuleSet.Default;
        var rnd = new Random(7);
        var placements = p!.Occurrences
            .Select(o => new PlacedLesson { OccurrenceId = o.Id, DayIndex = rnd.Next(5), SlotIndex = rnd.Next(1, 6) })
            .ToList();
        var idx = SearchIndex.Build(p!, placements, rs);
        for (int i = 0; i < 60; i++)
        {
            var occ = p!.Occurrences[rnd.Next(p.Occurrences.Count)];
            var mv = new CandidateMove(occ.Id, rnd.Next(5), rnd.Next(1, 6), null);
            var (allowed, delta, _) = idx.TryMove(mv);
            if (!allowed)
            {
                // Коллизия клеток: дельта 0, полный пересчёт не сравниваем.
                Assert.Equal(0, delta);
                continue;
            }
            var before = SoftEvaluator.Evaluate(p!, idx.Snapshot(), rs).Total;
            var hypo = idx.Snapshot().Select(x =>
                x.OccurrenceId == occ.Id
                    ? new PlacedLesson { OccurrenceId = x.OccurrenceId, DayIndex = mv.DayIndex, SlotIndex = mv.SlotIndex }
                    : x).ToList();
            var after = SoftEvaluator.Evaluate(p!, hypo, rs).Total;
            Assert.Equal(after - before, delta);
            idx.Commit(mv);
        }
    }
}
