using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// P2 (R1–R9): solver-логика — кандидаты, ONLY-hard, R3-синтез, key-aware,
// crowding/split/heavy soft, R7-валидатор, R8-веса, паритет с flex.
public sealed class FlexP2Tests
{
    private static readonly Guid Year = Guid.NewGuid();

    private static (SchoolClass Cls, Teacher T, Subject S) Trio(
        string cls = "5А", int grade = 5, string teacher = "Иванов", string subj = "Мат")
    {
        var c = new SchoolClass { AcademicYearId = Year, Name = cls, Grade = grade, StudentCount = 25 };
        var t = new Teacher { Name = teacher, MaxLessonsPerDay = 6 };
        var s = new Subject { Name = subj, MaxPerDay = 2 };
        return (c, t, s);
    }

    private static SchedulingProblem Build(ProblemInput input)
    {
        var (p, e) = ProblemBuilder.Build(input);
        Assert.Empty(e);
        Assert.NotNull(p);
        return p!;
    }

    private static ProblemInput TinyInput(
        SchoolClass cls, Teacher teacher, Subject subj, int hours = 1,
        List<Room>? rooms = null, int days = 2, int slots = 3)
    {
        var item = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = subj.Id, TeacherId = teacher.Id, HoursPerWeek = hours
        };
        return new ProblemInput([cls], [teacher], [subj], [item],
            [], [], [], DaysCount: days, SlotsPerDay: slots, rooms: rooms ?? []);
    }

    // R1: manual-only исключён из кандидатов solver (greedy его не берёт).
    [Fact]
    public void R1_ManualOnly_ExcludedFromSolver()
    {
        var (cls, t, s) = Trio();
        var manual = new Room { Name = "Каб Я", PhysicalCapacity = 30, IsManualOnly = true };
        var normal = new Room { Name = "Каб А", PhysicalCapacity = 30 };
        var p = Build(TinyInput(cls, t, s, rooms: [manual, normal]));
        var placed = GreedyPlacer.Place(p);
        Assert.Empty(placed.Unplaced);
        var roomId = Assert.Single(placed.Placed.Values).RoomId;
        Assert.Equal(normal.Id, roomId);
    }

    // R1: только manual-only → ModelInvalid быстро (кандидатов нет).
    [Fact]
    public async Task R1_OnlyManualRoom_ModelInvalid()
    {
        var (cls, t, s) = Trio();
        var manual = new Room { Name = "Зал", PhysicalCapacity = 30, IsManualOnly = true };
        var p = Build(TinyInput(cls, t, s, rooms: [manual]));
        var solver = new Amsur.Scheduling.OrTools.OrToolsSolver();
        var res = await solver.SolveAsync(p, CancellationToken.None);
        Assert.Equal(SolverStatus.ModelInvalid, res.Status);
    }

    // R1: ONLY-чужой — hard в валидаторе; свой предмет — чисто.
    [Fact]
    public void R1_OnlySubject_ValidatorHard()
    {
        var (cls, t, math) = Trio();
        var phys = new Subject { Name = "Физ", MaxPerDay = 2 };
        var only = new Room { Name = "Лаб", PhysicalCapacity = 30, OnlySubjectId = math.Id };
        var item2 = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = phys.Id, TeacherId = t.Id, HoursPerWeek = 1
        };
        var item1 = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = math.Id, TeacherId = t.Id, HoursPerWeek = 1
        };
        var input = new ProblemInput([cls], [t], [math, phys], [item1, item2],
            [], [], [], DaysCount: 2, SlotsPerDay: 3, rooms: [only]);
        var p = Build(input);
        var occMath = p.Occurrences.Single(o => o.SubjectId == math.Id);
        var occPhys = p.Occurrences.Single(o => o.SubjectId == phys.Id);
        // Чужая физика в ONLY-мат. кабинете — hard.
        var bad = new List<PlacedLesson>
        {
            new() { OccurrenceId = occMath.Id, DayIndex = 0, SlotIndex = 1, RoomId = null },
            new() { OccurrenceId = occPhys.Id, DayIndex = 0, SlotIndex = 2, RoomId = only.Id },
        };
        var vr = PlacementValidator.Validate(p, bad);
        Assert.Contains(vr.HardViolations, v => v.Code == "forbidden-room");
        // Своя математика — чисто.
        var good = new List<PlacedLesson>
        {
            new() { OccurrenceId = occMath.Id, DayIndex = 0, SlotIndex = 1, RoomId = only.Id },
            new() { OccurrenceId = occPhys.Id, DayIndex = 0, SlotIndex = 2, RoomId = null },
        };
        Assert.True(PlacementValidator.Validate(p, good).IsValid);
    }

    // R1: ручное назначение в manual-only разрешено (валидатор + preview).
    [Fact]
    public void R1_ManualOnly_ManualEditAllowed()
    {
        var (cls, t, s) = Trio();
        var manual = new Room { Name = "Зал", PhysicalCapacity = 30, IsManualOnly = true };
        var p = Build(TinyInput(cls, t, s, rooms: [manual]));
        var occ = Assert.Single(p.Occurrences);
        var placed = new List<PlacedLesson>
        {
            new() { OccurrenceId = occ.Id, DayIndex = 0, SlotIndex = 1, RoomId = manual.Id }
        };
        Assert.True(PlacementValidator.Validate(p, placed).IsValid);
        var ev = IncrementalEvaluator.Evaluate(p, placed,
            new CandidateMove(occ.Id, 1, 1, manual.Id));
        Assert.NotEqual(EvaluationSeverity.Forbidden, ev.Severity);
        // А ONLY-чужой вручную — запрещён.
        var only = new Room { Name = "Лаб", PhysicalCapacity = 30, OnlySubjectId = Guid.NewGuid() };
        var p2 = Build(TinyInput(cls, t, s, rooms: [only]));
        var occ2 = Assert.Single(p2.Occurrences);
        var placed2 = new List<PlacedLesson>
        {
            new() { OccurrenceId = occ2.Id, DayIndex = 0, SlotIndex = 1, RoomId = null }
        };
        var ev2 = IncrementalEvaluator.Evaluate(p2, placed2,
            new CandidateMove(occ2.Id, 1, 1, only.Id));
        Assert.Equal(EvaluationSeverity.Forbidden, ev2.Severity);
        Assert.Contains(ev2.HardViolations, v => v.Code == "forbidden-room");
    }

    // R1: greedy не ставит физику в ONLY-мат. кабинет.
    [Fact]
    public void R1_OnlySubject_SolverAvoids()
    {
        var (cls, t, math) = Trio(subj: "Мат");
        var phys = new Subject { Name = "Физ", MaxPerDay = 2 };
        var only = new Room { Name = "А-Лаб", PhysicalCapacity = 30, OnlySubjectId = math.Id };
        var uni = new Room { Name = "Я-Каб", PhysicalCapacity = 30 };
        var item = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = phys.Id, TeacherId = t.Id, HoursPerWeek = 1
        };
        var input = new ProblemInput([cls], [t], [math, phys], [item],
            [], [], [], DaysCount: 2, SlotsPerDay: 3, rooms: [only, uni]);
        var p = Build(input);
        var placed = GreedyPlacer.Place(p);
        Assert.Empty(placed.Unplaced);
        Assert.Equal(uni.Id, Assert.Single(placed.Placed.Values).RoomId);
    }

    // R3: по умолчанию выключен — синтеза нет.
    [Fact]
    public void R3_DisabledByDefault_NoSynthesis()
    {
        var data = SchoolDataImporter.Import(Guid.NewGuid(), DemoSchoolTests.DemoRows());
        var p = Build(data.ToProblemInput());
        Assert.DoesNotContain(p.Occurrences,
            o => p.Subjects[o.SubjectId].Name == "Классный час");
    }

    private static (ProblemInput Input, SchoolClass A, SchoolClass B) CommonInput(
        string gradesCsv = "5,6", int day = 0, int slot = 1, bool useOwn = true)
    {
        var year = Guid.NewGuid();
        var a = new SchoolClass { AcademicYearId = year, Name = "5А", Grade = 5, StudentCount = 25 };
        var b = new SchoolClass { AcademicYearId = year, Name = "6А", Grade = 6, StudentCount = 25 };
        var ta = new Teacher { Name = "КласснаяА", MaxLessonsPerDay = 6 };
        var tb = new Teacher { Name = "КласснаяБ", MaxLessonsPerDay = 6 };
        a.ClassTeacherId = ta.Id;
        b.ClassTeacherId = tb.Id;
        var math = new Subject { Name = "Мат", MaxPerDay = 2 };
        var cur = new List<CurriculumItem>
        {
            new() { ClassId = a.Id, SubjectId = math.Id, TeacherId = ta.Id, HoursPerWeek = 2 },
            new() { ClassId = b.Id, SubjectId = math.Id, TeacherId = tb.Id, HoursPerWeek = 2 },
        };
        var common = new CommonLesson
        {
            Enabled = true, DayIndex = day, SlotIndex = slot,
            GradesCsv = gradesCsv, UseOwnRooms = useOwn
        };
        var input = new ProblemInput([a, b], [ta, tb], [math], cur,
            [], [], [], DaysCount: 5, SlotsPerDay: 5, commonLesson: common,
            flex: FlexSettings.Default);
        return (input, a, b);
    }

    // R3: включён — общий SyncGroup, одна клетка, validator чист.
    [Fact]
    public void R3_Enabled_SameCellClean()
    {
        var (input, a, b) = CommonInput();
        var p = Build(input);
        var commons = p.Occurrences.Where(o => p.Subjects[o.SubjectId].Name == "Классный час").ToList();
        Assert.Equal(2, commons.Count);
        Assert.Single(commons.Select(o => o.SyncGroupId).Distinct());
        Assert.Equal(a.ClassTeacherId, commons.Single(o => o.ClassId == a.Id).TeacherId);
        Assert.Equal(b.ClassTeacherId, commons.Single(o => o.ClassId == b.Id).TeacherId);
        var placed = GreedyPlacer.Place(p);
        Assert.Empty(placed.Unplaced);
        var cells = commons.Select(o => placed.Placed[o.Id]).ToList();
        Assert.Equal((0, 1), (cells[0].Day, cells[0].Slot));
        Assert.Equal(cells[0].Day, cells[1].Day);
        Assert.Equal(cells[0].Slot, cells[1].Slot);
        // Детерминированно компактная раскладка тех же часов — validator чист.
        var byClass = commons.ToDictionary(o => o.ClassId);
        var mathOf = p.Occurrences
            .Where(o => p.Subjects[o.SubjectId].Name == "Мат")
            .GroupBy(o => o.ClassId)
            .ToDictionary(g => g.Key, g => g.OrderBy(o => o.Id).ToList());
        var lessons = new List<PlacedLesson>();
        foreach (var cls in new[] { a, b })
        {
            lessons.Add(new PlacedLesson
            {
                OccurrenceId = byClass[cls.Id].Id, DayIndex = 0, SlotIndex = 1
            });
            lessons.Add(new PlacedLesson
            {
                OccurrenceId = mathOf[cls.Id][0].Id, DayIndex = 0, SlotIndex = 2
            });
            lessons.Add(new PlacedLesson
            {
                OccurrenceId = mathOf[cls.Id][1].Id, DayIndex = 0, SlotIndex = 3
            });
        }
        Assert.True(PlacementValidator.Validate(p, lessons).IsValid);
    }

    [Fact]
    public void R3_MissingClassTeacher_Error()
    {
        var (input, _, _) = CommonInput();
        var nocls = new SchoolClass { AcademicYearId = Year, Name = "5А", Grade = 5, StudentCount = 25 };
        var t = new Teacher { Name = "T", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Мат", MaxPerDay = 2 };
        var bad = new ProblemInput([nocls], [t], [math],
            [new CurriculumItem { ClassId = nocls.Id, SubjectId = math.Id, TeacherId = t.Id, HoursPerWeek = 1 }],
            [], [], [], DaysCount: 5, SlotsPerDay: 5,
            commonLesson: new CommonLesson { Enabled = true, GradesCsv = "5" },
            flex: FlexSettings.Default);
        var (p, e) = ProblemBuilder.Build(bad);
        Assert.Null(p);
        Assert.Contains(e, x => x.Contains("классного руководителя"));
    }

    [Fact]
    public void R3_DuplicateClassTeacher_Error()
    {
        var year = Guid.NewGuid();
        var a = new SchoolClass { AcademicYearId = year, Name = "5А", Grade = 5, StudentCount = 25 };
        var b = new SchoolClass { AcademicYearId = year, Name = "6А", Grade = 6, StudentCount = 25 };
        var t = new Teacher { Name = "Одна", MaxLessonsPerDay = 6 };
        a.ClassTeacherId = t.Id;
        b.ClassTeacherId = t.Id;
        var math = new Subject { Name = "Мат", MaxPerDay = 2 };
        var input = new ProblemInput([a, b], [t], [math],
            [new CurriculumItem { ClassId = a.Id, SubjectId = math.Id, TeacherId = t.Id, HoursPerWeek = 1 },
             new CurriculumItem { ClassId = b.Id, SubjectId = math.Id, TeacherId = t.Id, HoursPerWeek = 1 }],
            [], [], [], DaysCount: 5, SlotsPerDay: 5,
            commonLesson: new CommonLesson { Enabled = true, GradesCsv = "5,6" },
            flex: FlexSettings.Default);
        var (p, e) = ProblemBuilder.Build(input);
        Assert.Null(p);
        Assert.Contains(e, x => x.Contains("двух классах"));
    }

    [Fact]
    public void R3_HallWithoutChoice_Error()
    {
        var (input, _, _) = CommonInput(useOwn: false);
        var (p, e) = ProblemBuilder.Build(input);
        Assert.Null(p);
        Assert.Contains(e, x => x.Contains("P3"));
    }

    // R3: LS не двигает общий урок (sync frozen).
    [Fact]
    public void R3_LS_Frozen()
    {
        var (input, _, _) = CommonInput();
        var p = Build(input);
        var placed = GreedyPlacer.Place(p);
        Assert.Empty(placed.Unplaced);
        var commons = p.Occurrences.Where(o => p.Subjects[o.SubjectId].Name == "Классный час").ToList();
        var index = SearchIndex.Build(p, placed.Placed.Select(kv => new PlacedLesson
        {
            OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
            SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
        }).ToList());
        var first = commons[0];
        // Вне единственной клетки домена — двигаться некуда (sync frozen + домен).
        var (allowed, _, hard) = index.TryMove(new CandidateMove(first.Id, 1, 1, null));
        Assert.False(allowed);
        Assert.NotEmpty(hard);
    }

    // R5: сплит одним классом в flag=false кабинете cap 1 — валидно; флаг true — overflow.
    [Fact]
    public void R5_KeyAware_Validator()
    {
        (SchedulingProblem p, LessonOccurrence a, LessonOccurrence b) Setup(bool flag)
        {
            var year = Guid.NewGuid();
            var cls = new SchoolClass { AcademicYearId = year, Name = "5А", Grade = 5, StudentCount = 25 };
            var ta = new Teacher { Name = "A", MaxLessonsPerDay = 6 };
            var tb = new Teacher { Name = "B", MaxLessonsPerDay = 6 };
            var subj = new Subject { Name = "Яз", MaxPerDay = 2 };
            var gA = new StudentGroup { ClassId = cls.Id, Name = "A" };
            var gB = new StudentGroup { ClassId = cls.Id, Name = "B" };
            var gym = new Room
            {
                Name = "Спортзал", PhysicalCapacity = 30,
                MaxSimultaneousGroups = 1, CountSubgroupAsGroup = flag
            };
            var item = new CurriculumItem
            {
                ClassId = cls.Id, SubjectId = subj.Id, TeacherId = ta.Id,
                HoursPerWeek = 1, SplitSubgroups = true
            };
            var input = new ProblemInput([cls], [ta, tb], [subj], [item],
                [gA, gB], [], [], DaysCount: 2, SlotsPerDay: 3,
                SplitTeachers: new Dictionary<Guid, (Guid, Guid)> { [item.Id] = (ta.Id, tb.Id) },
                rooms: [gym]);
            var prob = Build(input);
            var halves = prob.Occurrences.OrderBy(o => o.TeacherId).ToList();
            return (prob, halves[0], halves[1]);
        }
        var (pFalse, fa, fb) = Setup(false);
        var bothFalse = new List<PlacedLesson>
        {
            new() { OccurrenceId = fa.Id, DayIndex = 0, SlotIndex = 1, RoomId = pFalse.Rooms.Values.Single().Id },
            new() { OccurrenceId = fb.Id, DayIndex = 0, SlotIndex = 1, RoomId = pFalse.Rooms.Values.Single().Id },
        };
        Assert.True(PlacementValidator.Validate(pFalse, bothFalse).IsValid);
        var (pTrue, ta2, tb2) = Setup(true);
        var bothTrue = new List<PlacedLesson>
        {
            new() { OccurrenceId = ta2.Id, DayIndex = 0, SlotIndex = 1, RoomId = pTrue.Rooms.Values.Single().Id },
            new() { OccurrenceId = tb2.Id, DayIndex = 0, SlotIndex = 1, RoomId = pTrue.Rooms.Values.Single().Id },
        };
        Assert.Contains(PlacementValidator.Validate(pTrue, bothTrue).HardViolations,
            v => v.Code == PhysicalRuleCodes.RoomOverflow);
    }

    // R5: теснота сверх «желательно» — soft room-crowding.
    [Fact]
    public void R5_Crowding_Soft()
    {
        var year = Guid.NewGuid();
        var classes = new[] { "5А", "5Б", "5В" }.Select(n => new SchoolClass
        {
            AcademicYearId = year, Name = n, Grade = 5, StudentCount = 20
        }).ToList();
        var teachers = new[] { "T1", "T2", "T3" }.Select(n => new Teacher
        {
            Name = n, MaxLessonsPerDay = 6
        }).ToList();
        var pe = new Subject { Name = "Физ", MaxPerDay = 2 };
        var gym = new Room
        {
            Name = "Спортзал", PhysicalCapacity = 30,
            MaxSimultaneousGroups = 4, DesiredGroups = 2
        };
        var cur = classes.Zip(teachers).Select(x => new CurriculumItem
        {
            ClassId = x.First.Id, SubjectId = pe.Id, TeacherId = x.Second.Id, HoursPerWeek = 1
        }).ToList();
        var input = new ProblemInput(classes, teachers, [pe], cur,
            [], [], [], DaysCount: 2, SlotsPerDay: 3, rooms: [gym]);
        var p = Build(input);
        var placed = p.Occurrences.Select(o => new PlacedLesson
        {
            OccurrenceId = o.Id, DayIndex = 0, SlotIndex = 1, RoomId = gym.Id
        }).ToList();
        Assert.True(PlacementValidator.Validate(p, placed).IsValid);
        var bd = SoftEvaluator.Evaluate(p, placed);
        Assert.Equal(RuleCatalog.RoomCrowding, bd.Components.Single(c => c.Code == "room-crowding").Value);
    }

    // R5: «желательно» не задано → претензий нет.
    [Fact]
    public void R5_DesiredUnset_NoPressure()
    {
        var (cls, t, s) = Trio();
        var room = new Room { Name = "Каб", PhysicalCapacity = 30, MaxSimultaneousGroups = 2 };
        var p = Build(TinyInput(cls, t, s, rooms: [room]));
        var occ = Assert.Single(p.Occurrences);
        var placed = new List<PlacedLesson>
        {
            new() { OccurrenceId = occ.Id, DayIndex = 0, SlotIndex = 1, RoomId = room.Id }
        };
        var bd = SoftEvaluator.Evaluate(p, placed);
        Assert.Equal(0, bd.Components.Single(c => c.Code == "room-crowding").Value);
        Assert.Equal(2, RoomPolicy.EffectiveDesired(room));
    }

    // R6: тяжёлый на краю дня; порог настраивается.
    [Fact]
    public void R6_HeavyEdge_UnitsAndThreshold()
    {
        (SchedulingProblem p, PlacedLesson[] placed) Setup(int difficulty, FlexSettings flex)
        {
            var (cls, t, math) = Trio();
            math.Difficulty = difficulty;
            var lit = new Subject { Name = "Лит", MaxPerDay = 2 };
            var cur = new List<CurriculumItem>
            {
                new() { ClassId = cls.Id, SubjectId = math.Id, TeacherId = t.Id, HoursPerWeek = 1 },
                new() { ClassId = cls.Id, SubjectId = lit.Id, TeacherId = t.Id, HoursPerWeek = 2 },
            };
            var input = new ProblemInput([cls], [t], [math, lit], cur,
                [], [], [], DaysCount: 2, SlotsPerDay: 4, flex: flex);
            var prob = Build(input);
            var om = prob.Occurrences.Where(o => o.SubjectId == math.Id).ToList();
            var ol = prob.Occurrences.Where(o => o.SubjectId == lit.Id).OrderBy(o => o.Id).ToList();
            var pl = new List<PlacedLesson>
            {
                new() { OccurrenceId = om[0].Id, DayIndex = 0, SlotIndex = 1 },
                new() { OccurrenceId = ol[0].Id, DayIndex = 0, SlotIndex = 2 },
                new() { OccurrenceId = ol[1].Id, DayIndex = 0, SlotIndex = 3 },
            };
            return (prob, pl.ToArray());
        }
        var (p, placed) = Setup(8, FlexSettings.Neutral);
        var bd = SoftEvaluator.Evaluate(p, placed);
        Assert.Equal(RuleCatalog.HeavyEdge, bd.Components.Single(c => c.Code == "heavy-edge").Value);
        // Порог 9: математика-8 уже не тяжёлая.
        var custom = new FlexSettings
        {
            GradePriorityEnabled = false, Grade11Weight = 1, Grade9Weight = 1,
            GradeOtherWeight = 1, IsHeavyThreshold = 9, AssignMode = TeacherAssignMode.Off
        };
        var (p2, placed2) = Setup(8, custom);
        var bd2 = SoftEvaluator.Evaluate(p2, placed2);
        Assert.Equal(0, bd2.Components.Single(c => c.Code == "heavy-edge").Value);
    }

    // R6: норма сложности через импорт.
    [Fact]
    public void R6_ImporterNorm_AppliesAndValidates()
    {
        var rows = new List<LoadRow> { new("5А", "Математика", 3, "Иванова", false, null, null) };
        var flex = FlexDataset.Empty with
        {
            SubjectDifficulty = new List<SubjectDifficultyRow> { new("Математика", 9) }
        };
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows, flex: flex);
        Assert.Equal(9, data.Subjects.Single().Difficulty);
        var bad = FlexDataset.Empty with
        {
            SubjectDifficulty = new List<SubjectDifficultyRow> { new("Математика", 11) }
        };
        Assert.Throws<InvalidOperationException>(() =>
            SchoolDataImporter.Import(Guid.NewGuid(), rows, flex: bad));
    }

    // R7: HardClass ловит двух учителей; Neutral молчит; сплиты освобождены.
    [Fact]
    public void R7_Validator_Modes()
    {
        (SchedulingProblem p, PlacedLesson[] placed) Setup(TeacherAssignMode mode)
        {
            var year = Guid.NewGuid();
            var cls = new SchoolClass { AcademicYearId = year, Name = "5А", Grade = 5, StudentCount = 25 };
            var t1 = new Teacher { Name = "T1", MaxLessonsPerDay = 6 };
            var t2 = new Teacher { Name = "T2", MaxLessonsPerDay = 6 };
            var math = new Subject { Name = "Мат", MaxPerDay = 2 };
            var prob = new SchedulingProblem
            {
                Classes = new() { [cls.Id] = cls },
                Teachers = new() { [t1.Id] = t1, [t2.Id] = t2 },
                Subjects = new() { [math.Id] = math },
                Flex = new FlexSettings
                {
                    GradePriorityEnabled = false, Grade11Weight = 1, Grade9Weight = 1,
                    GradeOtherWeight = 1, IsHeavyThreshold = 7, AssignMode = mode
                },
            };
            var o1 = new LessonOccurrence
            {
                CurriculumItemId = Guid.NewGuid(), ClassId = cls.Id,
                SubjectId = math.Id, TeacherId = t1.Id, StableKey = "k1"
            };
            var o2 = new LessonOccurrence
            {
                CurriculumItemId = Guid.NewGuid(), ClassId = cls.Id,
                SubjectId = math.Id, TeacherId = t2.Id, StableKey = "k2"
            };
            prob.Occurrences.Add(o1);
            prob.Occurrences.Add(o2);
            var pl = new[]
            {
                new PlacedLesson { OccurrenceId = o1.Id, DayIndex = 0, SlotIndex = 1 },
                new PlacedLesson { OccurrenceId = o2.Id, DayIndex = 1, SlotIndex = 1 },
            };
            return (prob, pl);
        }
        var (pHard, plHard) = Setup(TeacherAssignMode.HardClass);
        Assert.Contains(PlacementValidator.Validate(pHard, plHard).HardViolations,
            v => v.Code == "teacher-assign");
        var (pOff, plOff) = Setup(TeacherAssignMode.Off);
        Assert.DoesNotContain(PlacementValidator.Validate(pOff, plOff).HardViolations,
            v => v.Code == "teacher-assign");
    }

    // R7: Soft — строка в breakdown, Off — ноль.
    [Fact]
    public void R7_Soft_Reports()
    {
        (SchedulingProblem p, PlacedLesson[] placed) Setup(TeacherAssignMode mode)
        {
            var year = Guid.NewGuid();
            var cls = new SchoolClass { AcademicYearId = year, Name = "5А", Grade = 5, StudentCount = 25 };
            var t1 = new Teacher { Name = "T1", MaxLessonsPerDay = 6 };
            var t2 = new Teacher { Name = "T2", MaxLessonsPerDay = 6 };
            var math = new Subject { Name = "Мат", MaxPerDay = 2 };
            var prob = new SchedulingProblem
            {
                Classes = new() { [cls.Id] = cls },
                Teachers = new() { [t1.Id] = t1, [t2.Id] = t2 },
                Subjects = new() { [math.Id] = math },
                Flex = new FlexSettings
                {
                    GradePriorityEnabled = false, Grade11Weight = 1, Grade9Weight = 1,
                    GradeOtherWeight = 1, IsHeavyThreshold = 7, AssignMode = mode
                },
            };
            var o1 = new LessonOccurrence
            {
                CurriculumItemId = Guid.NewGuid(), ClassId = cls.Id,
                SubjectId = math.Id, TeacherId = t1.Id, StableKey = "k1"
            };
            var o2 = new LessonOccurrence
            {
                CurriculumItemId = Guid.NewGuid(), ClassId = cls.Id,
                SubjectId = math.Id, TeacherId = t2.Id, StableKey = "k2"
            };
            prob.Occurrences.Add(o1);
            prob.Occurrences.Add(o2);
            return (prob, new[]
            {
                new PlacedLesson { OccurrenceId = o1.Id, DayIndex = 0, SlotIndex = 1 },
                new PlacedLesson { OccurrenceId = o2.Id, DayIndex = 1, SlotIndex = 1 },
            });
        }
        var (pSoft, plSoft) = Setup(TeacherAssignMode.Soft);
        Assert.Equal(RuleCatalog.TeacherSplit,
            SoftEvaluator.Evaluate(pSoft, plSoft).Components.Single(c => c.Code == "teacher-split").Value);
        var (pOff, plOff) = Setup(TeacherAssignMode.Off);
        Assert.Equal(0,
            SoftEvaluator.Evaluate(pOff, plOff).Components.Single(c => c.Code == "teacher-split").Value);
    }

    // R8: окно 11-го класса весит ×3, 5-го — ×1; Neutral всех уравнивает.
    [Fact]
    public void R8_GradeWeights()
    {
        (SchedulingProblem p, PlacedLesson[] placed) Setup(int grade, FlexSettings flex)
        {
            var (cls, t, s) = Trio(cls: "11А", grade: grade);
            var lit = new Subject { Name = "Лит", MaxPerDay = 2 };
            var cur = new List<CurriculumItem>
            {
                new() { ClassId = cls.Id, SubjectId = s.Id, TeacherId = t.Id, HoursPerWeek = 1 },
                new() { ClassId = cls.Id, SubjectId = lit.Id, TeacherId = t.Id, HoursPerWeek = 1 },
            };
            var input = new ProblemInput([cls], [t], [s, lit], cur,
                [], [], [], DaysCount: 2, SlotsPerDay: 4, flex: flex);
            var prob = Build(input);
            var om = prob.Occurrences.Single(o => o.SubjectId == s.Id);
            var ol = prob.Occurrences.Single(o => o.SubjectId == lit.Id);
            return (prob, new[]
            {
                new PlacedLesson { OccurrenceId = om.Id, DayIndex = 0, SlotIndex = 1 },
                new PlacedLesson { OccurrenceId = ol.Id, DayIndex = 0, SlotIndex = 3 },
            });
        }
        var (p11, pl11) = Setup(11, FlexSettings.Default);
        Assert.Equal(3 * RuleCatalog.StudentGap,
            SoftEvaluator.Evaluate(p11, pl11).Components.Single(c => c.Code == "student-gap").Value);
        var (p5, pl5) = Setup(5, FlexSettings.Default);
        Assert.Equal(RuleCatalog.StudentGap,
            SoftEvaluator.Evaluate(p5, pl5).Components.Single(c => c.Code == "student-gap").Value);
        var (pN, plN) = Setup(11, FlexSettings.Neutral);
        Assert.Equal(RuleCatalog.StudentGap,
            SoftEvaluator.Evaluate(pN, plN).Components.Single(c => c.Code == "student-gap").Value);
    }

    // Паритет индекса с flex (веса параллелей + теснота + тяжёлые): ходы со сменой
    // времени. Индекс намеренно мягче FullValidator ровно на student-gap/late-start
    // (soft-travel движка, D-28): allowed ⟺ нет прочих hard; дельта == полному пересчёту.
    [Fact]
    public void Parity_WithFlex()
    {
        var year = Guid.NewGuid();
        var c11 = new SchoolClass { AcademicYearId = year, Name = "11А", Grade = 11, StudentCount = 20 };
        var c5 = new SchoolClass { AcademicYearId = year, Name = "5А", Grade = 5, StudentCount = 20 };
        var t1 = new Teacher { Name = "T1", MaxLessonsPerDay = 6 };
        var t2 = new Teacher { Name = "T2", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Мат", MaxPerDay = 2, Difficulty = 8 };
        var lit = new Subject { Name = "Лит", MaxPerDay = 2 };
        var gym = new Room { Name = "Спортзал", PhysicalCapacity = 30, MaxSimultaneousGroups = 3, DesiredGroups = 1 };
        var cab = new Room { Name = "Каб", PhysicalCapacity = 30 };
        var cur = new List<CurriculumItem>
        {
            new() { ClassId = c11.Id, SubjectId = math.Id, TeacherId = t1.Id, HoursPerWeek = 3 },
            new() { ClassId = c11.Id, SubjectId = lit.Id, TeacherId = t2.Id, HoursPerWeek = 3 },
            new() { ClassId = c5.Id, SubjectId = math.Id, TeacherId = t2.Id, HoursPerWeek = 3 },
            new() { ClassId = c5.Id, SubjectId = lit.Id, TeacherId = t1.Id, HoursPerWeek = 3 },
        };
        var input = new ProblemInput([c11, c5], [t1, t2], [math, lit], cur,
            [], [], [], DaysCount: 4, SlotsPerDay: 4,
            rooms: [gym, cab], flex: FlexSettings.Default);
        var p = Build(input);
        var start = GreedyPlacer.Place(p);
        Assert.Empty(start.Unplaced);
        var current = start.Placed.Select(kv => new PlacedLesson
        {
            OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
            SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
        }).ToList();
        var index = SearchIndex.Build(p, current);
        var rng = new Random(21);
        var roomIds = p.Rooms.Values.Select(r => (Guid?)r.Id).Concat([(Guid?)null]).ToList();
        long fullBefore = SoftEvaluator.Evaluate(p, current).Total;
        for (int i = 0; i < 150; i++)
        {
            var target = current[rng.Next(current.Count)];
            var move = new CandidateMove(target.OccurrenceId,
                rng.Next(4), 1 + rng.Next(4), roomIds[rng.Next(roomIds.Count)]);
            var (allowed, delta, _) = index.TryMove(move);
            var hypo = current
                .Where(x => x.OccurrenceId != move.OccurrenceId)
                .Concat([new PlacedLesson
                {
                    OccurrenceId = move.OccurrenceId, DayIndex = move.DayIndex,
                    SlotIndex = move.SlotIndex, RoomId = move.RoomId
                }]).ToList();
            long fullAfter = SoftEvaluator.Evaluate(p, hypo).Total;
            bool nonGapHard = PlacementValidator.Validate(p, hypo).HardViolations
                .Any(v => v.Code != "student-gap" && v.Code != "student-late-start");
            Assert.Equal(!nonGapHard, allowed);
            // Контракт дельты (как у IncrementalEvaluator): при hard — 0,
            // иначе — полному пересчёту.
            Assert.Equal(allowed ? fullAfter - fullBefore : 0, delta);
        }
    }

    // Каталог v6 (D-50, осознанно): v5-коды + teacher-active-day (цена занятого
    // учителе-дня, дефолт 0 = поведение не меняется). Дефолты v5 НЕ меняются;
    // новое — только код + WeightRange 0..100 (настройка через CUSTOM/expert).
    [Fact]
    public void CatalogV4_Codes()
    {
        Assert.Equal(6, RuleCatalog.Version);
        Assert.Contains("room-crowding", RuleCatalog.AllCodes);
        Assert.Contains("teacher-split", RuleCatalog.AllCodes);
        Assert.Contains("teacher-active-day", RuleCatalog.AllCodes);
        Assert.Equal(0, RuleCatalog.DefaultWeight("teacher-active-day"));
        Assert.Equal((0, 100), RuleCatalog.WeightRange("teacher-active-day"));
        Assert.Equal(8, RuleCatalog.DefaultWeight("room-crowding"));
        Assert.Equal(25, RuleCatalog.DefaultWeight("teacher-split"));
        Assert.Equal((0, 50), RuleCatalog.WeightRange("room-crowding"));
        Assert.Equal((0, 100), RuleCatalog.WeightRange("teacher-split"));
        var rs = RuleResolver.Resolve("STANDARD");
        Assert.Equal(6, rs.CatalogVersion);
        Assert.Equal(8, rs.Weight("room-crowding"));
        // B2: дефолтные цифры строгого те же (student 100/100), новое — механизм override.
        Assert.Equal(100, RuleCatalog.DefaultWeight("student-gap"));
        Assert.Equal(100, RuleCatalog.DefaultWeight("student-late-start"));
        Assert.Contains("student-gap", RuleCatalog.DangerousCodes);
        Assert.Contains("teacher-maxperday", RuleCatalog.DangerousCodes);
        Assert.Equal((0, 100), RuleCatalog.OverrideRange("student-gap"));
        Assert.Equal((0, 100), RuleCatalog.OverrideRange("teacher-maxperday"));
    }

    // Проводка ToProblemInput: common + назначения + настройки.
    [Fact]
    public void ToProblemInput_WiresFlex()
    {
        var rows = new List<LoadRow>
        {
            new("5А", "Математика", 3, "Иванова", false, null, null),
            new("6А", "Математика", 3, "Петрова", false, null, null),
        };
        var flex = FlexDataset.Empty with
        {
            Classes = new List<ClassConfigRow>
            {
                new("5А", "Иванова", 5), new("6А", "Петрова", 6)
            },
            CommonLesson = new CommonLessonRow(true, 3, 1, "5,6", true),
            Assignments = new List<TeacherAssignRow>
            {
                new("Иванова", "Математика", AssignmentScope.Class, "5А", null)
            },
            Settings = new FlexSettingsRow(true, 3, 2, 1, 7, TeacherAssignMode.HardClass),
        };
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows, flex: flex);
        var input = data.ToProblemInput();
        Assert.NotNull(input.CommonLesson);
        Assert.True(input.CommonLesson!.Enabled);
        Assert.Single(input.Assignments);
        Assert.Equal(AssignmentScope.Class, input.Assignments[0].Scope);
        Assert.Equal(3, input.Flex.Grade11Weight);
        Assert.Equal(TeacherAssignMode.HardClass, input.Flex.AssignMode);
        var p = Build(input);
        Assert.Single(p.Assignments);
        Assert.Equal(2, p.Occurrences.Count(o => p.Subjects[o.SubjectId].Name == "Классный час"));
    }
}
