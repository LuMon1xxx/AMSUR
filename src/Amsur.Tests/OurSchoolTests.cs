using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;
using Amsur.Scheduling.OrTools;

namespace Amsur.Tests;

// Наша школа (5–11, 27 классов): 5,6,7 по 5; 8,9 по 4; 10,11 по 2
// (А — профиль ХимБио/АнглОбщ, Б — физмат). 6–7 во 2-й смене, остальные
// в 1-й. Пятидневка. Началки нет. Классный час — четверг первым уроком
// своей смены (слот 1 / слот 8), дома, с классным руководителем.
// Источник: опрос ученика 15.09.2026. Допущения помечены ASSUME.
public sealed class OurSchoolTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private sealed record SubjHours(string Name, int Hours);

    private const string Foreign = "Иностранный язык";
    private const string Pe = "Физическая культура и здоровье";
    private const string Labor = "Трудовое обучение";
    private const string Informatics = "Информатика";
    private const string Chem = "Химия";

    // План — нормы РБ (см. SUBJECTS_RB.md); отклонения: ОБЖ нет (школа не ведёт),
    // ИЗО — факультатив (в сетку не входит), музыка только 5–6, физмат 10Б/11Б — ин.яз 2ч.
    private static readonly Dictionary<int, SubjHours[]> Plan = new()
    {
        [5] = [new("Математика", 5), new("Русский язык", 3), new("Русская литература", 2), new("Белорусский язык", 2), new("Белорусская литература", 2), new(Foreign, 3), new("История", 2), new("География", 2), new("Биология", 1), new(Informatics, 1), new("Музыка", 1), new(Pe, 2), new(Labor, 2)],
        [6] = [new("Математика", 5), new("Русский язык", 3), new("Русская литература", 2), new("Белорусский язык", 2), new("Белорусская литература", 2), new(Foreign, 3), new("История", 2), new("География", 2), new("Биология", 2), new(Informatics, 1), new("Музыка", 1), new(Pe, 2), new(Labor, 2)],
        [7] = [new("Алгебра", 4), new("Геометрия", 2), new("Русский язык", 2), new("Русская литература", 2), new("Белорусский язык", 2), new("Белорусская литература", 2), new(Foreign, 3), new("История", 2), new("География", 2), new("Биология", 2), new("Физика", 2), new(Informatics, 2), new(Pe, 3), new(Labor, 2)],
        [8] = [new("Алгебра", 4), new("Геометрия", 2), new("Русский язык", 2), new("Русская литература", 2), new("Белорусский язык", 2), new("Белорусская литература", 1), new(Foreign, 3), new("История", 2), new("География", 2), new("Биология", 2), new("Физика", 3), new(Chem, 2), new(Informatics, 1), new(Pe, 3), new(Labor, 1)],
        [9] = [new("Алгебра", 4), new("Геометрия", 2), new("Русский язык", 2), new("Русская литература", 2), new("Белорусский язык", 2), new("Белорусская литература", 1), new(Foreign, 3), new("История", 2), new("География", 2), new("Биология", 2), new("Физика", 3), new(Chem, 2), new(Informatics, 1), new(Pe, 3), new(Labor, 1)],
    };

    // 10–11: Б — физмат (ин.яз 2ч), А — профиль (целые 2ч ин.яз + пара ХимБио/АнглОбщ).
    // ASSUME-ужатия под слоты (школа уточнит): 6-е — физра 2 (не 3);
    // профиль А — физра 1 + общество 1; 11Б — физра 1. Иначе класс не влезает
    // в недельную сетку (SanPin-кэп distinct-слотов) — доказано greedy-зондом.
    private static SubjHours[] Plan10_11(int grade, bool profile)
    {
        int pe = profile ? 1 : (grade == 11 ? 1 : 2);
        int soc = profile ? 1 : 2;
        return [new("Алгебра", 3), new("Геометрия", 2), new("Русский язык", 2), new("Русская литература", 3), new("Белорусский язык", 2), new("Белорусская литература", 1), new(Foreign, 2), new("История", 2), new("География", grade == 10 ? 2 : 1), new("Биология", 2), new("Физика", 3), new(Chem, 2), new("Обществоведение", soc), new(Informatics, 2), new(Pe, pe), new("Допризывная подготовка", 0), new("Медицинская подготовка", 0),
            .. (grade == 11 ? new SubjHours[] { new("Астрономия", 1) } : [])];
    }

    // Смены: 6,7 целиком + 8Г,9Г (ASSUME-буквы: 1 класс из 8-х и 1 из 9-х — ученик уточнит).
    private static bool IsSecondShiftOur(SchoolClass cls) =>
        cls.Grade is 6 or 7 || cls.Name is "8Г" or "9Г";

    private static int LettersCount(int grade) => grade <= 7 ? 5 : grade <= 9 ? 4 : 2;

    private static readonly string[] Letters = ["А", "Б", "В", "Г", "Д"];

    private static bool IsProfile(int grade, int li) => grade >= 10 && li == 0; // 10А/11А

    // Деление stated by school: ин.яз везде кроме 5-х (ASSUME: иначе 162ч > 150 слотов),
    // информатика 8–11, физра М/Д 10–11, труды М/Д 5–9.
    private static bool IsSameSubjectSplit(int grade, int li, string subj) =>
        (subj == Foreign && grade >= 6) ||
        (subj == Informatics && grade >= 8) ||
        (subj == Pe && grade >= 10) ||
        (subj == Labor && grade is >= 5 and <= 9);

    public static (SchedulingProblem? Problem, IReadOnlyList<string> Errors) TryBuildOurSchool(
        double budget = 8, int? seed = 11, bool allowOverload = false, int overloadCap = 9,
        bool classDayCap8 = false)
    {
        var year = Guid.NewGuid();
        var shift1 = Guid.NewGuid();
        var shift2 = Guid.NewGuid();
        var classes = new List<SchoolClass>();
        foreach (var grade in Enumerable.Range(5, 7))
            for (int li = 0; li < LettersCount(grade); li++)
            {
                var nm = $"{grade}{Letters[li]}";
                classes.Add(new SchoolClass
                {
                    AcademicYearId = year, Name = nm,
                    Grade = grade, StudentCount = 25,
                    ShiftId = (grade is 6 or 7 || nm is "8Г" or "9Г") ? shift2 : shift1,
                    // Cap8 (D-42): дневная норма классов 8. Школа подтверждает:
                    // 8-урочные дни бывают (пиковые), хоть СанПиН-норма 6/7.
                    MaxLessonsPerDay = classDayCap8 ? 8 : (grade <= 6 ? 6 : 7),
                });
            }

        var classSlots = classes.ToDictionary(
            c => c.Id,
            c => (IReadOnlyList<int>)(IsSecondShiftOur(c)
                ? Enumerable.Range(8, 7).ToList()
                : Enumerable.Range(1, 7).ToList()));

        string[] surnames =
        [
            "Ковалёв", "Мороз", "Козлов", "Новикова", "Соколова", "Михайлова", "Фёдорова",
            "Волкова", "Смирнова", "Кузнецова", "Попова", "Орлова", "Семёнова", "Егорова",
            "Павлова", "Громова", "Лебедева", "Сорокина", "Дроздова", "Кузьмина",
            "Иванова", "Петрова", "Сидорова", "Кравцова", "Мельникова", "Шевченко",
            "Бондаренко", "Климова", "Савицкая", "Герасимова", "Макарова", "Захарова",
            "Тихонова", "Андреева", "Григорьева", "Дмитриева", "Жукова", "Зайцева",
        ];
        string[] initials = ["А.В.", "М.С.", "Д.И.", "Е.П.", "О.Н.", "И.К.", "Т.В.", "Н.А."];
        int nameIdx = 0;
        Teacher NewTeacher(int maxPerDay = 6)
        {
            var t = new Teacher
            {
                Name = $"{surnames[nameIdx % surnames.Length]} {initials[(nameIdx / surnames.Length) % initials.Length]}",
                MaxLessonsPerDay = maxPerDay
            };
            nameIdx++;
            return t;
        }

        // Штат stated by school (русский блок 3 — реальная нехватка, см. тест покрытия).
        var foreign = Enumerable.Range(0, 5).Select(_ => NewTeacher()).ToList();
        var mathJunior = Enumerable.Range(0, 3).Select(_ => NewTeacher()).ToList();
        var mathSenior = Enumerable.Range(0, 3).Select(_ => NewTeacher()).ToList();
        var russian = Enumerable.Range(0, 3).Select(_ => NewTeacher()).ToList();
        var belarus = Enumerable.Range(0, 4).Select(_ => NewTeacher()).ToList();
        var history = Enumerable.Range(0, 3).Select(_ => NewTeacher()).ToList();
        var geo = Enumerable.Range(0, 2).Select(_ => NewTeacher()).ToList();
        var chem1 = NewTeacher(); var bio1 = NewTeacher(); var bioChem = NewTeacher();
        var phys = Enumerable.Range(0, 2).Select(_ => NewTeacher()).ToList();
        var inf = Enumerable.Range(0, 2).Select(_ => NewTeacher()).ToList();
        var pe = Enumerable.Range(0, 3).Select(_ => NewTeacher()).ToList();
        var laborBoys = Enumerable.Range(0, 2).Select(_ => NewTeacher()).ToList();
        var laborGirls = Enumerable.Range(0, 2).Select(_ => NewTeacher()).ToList();
        var music = NewTeacher();
        var dpm = NewTeacher(); var med = NewTeacher();
        var allPool = foreign.Concat(mathJunior).Concat(mathSenior).Concat(russian)
            .Concat(belarus).Concat(history).Concat(geo)
            .Concat([chem1, bio1, bioChem]).Concat(phys).Concat(inf).Concat(pe)
            .Concat(laborBoys).Concat(laborGirls).Concat([music, dpm, med]).ToList();

        List<Teacher> PoolOf(string subj, int grade) => subj switch
        {
            Foreign => foreign,
            "Математика" => mathJunior,
            "Алгебра" or "Геометрия" => mathSenior,
            "Русский язык" or "Русская литература" => russian,
            "Белорусский язык" or "Белорусская литература" => belarus,
            "История" or "Обществоведение" => history,
            "География" => geo,
            Chem => [chem1, bioChem],
            "Биология" => [bio1, bioChem],
            "Физика" or "Астрономия" => phys,
            Informatics => inf,
            Pe => pe,
            Labor => laborBoys.Concat(laborGirls).ToList(), // деление ниже — по полам явно
            "Музыка" => [music],
            "Допризывная подготовка" => [dpm],
            "Медицинская подготовка" => [med],
            _ => throw new InvalidOperationException($"Нет пула: {subj}"),
        };

        var subjNames = Plan.Values.SelectMany(x => x).Select(s => s.Name)
            .Concat(["Обществоведение", "Допризывная подготовка", "Медицинская подготовка", "Астрономия"])
            .Distinct().ToList();
        var subjects = subjNames.Select(n => new Subject { Name = n, MaxPerDay = 2 }).ToList();
        var subjByName = subjects.ToDictionary(s => s.Name);
        var teachIdx = subjNames.Concat(["Труды-М", "Труды-Д"]).ToDictionary(k => k, _ => 0);
        // Нагрузка учителя суммарно по всем пулам (bioChem — в двух!): как у завуча,
        // новый предмет отдаём наименее загруженному (иначе round-robin по предметам
        // даёт одному 36ч, другому 12ч — артефакт фикстуры, а не школы).
        var teacherLoad = allPool.ToDictionary(t => t.Id, _ => 0);
        Teacher NextOf(string subj, int grade, int hours, Guid? excludeId = null)
        {
            var pool = PoolOf(subj, grade);
            var cands = excludeId.HasValue ? pool.Where(t => t.Id != excludeId.Value).ToList() : pool;
            var t = cands.MinBy(t => teacherLoad[t.Id])!;
            teacherLoad[t.Id] += hours;
            teachIdx[subj]++;
            return t;
        }

        var curriculum = new List<CurriculumItem>();
        var groups = new List<StudentGroup>();
        var splitTeachers = new Dictionary<Guid, (Guid, Guid)>();
        var classSubjectTeachers = new Dictionary<Guid, List<Guid>>();
        var classGroups = new Dictionary<Guid, (StudentGroup A, StudentGroup B)>();
        (StudentGroup A, StudentGroup B) GroupsOf(SchoolClass cls)
        {
            if (!classGroups.TryGetValue(cls.Id, out var g))
            {
                g = (new StudentGroup { ClassId = cls.Id, Name = "A" },
                     new StudentGroup { ClassId = cls.Id, Name = "B" });
                groups.Add(g.A); groups.Add(g.B);
                classGroups[cls.Id] = g;
            }
            return g;
        }
        void AddItem(SchoolClass cls, string subj, Teacher t, int hours, bool split = false)
        {
            var item = new CurriculumItem
            {
                ClassId = cls.Id, SubjectId = subjByName[subj].Id,
                TeacherId = t.Id, HoursPerWeek = hours, SplitSubgroups = split,
            };
            if (!classSubjectTeachers.TryGetValue(cls.Id, out var tl))
                classSubjectTeachers[cls.Id] = tl = [];
            tl.Add(t.Id);
            curriculum.Add(item);
        }

        foreach (var cls in classes)
        {
            int li = Array.IndexOf(Letters, cls.Name[^1..]);
            bool profile = IsProfile(cls.Grade, li);
            var plan = cls.Grade <= 9 ? Plan[cls.Grade] : Plan10_11(cls.Grade, profile);
            foreach (var s in plan)
            {
                if (s.Hours == 0) continue;
                if (s.Name is "Допризывная подготовка" or "Медицинская подготовка")
                    continue; // парами ниже
                var tA = NextOf(s.Name, cls.Grade, s.Hours);
                if (!IsSameSubjectSplit(cls.Grade, li, s.Name))
                {
                    AddItem(cls, s.Name, tA, s.Hours);
                    continue;
                }
                // Одноимённый сплит A/B (ин.яз/инф/физра/труды).
                var (gA, gB) = GroupsOf(cls);
                Teacher tB;
                if (s.Name == Labor)
                {
                    // Мальчики → boys-пул, девочки → girls-пул (не round-robin).
                    teacherLoad[tA.Id] -= s.Hours; // откат общей раздачи выше
                    tA = laborBoys.MinBy(t => teacherLoad[t.Id])!;
                    teacherLoad[tA.Id] += s.Hours;
                    tB = laborGirls.MinBy(t => teacherLoad[t.Id])!;
                    teacherLoad[tB.Id] += s.Hours;
                    teachIdx[s.Name] += 2;
                }
                else
                {
                    tB = NextOf(s.Name, cls.Grade, s.Hours, excludeId: tA.Id);
                }
                var item = new CurriculumItem
                {
                    ClassId = cls.Id, SubjectId = subjByName[s.Name].Id,
                    TeacherId = tA.Id, HoursPerWeek = s.Hours, SplitSubgroups = true,
                };
                if (!classSubjectTeachers.TryGetValue(cls.Id, out var tl))
                    classSubjectTeachers[cls.Id] = tl = [];
                tl.Add(tA.Id); tl.Add(tB.Id);
                splitTeachers[item.Id] = (tA.Id, tB.Id);
                curriculum.Add(item);
            }
            if (profile)
            {
                // Профильная пара: A(ХимБио)→химия 2ч, B(АнглОбщ)→английский 2ч, одновременно.
                var (gA, gB) = GroupsOf(cls);
                var pairSync = Guid.NewGuid();
                var tChem = NextOf(Chem, cls.Grade, hours: 2);
                var tEng = NextOf(Foreign, cls.Grade, hours: 2, excludeId: tChem.Id);
                curriculum.Add(new CurriculumItem
                {
                    ClassId = cls.Id, SubjectId = subjByName[Chem].Id, TeacherId = tChem.Id,
                    HoursPerWeek = 2, GroupId = gA.Id, SyncGroupId = pairSync,
                });
                curriculum.Add(new CurriculumItem
                {
                    ClassId = cls.Id, SubjectId = subjByName[Foreign].Id, TeacherId = tEng.Id,
                    HoursPerWeek = 2, GroupId = gB.Id, SyncGroupId = pairSync,
                });
                if (!classSubjectTeachers.TryGetValue(cls.Id, out var tl2))
                    classSubjectTeachers[cls.Id] = tl2 = [];
                tl2.Add(tChem.Id); tl2.Add(tEng.Id);
            }
            if (cls.Grade >= 10)
            {
                // ДПМ/мед: мальчики→допризывная, девочки→медподготовка, одновременно.
                var (gA, gB) = GroupsOf(cls);
                var pairSync = Guid.NewGuid();
                curriculum.Add(new CurriculumItem
                {
                    ClassId = cls.Id, SubjectId = subjByName["Допризывная подготовка"].Id,
                    TeacherId = dpm.Id, HoursPerWeek = 1, GroupId = gA.Id, SyncGroupId = pairSync,
                });
                curriculum.Add(new CurriculumItem
                {
                    ClassId = cls.Id, SubjectId = subjByName["Медицинская подготовка"].Id,
                    TeacherId = med.Id, HoursPerWeek = 1, GroupId = gB.Id, SyncGroupId = pairSync,
                });
                if (!classSubjectTeachers.TryGetValue(cls.Id, out var tl3))
                    classSubjectTeachers[cls.Id] = tl3 = [];
                tl3.Add(dpm.Id); tl3.Add(med.Id);
            }
        }

        // Классные руководители — из пула, distinct в пределах сменной sync-группы.
        var usedShift1 = new HashSet<Guid>();
        var usedShift2 = new HashSet<Guid>();
        foreach (var cls in classes.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            var used = IsSecondShiftOur(cls) ? usedShift2 : usedShift1;
            var pick = classSubjectTeachers[cls.Id].FirstOrDefault(id => !used.Contains(id));
            if (pick == Guid.Empty)
                pick = allPool.Select(t => t.Id).FirstOrDefault(id => !used.Contains(id));
            if (pick == Guid.Empty)
                pick = classSubjectTeachers[cls.Id][0];
            cls.ClassTeacherId = pick;
            used.Add(pick);
        }

        // Кабинеты (пересчитаны школой 15.09): 21 универсальный + химия + физика +
        // мастерская + кухня + швейная + 3 информатики (3-й редкий) + спортзал.
        // Спортзал ONLY-физра на 3 группы (мощность 105 группо-слотов ≥ ~74ч физры).
        var rooms = new List<Room>();
        for (int i = 1; i <= 21; i++)
            rooms.Add(new Room { Name = (200 + i).ToString(), PhysicalCapacity = 28, MaxSimultaneousGroups = 1 });
        foreach (var name in new[] { "Каб.химии", "Каб.физики", "Инф-1", "Инф-2", "Инф-3", "Мастерская", "Кухня", "Швейная" })
            rooms.Add(new Room { Name = name, PhysicalCapacity = 28, MaxSimultaneousGroups = 1 });
        var gym = new Room
        {
            Name = "Спортзал", PhysicalCapacity = 90,
            MaxSimultaneousGroups = 3, DesiredGroups = 3,
            CountSubgroupAsGroup = true,
            OnlySubjectId = subjByName[Pe].Id,
        };
        rooms.Add(gym);
        var roomCaps = rooms.Where(r => r.Id != gym.Id)
            .Select(r => new RoomCapability
            {
                RoomId = r.Id, SubjectId = subjByName[Pe].Id,
                Kind = RoomCapabilityKind.Forbidden, // вся физра — только в зал
            }).ToList();

        var input = new ProblemInput(classes, allPool, subjects, curriculum,
            groups, [], [], DaysCount: 5, SlotsPerDay: 14,
            SplitTeachers: splitTeachers, rooms: rooms, roomCaps: roomCaps,
            classSlots: classSlots,
            commonLesson: new CommonLesson
            {
                Enabled = true, DayIndex = 3, SlotIndex = 1, SlotIndexShift2 = 8,
                GradesCsv = "5,6,7,8,9,10,11", UseOwnRooms = true,
            },
            flex: allowOverload
                ? new FlexSettings { AllowTeacherOverload = true, TeacherOverloadCap = overloadCap }
                : FlexSettings.Default);
        return ProblemBuilder.Build(input,
            new SolverOptions(MaxTimeSeconds: budget, NumSearchWorkers: 1,
                RandomSeed: seed, PresolveInPhaseA: false));
    }

    public static SchedulingProblem BuildOurSchool(double budget = 8, int? seed = 11)
    {
        var (problem, errors) = TryBuildOurSchool(budget, seed);
        Assert.Empty(errors);
        return problem!;
    }

    // --- Построение и форма (быстро, без solver) ---
    [Fact]
    public void BuildOurSchool_CountsAndShape()
    {
        var (maybeProblem, errors) = TryBuildOurSchool();
        if (maybeProblem is null)
            output.WriteLine("BUILD ERRORS:\n" + string.Join("\n", errors));
        Assert.Empty(errors);
        var problem = maybeProblem!;
        Assert.Equal(27, problem.Classes.Count);
        Assert.Equal(27, problem.Occurrences.Select(o => o.ClassId).Distinct().Count());
        Assert.Equal(40, problem.Teachers.Count);
        Assert.Equal(30, problem.Rooms.Count);
        var gym = problem.Rooms.Values.Single(r => r.Name == "Спортзал");
        Assert.Equal(3, gym.MaxSimultaneousGroups);
        Assert.NotNull(gym.OnlySubjectId);
        Assert.Equal(29, problem.RoomCaps.Count); // вся физра — только в зал
        // Смены: 6,7 — вторая (слоты 8–14); остальные — первая (слоты 1–7).
        // Проверка через домены целых уроков (без подгрупп и классного часа).
        var hourName = problem.Subjects.Values.Single(s => s.Name == "Классный час").Id;
        foreach (var c in problem.Classes.Values)
        {
            var whole = problem.Occurrences.First(o =>
                o.ClassId == c.Id && !o.GroupId.HasValue && o.SubjectId != hourName);
            var (lo, hi) = IsSecondShiftOur(c) ? (8, 14) : (1, 7);
            Assert.All(problem.AllowedSlots[whole.Id], s => Assert.InRange(s, lo, hi));
        }
        // Классный час: 27, четверг, слот 1 / слот 8, ведёт классный.
        var hourSubj = problem.Subjects.Values.Single(s => s.Name == "Классный час");
        var hours = problem.Occurrences.Where(o => o.SubjectId == hourSubj.Id).ToList();
        Assert.Equal(27, hours.Count);
        foreach (var h in hours)
        {
            var cls = problem.Classes[h.ClassId];
            int expectSlot = IsSecondShiftOur(cls) ? 8 : 1;
            Assert.Equal(3, problem.AllowedDays[h.Id].Single());
            Assert.Equal(expectSlot, problem.AllowedSlots[h.Id].Single());
            Assert.Equal(cls.ClassTeacherId, h.TeacherId);
        }
        // Профильные пары 10А/11А: химия↔английский, разные предметы/учителя/группы, один старт.
        // (Одноимённые сплиты — тоже sync, но с одним предметом; берём только разнопредметные.)
        var chemName = Chem; var engName = Foreign;
        var profilePairs = problem.Occurrences
            .Where(o => o.GroupId.HasValue && o.SyncGroupId.HasValue)
            .GroupBy(o => o.SyncGroupId!.Value)
            .Where(g =>
            {
                var names = g.Select(o => problem.Subjects[o.SubjectId].Name).ToHashSet();
                return names.Count == 2 && names.Contains(engName) && names.Contains(chemName);
            })
            .ToList();
        Assert.Equal(4, profilePairs.Count); // 2 класса × 2 часа
        foreach (var pair in profilePairs)
        {
            var members = pair.ToList();
            Assert.Equal(2, members.Count);
            Assert.Equal(2, members.Select(m => m.SubjectId).Distinct().Count());
            Assert.Equal(2, members.Select(m => m.TeacherId).Distinct().Count());
            Assert.Equal(2, members.Select(m => m.GroupId).Distinct().Count());
            Assert.Single(members.Select(m => m.ClassId).Distinct());
        }
        // ДПМ/мед-пары в 10–11.
        var dpmCount = problem.Occurrences.Count(o =>
            problem.Subjects[o.SubjectId].Name is "Допризывная подготовка" or "Медицинская подготовка");
        Assert.Equal(8, dpmCount); // 4 класса × 2 половины
        output.WriteLine(
            $"OURS build: occ={problem.Occurrences.Count} " +
            $"teachers={problem.Teachers.Count} rooms={problem.Rooms.Count}");
    }

    // --- Greedy-покрытие: школа как есть не влезает — находим, КТО не влез ---
    [Fact]
    public void OurSchool_GreedyCoverage_NamesShortage()
    {
        var problem = BuildOurSchool();
        var g = GreedyPlacer.Place(problem, 11);
        output.WriteLine($"OURS greedy: placed {g.Placed.Count}/{problem.Occurrences.Count}");
        var bySubject = g.Unplaced
            .Select(id => problem.Occurrences.First(o => o.Id == id))
            .GroupBy(o => problem.Subjects[o.SubjectId].Name)
            .Select(grp => (Name: grp.Key, Count: grp.Count()))
            .OrderByDescending(x => x.Count).ToList();
        foreach (var (name, count) in bySubject.Take(10))
            output.WriteLine($"OURS greedy unplaced: {name} — {count}");
        // Арифметика штата: русский блок 122ч на 3 учителей × 30 слотов → ≥32 не влезет.
        var rusUnplaced = bySubject
            .Where(x => x.Name is "Русский язык" or "Русская литература")
            .Sum(x => x.Count);
        Assert.True(g.Placed.Count < problem.Occurrences.Count);
        Assert.True(rusUnplaced >= 20,
            $"Ожидалась нехватка русских (≥20), факт {rusUnplaced} — проверь пулы.");
    }

    // --- Короткий solver-зонд: честный статус, запрет «оптимума» ---
    [Fact]
    public async Task OurSchool_SolverProbe_Honest()
    {
        var problem = BuildOurSchool(budget: 8, seed: 11);
        var solver = new OrToolsSolver();
        var result = await solver.SolveAsync(problem, default);
        output.WriteLine(
            $"OURS probe: status={result.Status} hard={result.HardViolations} " +
            $"place={result.Placements.Count}/{problem.Occurrences.Count} " +
            $"firstMs={result.FirstFeasibleMs} wallMs={result.ElapsedMs}");
        foreach (var d in result.Diagnostics.Take(15))
            output.WriteLine("OURS probe: " + d);
        Assert.NotEqual(SolverStatus.Optimal, result.Status); // оптимум не заявляем никогда
        if (result.Placements.Count > 0)
        {
            var vr = PlacementValidator.Validate(problem, result.Placements,
                EffectiveRuleSet.Default);
            Assert.True(vr.IsValid); // INV-01: что принято — то без жёстких
        }
    }

    // --- Перегрузка: подняты только перегруженные пулы (русские + старшие математики) ---
    [Fact]
    public void OurSchool_Overload_RaisesOnlyOverloaded()
    {
        var (maybeProblem, errors) = TryBuildOurSchool(allowOverload: true);
        Assert.Empty(errors);
        var problem = maybeProblem!;
        var raised = problem.Teachers.Values.Where(t => t.MaxLessonsPerDay > 6).ToList();
        output.WriteLine("OURS overload raised: " +
            string.Join("; ", raised.Select(t => $"{t.Name} ({t.MaxLessonsPerDay}/день)").Order()));
        // Перегружены ровно два пула: русский блок (122ч > 90) и старшая математика
        // (алгебра+геометрия 7–11 = 98ч > 90 — в РБ алгебра начинается с 7-го!).
        var rusPool = problem.Teachers.Values.Where(t =>
            problem.Occurrences.Any(o => o.TeacherId == t.Id &&
                problem.Subjects[o.SubjectId].Name is "Русский язык" or "Русская литература")).ToList();
        var mathSrPool = problem.Teachers.Values.Where(t =>
            problem.Occurrences.Any(o => o.TeacherId == t.Id &&
                problem.Subjects[o.SubjectId].Name is "Алгебра" or "Геометрия")).ToList();
        Assert.Equal(3, rusPool.Count);
        Assert.Equal(3, mathSrPool.Count);
        Assert.Equal(6, raised.Count);
        Assert.All(rusPool.Concat(mathSrPool), t =>
            Assert.Equal(9, problem.Teachers[t.Id].MaxLessonsPerDay));
        // Greedy с перегрузкой: русские влезли полностью.
        var g = GreedyPlacer.Place(problem, 11);
        output.WriteLine($"OURS overload greedy: placed {g.Placed.Count}/{problem.Occurrences.Count}");
        var rusLeftIds = g.Unplaced
            .Select(id => problem.Occurrences.First(o => o.Id == id))
            .Where(o => problem.Subjects[o.SubjectId].Name is "Русский язык" or "Русская литература")
            .ToList();
        foreach (var o in rusLeftIds)
            output.WriteLine($"OURS overload rus-left: {problem.Classes[o.ClassId].Name}|" +
                $"{problem.Subjects[o.SubjectId].Name}|{problem.Teachers[o.TeacherId].Name}");
        // Greedy с перегрузкой: почти всё влезло (остатки ≤4 — фрагментация
        // жадного алгоритма, не штата: слоты есть, бэктрекинга нет; полный ответ — за solver).
        Assert.True(rusLeftIds.Count <= 4);
    }

    // --- Персист настройки: флаг перегрузки переживает запись/чтение (вкл. старые БД) ---
    [Fact]
    public async Task OverloadSetting_StoreRoundtrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"amsur-our-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new Amsur.Infrastructure.SqliteFlexStore($"Data Source={Path.Combine(dir, "flex.db")}");
            await store.InitializeAsync();
            var data = Amsur.Domain.FlexDataset.Empty with
            {
                Settings = new Amsur.Domain.FlexSettingsRow(
                    true, 3, 2, 1, 7, Amsur.Domain.TeacherAssignMode.HardClass,
                    AllowTeacherOverload: true, TeacherOverloadCap: 9),
            };
            await store.SaveAsync(data);
            var got = await store.LoadAsync();
            Assert.True(got.Settings.AllowTeacherOverload);
            Assert.Equal(9, got.Settings.TeacherOverloadCap);
            // Дефолт — выключено.
            Assert.False(Amsur.Domain.FlexDataset.Empty.Settings.AllowTeacherOverload);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // --- Бисекция душителей: что мешает последним ~18 урокам (быстро, без solver) ---
    [Fact]
    public void OurSchool_Overload_Bisect()
    {
        var (maybeProblem, errors) = TryBuildOurSchool(allowOverload: true);
        Assert.Empty(errors);
        var p = maybeProblem!;
        void Probe(string name, SchedulingProblem problem)
        {
            var g = GreedyPlacer.Place(problem, 11);
            output.WriteLine($"OURS bisect {name}: placed {g.Placed.Count}/{problem.Occurrences.Count}");
        }
        SchedulingProblem Copy(
            Dictionary<Guid, Teacher>? teachers = null,
            Dictionary<Guid, Room>? rooms = null,
            HashSet<Guid>? keepOcc = null,
            Dictionary<Guid, SchoolClass>? classes = null)
        {
            var occs = keepOcc is null
                ? p.Occurrences
                : p.Occurrences.Where(o => keepOcc.Contains(o.Id)).ToList();
            var occIds = occs.Select(o => o.Id).ToHashSet();
            return new SchedulingProblem
            {
                Occurrences = occs,
                Classes = classes ?? p.Classes,
                Teachers = teachers ?? p.Teachers,
                Rooms = rooms ?? p.Rooms,
                Subjects = p.Subjects,
                AllowedDays = p.AllowedDays
                    .Where(kv => occIds.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value),
                AllowedSlots = p.AllowedSlots
                    .Where(kv => occIds.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value),
                GroupParents = p.GroupParents,
                RoomCaps = p.RoomCaps,
                BannedTimes = p.BannedTimes,
                Relations = p.Relations,
                DaysCount = p.DaysCount,
                SlotsPerDay = p.SlotsPerDay,
                ShiftBands = p.ShiftBands,
                Options = p.Options,
                Flex = p.Flex,
                Assignments = p.Assignments,
            };
        }
        SchedulingProblem WithRooms(int extra)
        {
            var rooms = p.Rooms.ToDictionary(kv => kv.Key, kv => kv.Value);
            for (int i = 0; i < extra; i++)
            {
                var r = new Room { Name = $"Доп{i + 1}", PhysicalCapacity = 28, MaxSimultaneousGroups = 1 };
                rooms[r.Id] = r;
            }
            return Copy(rooms: rooms);
        }
        SchedulingProblem UncappedTeachers(Dictionary<Guid, Room>? rooms = null) =>
            Copy(
                teachers: p.Teachers.ToDictionary(kv => kv.Key, kv =>
                {
                    var t = kv.Value;
                    return new Teacher
                    {
                        Id = kv.Key, Name = t.Name, MaxLessonsPerDay = 14,
                        PreferredStartSlot = t.PreferredStartSlot, PreferredEndSlot = t.PreferredEndSlot,
                    };
                }),
                rooms: rooms);
        SchedulingProblem WithoutHour()
        {
            var hourId = p.Subjects.Values.Single(s => s.Name == "Классный час").Id;
            var keep = p.Occurrences.Where(o => o.SubjectId != hourId).Select(o => o.Id).ToHashSet();
            return Copy(keepOcc: keep);
        }
        SchedulingProblem WithRooms10() =>
            Copy(rooms: p.Rooms.Values.Concat(Enumerable.Range(1, 10).Select(i =>
                    new Room { Name = $"Доп{i}", PhysicalCapacity = 28, MaxSimultaneousGroups = 1 }))
                .ToDictionary(r => r.Id, r => r));
        Probe("baseline-overload", p);
        Probe("+10rooms", WithRooms10());
        Probe("noteachers-cap", UncappedTeachers());
        Probe("nohour", WithoutHour());
        Probe("+10rooms+nocap", UncappedTeachers(
            p.Rooms.Values.Concat(Enumerable.Range(1, 10).Select(i =>
                    new Room { Name = $"Доп{i}", PhysicalCapacity = 28, MaxSimultaneousGroups = 1 }))
                .ToDictionary(r => r.Id, r => r)));
    }

    // --- Гарды расширения билдера: профильные пары fail-loud ---
    [Fact]
    public void Builder_GroupOfOtherClass_Rejected()
    {
        var (baseProblem, errors) = TryBuildOurSchool();
        Assert.Empty(errors);
        var p = baseProblem!;
        var foreignCls = p.Classes.Values.First(c => c.Grade == 6);
        var otherCls = p.Classes.Values.First(c => c.Grade == 8);
        var otherGroup = p.GroupParents.First(kv => kv.Value == otherCls.Id).Key;
        var item = new CurriculumItem
        {
            ClassId = foreignCls.Id,
            SubjectId = p.Subjects.Values.First(s => s.Name == Foreign).Id,
            TeacherId = p.Teachers.Values.First().Id,
            HoursPerWeek = 1, GroupId = otherGroup,
        };
        var input = new ProblemInput(
            p.Classes.Values.ToList(), p.Teachers.Values.ToList(), p.Subjects.Values.ToList(),
            [item], p.GroupParents.Where(kv => kv.Value == foreignCls.Id || kv.Value == otherCls.Id)
                .Select(kv => new StudentGroup { Id = kv.Key, ClassId = kv.Value, Name = "X" }).ToList(),
            [], [], DaysCount: 5, SlotsPerDay: 14,
            SplitTeachers: new Dictionary<Guid, (Guid, Guid)>(), rooms: [],
            roomCaps: [], classSlots: new Dictionary<Guid, IReadOnlyList<int>>(),
            commonLesson: null, flex: FlexSettings.Default);
        var (_, errs) = ProblemBuilder.Build(input);
        Assert.Contains(errs, e => e.Contains("does not belong to class"));
    }

    [Fact]
    public void Builder_SyncWithoutGroup_Rejected()
    {
        var (baseProblem, errors) = TryBuildOurSchool();
        Assert.Empty(errors);
        var p = baseProblem!;
        var cls = p.Classes.Values.First(c => c.Grade == 6);
        var item = new CurriculumItem
        {
            ClassId = cls.Id,
            SubjectId = p.Subjects.Values.First(s => s.Name == Foreign).Id,
            TeacherId = p.Teachers.Values.First().Id,
            HoursPerWeek = 1, SyncGroupId = Guid.NewGuid(),
        };
        var input = new ProblemInput(
            [cls], p.Teachers.Values.ToList(), p.Subjects.Values.ToList(),
            [item], [], [], [], DaysCount: 5, SlotsPerDay: 14,
            SplitTeachers: new Dictionary<Guid, (Guid, Guid)>(), rooms: [],
            roomCaps: [], classSlots: new Dictionary<Guid, IReadOnlyList<int>>(),
            commonLesson: null, flex: FlexSettings.Default);
        var (_, errs) = ProblemBuilder.Build(input);
        Assert.Contains(errs, e => e.Contains("sync needs a subgroup"));
    }
}
