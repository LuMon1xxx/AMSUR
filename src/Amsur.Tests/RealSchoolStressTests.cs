using Amsur.Domain;
using Amsur.Scheduling.Core;
using Amsur.Scheduling.OrTools;

namespace Amsur.Tests;

// Прототип «типовая СШ Минска»: 40 классов (1–9 АБВГ, 10–11 АБ), 100 учителей, 40 кабинетов.
// Учебный план — аппроксимация типовых планов РБ (пост. МО №47/2024, №75/2025, №104/2026),
// суммы часов/нед = нормам 5-дневки 21/23/29/30/32/33/34 (см. SUBJECTS_RB.md §2).
// Точные официальные раскладки — в приложениях постановлений; отклонения — в SUBJECTS_RB.md §3.
// Сетка 5 дней (Пн–Пт) × 14 слотов: 1–7 первая смена, 8–14 вторая смена (SANPIN_RB.md §6).
// 1–4, 9–11 — первая смена; 5–8 — вторая. Сплит A/B — иностранный язык 5–11.
public sealed class RealSchoolStressTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private sealed record SubjHours(string Name, int Hours, bool Split = false);

    private static readonly Dictionary<int, SubjHours[]> Plan = new()
    {
        [1] = [new("Математика", 4), new("Русский язык", 4), new("Белорусский язык", 3), new("Русская литература", 2), new("Белорусская литература", 1), new("Человек и мир", 1), new("Музыка", 1), new("Изобразительное искусство", 1), new("Физическая культура и здоровье", 3), new("Трудовое обучение", 1)],
        [2] = [new("Математика", 4), new("Русский язык", 4), new("Белорусский язык", 3), new("Русская литература", 2), new("Белорусская литература", 2), new("Человек и мир", 1), new("Музыка", 1), new("Изобразительное искусство", 1), new("Физическая культура и здоровье", 3), new("Трудовое обучение", 2)],
        [3] = [new("Математика", 4), new("Русский язык", 4), new("Белорусский язык", 3), new("Русская литература", 1), new("Белорусская литература", 1), new("Иностранный язык", 3), new("Человек и мир", 1), new("Музыка", 1), new("Изобразительное искусство", 1), new("Физическая культура и здоровье", 3), new("Трудовое обучение", 1)],
        [4] = [new("Математика", 4), new("Русский язык", 4), new("Белорусский язык", 3), new("Русская литература", 1), new("Белорусская литература", 1), new("Иностранный язык", 3), new("Человек и мир", 1), new("Музыка", 1), new("Изобразительное искусство", 1), new("Физическая культура и здоровье", 3), new("Трудовое обучение", 1)],
        [5] = [new("Математика", 5), new("Русский язык", 3), new("Русская литература", 2), new("Белорусский язык", 2), new("Белорусская литература", 2), new("Иностранный язык", 3, true), new("История", 2), new("География", 2), new("Биология", 1), new("Информатика", 1), new("Музыка", 1), new("Изобразительное искусство", 1), new("Физическая культура и здоровье", 2), new("Трудовое обучение", 2)],
        [6] = [new("Математика", 5), new("Русский язык", 3), new("Русская литература", 2), new("Белорусский язык", 2), new("Белорусская литература", 2), new("Иностранный язык", 3, true), new("История", 2), new("География", 2), new("Биология", 2), new("Информатика", 1), new("Музыка", 1), new("Физическая культура и здоровье", 3), new("Трудовое обучение", 2)],
        [7] = [new("Алгебра", 4), new("Геометрия", 2), new("Русский язык", 2), new("Русская литература", 2), new("Белорусский язык", 2), new("Белорусская литература", 2), new("Иностранный язык", 3, true), new("История", 2), new("География", 2), new("Биология", 2), new("Физика", 2), new("Информатика", 2), new("Физическая культура и здоровье", 3), new("Трудовое обучение", 2)],
        [8] = [new("Алгебра", 4), new("Геометрия", 2), new("Русский язык", 2), new("Русская литература", 2), new("Белорусский язык", 2), new("Белорусская литература", 1), new("Иностранный язык", 3, true), new("История", 2), new("География", 2), new("Биология", 2), new("Физика", 3), new("Химия", 2), new("Информатика", 1), new("Физическая культура и здоровье", 3), new("Трудовое обучение", 1), new("Основы безопасности жизнедеятельности", 1)],
        [9] = [new("Алгебра", 4), new("Геометрия", 2), new("Русский язык", 2), new("Русская литература", 2), new("Белорусский язык", 2), new("Белорусская литература", 1), new("Иностранный язык", 3, true), new("История", 2), new("География", 2), new("Биология", 2), new("Физика", 3), new("Химия", 2), new("Информатика", 1), new("Физическая культура и здоровье", 3), new("Трудовое обучение", 1), new("Основы безопасности жизнедеятельности", 1)],
        [10] = [new("Алгебра", 3), new("Геометрия", 2), new("Русский язык", 2), new("Русская литература", 3), new("Белорусский язык", 2), new("Белорусская литература", 1), new("Иностранный язык", 3, true), new("Физика", 3), new("Химия", 2), new("Биология", 2), new("География", 2), new("История", 2), new("Обществоведение", 2), new("Информатика", 2), new("Физическая культура и здоровье", 2), new("Допризывная и медицинская подготовка", 1)],
        [11] = [new("Алгебра", 3), new("Геометрия", 2), new("Русский язык", 2), new("Русская литература", 3), new("Белорусский язык", 2), new("Белорусская литература", 1), new("Иностранный язык", 3, true), new("Физика", 3), new("Химия", 2), new("Биология", 2), new("География", 1), new("История", 2), new("Обществоведение", 2), new("Информатика", 2), new("Физическая культура и здоровье", 2), new("Астрономия", 1), new("Допризывная и медицинская подготовка", 1)],
    };

    // Суммы планов обязаны равняться недельным нормам 5-дневки (SANPIN_RB.md §4).
    public static readonly Dictionary<int, int> WeeklyNorm = new()
    {
        [1] = 21, [2] = 23, [3] = 23, [4] = 23, [5] = 29, [6] = 30,
        [7] = 32, [8] = 33, [9] = 33, [10] = 34, [11] = 34,
    };

    // Дневной максимум по СанПиН (SANPIN_RB.md §3): 1→5 (4+1×5), 2–4→5, 5–6→6, 7–11→7.
    public static int ClassDayCap(int grade) => grade <= 4 ? 5 : grade <= 6 ? 6 : 7;

    // Смены (SANPIN_RB.md §6): 1–4, 9–11 — первая; 5–8 — вторая.
    public static bool IsSecondShift(int grade) => grade is >= 5 and <= 8;

    private static string[] Letters(int grade) =>
        grade <= 9 ? ["А", "Б", "В", "Г"] : ["А", "Б"];

    public static SchedulingProblem BuildRealSchool(
        bool includeRooms = true, double budget = 90, int workers = 1, bool presolveA = true,
        int[]? grades = null)
    {
        grades ??= Enumerable.Range(1, 11).ToArray();
        var year = Guid.NewGuid();
        var shift1 = Guid.NewGuid();
        var shift2 = Guid.NewGuid();
        var classes = new List<SchoolClass>();
        foreach (var grade in grades)
            foreach (var letter in Letters(grade))
                classes.Add(new SchoolClass
                {
                    AcademicYearId = year, Name = $"{grade}{letter}",
                    Grade = grade, StudentCount = 25,
                    ShiftId = IsSecondShift(grade) ? shift2 : shift1,
                    MaxLessonsPerDay = ClassDayCap(grade),
                });

        // Посменные слоты (D-28): 1-я смена 1..7, 2-я смена 8..14.
        var classSlots = classes.ToDictionary(
            c => c.Id,
            c => (IReadOnlyList<int>)(IsSecondShift(c.Grade)
                ? Enumerable.Range(8, 7).ToList()
                : Enumerable.Range(1, 7).ToList()));

        // Учителя (S5 P2 — реалистичная началка): 1–4 классы ведёт ОДИН классный учитель
        // (всё кроме физкультуры и музыки — у специалистов); 5–11 — предметные пулы
        // ~15 ч/ставка. Итого ровно 100 (добивка крупным пулам).
        // Имена — белорусские фамилии с инициалами (детерминированно по порядку создания).
        string[] surnames =
        [
            "Ковалёв", "Мороз", "Козлов", "Новикова", "Соколова", "Михайлова", "Фёдорова",
            "Волкова", "Смирнова", "Кузнецова", "Попова", "Орлова", "Семёнова", "Егорова",
            "Павлова", "Громова", "Лебедева", "Сорокина", "Дроздова", "Кузьмина",
            "Иванова", "Петрова", "Сидорова", "Кравцова", "Мельникова", "Шевченко",
            "Бондаренко", "Климова", "Савицкая", "Герасимова", "Макарова", "Захарова",
        ];
        string[] initials = ["А.В.", "М.С.", "Д.И.", "Е.П.", "О.Н.", "И.К.", "Т.В.", "Н.А."];
        int nameIdx = 0;
        Teacher NewTeacher()
        {
            var t = new Teacher
            {
                Name = $"{surnames[nameIdx % surnames.Length]} {initials[(nameIdx / surnames.Length) % initials.Length]}",
                MaxLessonsPerDay = 6
            };
            nameIdx++;
            return t;
        }
        // Предметы-специалисты началки (остальное ведёт классный).
        static bool IsPrimarySpecialist(string subj) =>
            subj is "Физическая культура и здоровье" or "Музыка";
        var primaryClasses = classes.Where(c => c.Grade <= 4).ToList();
        var classTeacherOf = primaryClasses.ToDictionary(c => c.Id, _ => NewTeacher());
        // Специалисты началки (только если началка в сборке): физра + музыка.
        var primaryPe = primaryClasses.Count > 0
            ? Enumerable.Range(0, 3).Select(_ => NewTeacher()).ToList()
            : new List<Teacher>();
        var primaryMusic = primaryClasses.Count > 0
            ? new List<Teacher> { NewTeacher() }
            : new List<Teacher>();
        int primaryStaff = classTeacherOf.Count + primaryPe.Count + primaryMusic.Count;
        int peIdx = 0, muIdx = 0;
        var subjHours = new Dictionary<string, int>();
        var primaryGradesInBuild = primaryClasses.Select(c => c.Grade).ToHashSet();
        foreach (var grade in grades)
            foreach (var s in Plan[grade])
            {
                // Часы началки покрыты классными учителями и специалистами (вне пулов).
                if (grade <= 4 && primaryGradesInBuild.Contains(grade)) continue;
                subjHours[s.Name] = subjHours.GetValueOrDefault(s.Name) + s.Hours * Letters(grade).Length;
            }
        var poolSize = subjHours.ToDictionary(kv => kv.Key,
            kv => Math.Max(1, (int)Math.Round(kv.Value / 15.0)));
        while (primaryStaff + poolSize.Values.Sum() < 100)
        {
            var biggest = poolSize.MaxBy(kv => subjHours[kv.Key]).Key;
            poolSize[biggest]++;
        }
        Assert.Equal(100, primaryStaff + poolSize.Values.Sum());
        var teachers = new Dictionary<string, List<Teacher>>();
        foreach (var (subj, n) in poolSize)
            teachers[subj] = Enumerable.Range(0, n).Select(_ => NewTeacher()).ToList();

        // Предметы — объединение планов включённых параллелей (пулы покрывают 5–11;
        // предметы только началки ведут классные, но сущности нужны).
        var subjects = grades.SelectMany(g => Plan[g]).Select(s => s.Name).Distinct()
            .Select(n => new Subject { Name = n, MaxPerDay = 2 }).ToList();
        var subjByName = subjects.ToDictionary(s => s.Name);
        var teachIdx = poolSize.Keys.ToDictionary(k => k, _ => 0);

        var curriculum = new List<CurriculumItem>();
        var groups = new List<StudentGroup>();
        var splitTeachers = new Dictionary<Guid, (Guid, Guid)>();
        foreach (var cls in classes)
        {
            StudentGroup? gA = null, gB = null;
            foreach (var s in Plan[cls.Grade])
            {
                Teacher tA;
                if (cls.Grade <= 4 && classTeacherOf.TryGetValue(cls.Id, out var ct))
                {
                    // Началка: классный ведёт всё, кроме физры/музыки (специалисты).
                    tA = IsPrimarySpecialist(s.Name)
                        ? (s.Name.StartsWith("Физ") ? primaryPe[peIdx++ % primaryPe.Count]
                            : primaryMusic[muIdx++ % primaryMusic.Count])
                        : ct;
                }
                else
                {
                    var pool = teachers[s.Name];
                    tA = pool[teachIdx[s.Name] % pool.Count]; teachIdx[s.Name]++;
                }
                var item = new CurriculumItem
                {
                    ClassId = cls.Id, SubjectId = subjByName[s.Name].Id,
                    TeacherId = tA.Id, HoursPerWeek = s.Hours, SplitSubgroups = s.Split,
                };
                if (s.Split)
                {
                    if (gA is null)
                    {
                        gA = new StudentGroup { ClassId = cls.Id, Name = "A" };
                        gB = new StudentGroup { ClassId = cls.Id, Name = "B" };
                        groups.Add(gA); groups.Add(gB!);
                    }
                    var pool = teachers[s.Name];
                    var tB = pool[teachIdx[s.Name] % pool.Count]; teachIdx[s.Name]++;
                    if (tB.Id == tA.Id) tB = pool[(teachIdx[s.Name]++) % pool.Count];
                    splitTeachers[item.Id] = (tA.Id, tB.Id);
                }
                curriculum.Add(item);
            }
        }

        var rooms = new List<Room>();
        for (int i = 1; i <= 37; i++)
            rooms.Add(new Room
            {
                Name = (100 + i * 3).ToString(),
                PhysicalCapacity = 28, MaxSimultaneousGroups = 1
            });
        rooms.Add(new Room { Name = "Спортзал", PhysicalCapacity = 60, MaxSimultaneousGroups = 3 });
        rooms.Add(new Room { Name = "Актовый зал", PhysicalCapacity = 100, MaxSimultaneousGroups = 2 });
        rooms.Add(new Room { Name = "Мастерская", PhysicalCapacity = 20, MaxSimultaneousGroups = 1 });

        var input = new ProblemInput(classes,
            teachers.Values.SelectMany(x => x)
                .Concat(classTeacherOf.Values).Concat(primaryPe).Concat(primaryMusic)
                .ToList(),
            subjects, curriculum,
            groups, [], [], DaysCount: 5, SlotsPerDay: 14,
            SplitTeachers: splitTeachers, rooms: includeRooms ? rooms : [],
            classSlots: classSlots);
        var (problem, errors) = ProblemBuilder.Build(input,
            new SolverOptions(MaxTimeSeconds: budget, NumSearchWorkers: workers, RandomSeed: 11, PresolveInPhaseA: presolveA));
        Assert.Empty(errors);
        return problem!;
    }

    // --- R2: построение + масштабы (быстро, без solver) ---
    [Fact]
    public void BuildRealSchool_CountsAndTiming()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var problem = BuildRealSchool();
        sw.Stop();
        int occ = problem.Occurrences.Count;
        // Плотность на класс: occ / (классы × 5 дней × 7 слотов смены).
        double density = (double)occ / (40 * 5 * 7);
        output.WriteLine(
            $"REALSCHOOL build: occ={occ} classes=40 teachers={problem.Teachers.Count} " +
            $"rooms={problem.Rooms.Count} grid=5x14(2 смены) density={density:F2} buildMs={sw.ElapsedMilliseconds}");
        Assert.Equal(40, problem.Classes.Count);
        Assert.Equal(100, problem.Teachers.Count);
        Assert.Equal(40, problem.Rooms.Count);
        Assert.True(occ > 1000);
    }

    // Суммы планов = недельным нормам 5-дневки (защита от «лишних/недостающих» часов).
    [Fact]
    public void PlanHours_MatchWeeklyNorms()
    {
        foreach (var (grade, rows) in Plan)
            Assert.Equal(WeeklyNorm[grade], rows.Sum(r => r.Hours));
    }

    // S5 P2: 1–4 классы ведёт один классный (+ специалисты физра/музыка), старт — 1-я смена.
    [Fact]
    public void PrimaryClasses_SingleClassTeacher()
    {
        var problem = BuildRealSchool();
        var occById = problem.Occurrences.ToDictionary(o => o.Id);
        foreach (var cls in problem.Classes.Values.Where(c => c.Grade <= 4))
        {
            var teachers = problem.Occurrences
                .Where(o => o.ClassId == cls.Id)
                .GroupBy(o => o.TeacherId)
                .ToDictionary(g => g.Key, g => g.Select(o =>
                    problem.Subjects.TryGetValue(o.SubjectId, out var s) ? s.Name : "?").Distinct().ToList());
            // Основной учитель: ведёт >= 6 разных предметов (почти всё).
            var main = teachers.MaxBy(kv => kv.Value.Count);
            Assert.True(main.Value.Count >= 6);
            // Остальные — только физра/музыка.
            foreach (var (tid, subjs) in teachers.Where(kv => kv.Key != main.Key))
                Assert.All(subjs, s => Assert.True(
                    s is "Физическая культура и здоровье" or "Музыка", $"класс {cls.Name}: {s}"));
            // Смена первая (слоты 1..7).
            foreach (var occ in problem.Occurrences.Where(o => o.ClassId == cls.Id))
                Assert.All(problem.AllowedSlots[occ.Id], s => Assert.InRange(s, 1, 7));
        }
    }
    // --- Зонд: presolve-off в Phase A (probing съедал бюджет до первой ветки) ---
    [Fact]
    public async Task ProbeRealSchool_NoPresolveA()
    {
        var problem = BuildRealSchool(budget: 90, workers: 1, presolveA: false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await new OrToolsSolver().SolveAsync(problem);
        sw.Stop();
        var ms = result.ModelStats ?? new Dictionary<string, long>();
        output.WriteLine(
            $"PROBE no-presolve-A: status={result.Status} wallMs={sw.ElapsedMilliseconds} " +
            $"solutions={result.SolutionsFound} bestSoft={(result.Placements.Count == 0 ? -1 : result.ObjectiveValue)} " +
            $"hard={result.HardViolations} firstFeasibleMs={result.FirstFeasibleMs} " +
            $"phaseMs=[{string.Join(";", result.PhaseMs.Select(kv => $"{kv.Key}={kv.Value}"))}] " +
            $"peakMb={ms.GetValueOrDefault("peakMemoryMb")} " +
            $"diagnostics=[{string.Join("|", result.Diagnostics)}]");
        Assert.True(sw.ElapsedMilliseconds < 300000);
    }

    // --- Зонд: та же школа БЕЗ кабинетов (изоляция причины краха) ---
    [Fact]
    public async Task ProbeRealSchool_NoRooms_30s()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var problem = BuildRealSchool(includeRooms: false, budget: 30);
        var result = await new OrToolsSolver().SolveAsync(problem);
        sw.Stop();
        output.WriteLine(
            $"PROBE no-rooms: occ={problem.Occurrences.Count} status={result.Status} " +
            $"wallMs={sw.ElapsedMilliseconds} solutions={result.SolutionsFound} " +
            $"phaseMs=[{string.Join(";", result.PhaseMs.Select(kv => $"{kv.Key}={kv.Value}"))}]");
        Assert.True(sw.ElapsedMilliseconds < 300000);
    }

    // --- §8 benchmark-матрица: Medium (~390 occ, 5–7 кл.) и Large (~850 occ, 4–10 кл.) ---
    [Fact]
    public async Task Matrix_Medium_60s() =>
        await RunMatrixAsync("MEDIUM", [5, 6, 7], budget: 60, wallCapMs: 120000);

    [Fact]
    public async Task Matrix_Large_120s() =>
        await RunMatrixAsync("LARGE", [4, 5, 6, 7, 8, 9, 10], budget: 120, wallCapMs: 240000);

    private async Task RunMatrixAsync(string tag, int[] grades, double budget, long wallCapMs)
    {
        var swBuild = System.Diagnostics.Stopwatch.StartNew();
        var problem = BuildRealSchool(budget: budget, grades: grades);
        swBuild.Stop();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await new OrToolsSolver().SolveAsync(problem);
        sw.Stop();
        var ms = result.ModelStats ?? new Dictionary<string, long>();
        output.WriteLine(
            $"{tag}: occ={problem.Occurrences.Count} status={result.Status} " +
            $"buildMs={swBuild.ElapsedMilliseconds} " +
            $"modelBuildMs={result.PhaseMs.GetValueOrDefault("modelBuildB", result.PhaseMs.GetValueOrDefault("modelBuildA"))} " +
            $"firstFeasibleMs={result.FirstFeasibleMs} totalMs={sw.ElapsedMilliseconds} " +
            $"bestSoft={(result.Placements.Count == 0 ? -1 : result.ObjectiveValue)} " +
            $"hard={result.HardViolations} solutions={result.SolutionsFound} " +
            $"vars=[{ms.GetValueOrDefault("intVars")}/{ms.GetValueOrDefault("boolVars")}/{ms.GetValueOrDefault("constraints")}] " +
            $"lanes={ms.GetValueOrDefault("laneCount")} peakMb={ms.GetValueOrDefault("peakMemoryMb")} " +
            $"diagnostics=[{string.Join("|", result.Diagnostics)}]");
        Assert.True(sw.ElapsedMilliseconds < wallCapMs);
        if (result.Status == SolverStatus.Feasible)
        {
            Assert.Equal(0, result.HardViolations);
            Assert.True(PlacementValidator.Validate(problem, result.Placements).IsValid);
        }
    }

    // EPIC-H H6/H10: Phase B пропускается на большой школе (порог 300 occ),
    // качество ведёт LS+VND; контракт (Feasible/Hard=0/Accept-gate) сохранён.
    [Fact]
    public async Task PhaseBSkipped_OnRealSchool()
    {
        var problem = BuildRealSchool(budget: 10);
        Assert.True(problem.Occurrences.Count >= OrToolsSolver.LargeSchoolPhaseBThreshold);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await new OrToolsSolver().SolveAsync(problem);
        sw.Stop();
        output.WriteLine(
            $"H6-GATE: status={result.Status} wallMs={sw.ElapsedMilliseconds} " +
            $"soft={result.ObjectiveValue} hard={result.HardViolations} " +
            $"phaseMs=[{string.Join(";", result.PhaseMs.Select(kv => $"{kv.Key}={kv.Value}"))}] " +
            $"diagnostics=[{string.Join("|", result.Diagnostics)}]");
        Assert.Equal(SolverStatus.Feasible, result.Status);
        Assert.Equal(0, result.HardViolations);
        Assert.True(PlacementValidator.Validate(problem, result.Placements).IsValid);
        Assert.Contains(result.Diagnostics, d => d.Contains("Phase B skipped"));
        Assert.Equal(0, result.PhaseMs.GetValueOrDefault("phaseB"));
        Assert.True(result.FirstFeasibleMs < 10000);
    }

    // R3: полная школа С кабинетами, lane-модель D-24 + greedy/LS (бывший краш OOM).
    // Бюджет — env AMSUR_REAL_BUDGET (дефолт 90с для сьюта); длинные production-прогоны
    // запускаются фильтром с большим бюджетом, цифры — в BENCHMARKS.md.
    [Fact]
    public async Task SolveRealSchool_Capped90s()
    {
        double budget = 90;
        if (double.TryParse(Environment.GetEnvironmentVariable("AMSUR_REAL_BUDGET"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double env))
            budget = env;
        var problem = BuildRealSchool(budget: budget);
        long memBefore = GC.GetTotalMemory(true);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await new OrToolsSolver().SolveAsync(problem);
        sw.Stop();
        long memAfter = GC.GetTotalMemory(false);
        var ms = result.ModelStats ?? new Dictionary<string, long>();
        output.WriteLine(
            $"REALSCHOOL solve: status={result.Status} wallMs={sw.ElapsedMilliseconds} " +
            $"solutions={result.SolutionsFound} bestSoft={(result.Placements.Count == 0 ? -1 : result.ObjectiveValue)} " +
            $"phaseMs=[{string.Join(";", result.PhaseMs.Select(kv => $"{kv.Key}={kv.Value}"))}] " +
            $"vars=[{ms.GetValueOrDefault("intVars")}/{ms.GetValueOrDefault("boolVars")}/{ms.GetValueOrDefault("constraints")}] " +
            $"lanes={ms.GetValueOrDefault("laneCount")} hints={ms.GetValueOrDefault("hintsApplied")} " +
            $"peakMb={ms.GetValueOrDefault("peakMemoryMb")} " +
            $"memDeltaMb={(memAfter - memBefore) / 1048576} diagnostics=[{string.Join("|", result.Diagnostics)}]");
        // Gate только на завершение попытки без исключений; feasible не требуем (честно).
        // Потолок — бюджет + запас на оверхед (сборка/валидация/GC).
        Assert.True(sw.ElapsedMilliseconds < budget * 1000 + 120000);
        if (result.Status == SolverStatus.Feasible)
        {
            // §13 Correctness: принят только validator-clean.
            Assert.Equal(0, result.HardViolations);
            Assert.True(PlacementValidator.Validate(problem, result.Placements).IsValid);
            // §7: feasible-first быстрый.
            Assert.True(result.FirstFeasibleMs < 10000, $"firstFeasible={result.FirstFeasibleMs}ms");
            // §6 Quality: не хуже жадного baseline (иначе улучшение сломано).
            var greedy = GreedyPlacer.Place(problem);
            var greedySoft = SoftEvaluator.Evaluate(problem,
                greedy.Placed.Select(kv => new PlacedLesson
                {
                    OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
                    SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
                }).ToList()).Total;
            output.WriteLine($"REALSCHOOL greedySoft={greedySoft}");
            Assert.True(result.ObjectiveValue <= greedySoft);
        }
    }
}
