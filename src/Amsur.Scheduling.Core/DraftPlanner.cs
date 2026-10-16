namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// Черновик-фолбэк (16.10.2026): строгий поиск не дал полного расписания —
// собрать лучшее доступное и честно показать, что в нём нарушено.
//
// Отличие от D-39 («разрешить перегрузку»): там норма ПОДМЕНЯЕТСЯ в задаче
// (нарушение исчезает из отчёта). Здесь норма остаётся исходной: поиск идёт
// по расслабленной копии (чтобы движок мог поставить урок), а валидация —
// против ИСХОДНОЙ задачи, поэтому превышение видно и уходит в Warnings
// только для тех кодов, которые школа явно разрешила.
//
// Черновик не принимается в базу: Accept гейтится Hard==0 (INV-01).
public sealed record DraftOptions(
    IReadOnlySet<string> RelaxCodes,
    int ExtraLessonsPerDay = 1,
    int Seed = 0,
    TimeSpan? PolishBudget = null)
{
    /// <summary>Набор по умолчанию (выбор школы, 16.10.2026): перегруз класса,
    /// перегруз учителя, поздний старт ученика. Окна и разнесение физры — нет.</summary>
    public static IReadOnlySet<string> DefaultRelaxCodes { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            DraftPlanner.ClassMaxPerDay,
            DraftPlanner.TeacherMaxPerDay,
            DraftPlanner.StudentLateStart,
        };

    /// <summary>Без послаблений: только то, что даёт жадный старт.</summary>
    public static DraftOptions StrictOnly(int seed = 0) =>
        new(new HashSet<string>(StringComparer.Ordinal), 0, seed, TimeSpan.Zero);

    public bool Allows(string code) => RelaxCodes.Contains(code);
}

public sealed record DraftResult(
    IReadOnlyList<PlacedLesson> Placements,
    IReadOnlyList<Guid> Unplaced,
    IReadOnlyList<ValidationIssue> Blocking,   // мешают выгрузке (школа не разрешала)
    IReadOnlyList<ValidationIssue> Noted,      // не блокируют (D-42), но глазами проверить
    IReadOnlyList<ValidationIssue> Allowed,    // разрешено школой → предупреждения
    long SoftTotal,
    IReadOnlyList<string> Summary)
{
    public int PlacedCount => Placements.Count;
    public bool IsComplete => Unplaced.Count == 0;
    /// <summary>Годен как черновик: выгрузка не будет отклонена.</summary>
    public bool IsUsable => Blocking.Count == 0;
}

public static class DraftPlanner
{
    public const string ClassMaxPerDay = "class-maxperday";
    public const string TeacherMaxPerDay = "teacher-maxperday";
    public const string StudentLateStart = "student-late-start";
    public const string StudentGap = "student-gap";
    public const string SanpinPeSpacing = "sanpin-pe-spacing";

    /// <summary>
    /// Коды, которые выгрузку ЧЕРНОВИКА не блокируют (D-42): на черновых данных
    /// полнота/окна/поздний старт смысла не имеют. Единственный источник истины —
    /// и DraftPlanner, и ScheduleExcelExporter зовут этот метод, чтобы гейты не разъехались.
    /// </summary>
    public static bool IsNonBlockingForDraft(string code) =>
        code is "placement-count" or StudentGap or StudentLateStart;

    /// <summary>
    /// Собрать черновик: жадный старт по расслабленной копии задачи,
    /// необязательная короткая доводка, валидация против исходных норм.
    /// Детерминирован (порядок обходов — StableKey, сид из options).
    /// </summary>
    public static DraftResult Build(
        SchedulingProblem problem,
        DraftOptions options,
        EffectiveRuleSet? rules = null,
        CancellationToken ct = default)
    {
        var relaxed = RelaxDayLimits(problem, options);
        var greedy = GreedyPlacer.Place(relaxed, options.Seed);
        var placements = greedy.Placed
            .OrderBy(kv => kv.Key)
            .Select(kv => new PlacedLesson
            {
                OccurrenceId = kv.Key,
                DayIndex = kv.Value.Day,
                SlotIndex = kv.Value.Slot,
                RoomId = kv.Value.RoomId,
            }).ToList();

        var rulesForDraft = (rules ?? EffectiveRuleSet.Default) with
        {
            RelaxedStrict = options.RelaxCodes,
        };
        long soft = SoftEvaluator.Evaluate(problem, placements, rulesForDraft).Total;

        // Доводка качества — только по полному размещению: LocalSearch не
        // досеивает недостающие уроки, для частичного черновика это шум.
        var polish = options.PolishBudget ?? TimeSpan.Zero;
        if (polish > TimeSpan.Zero && greedy.Unplaced.Count == 0 && !ct.IsCancellationRequested)
        {
            var ls = LocalSearch.Improve(relaxed, placements, polish, options.Seed, ct);
            placements = ls.Placements;
            soft = ls.SoftTotal;
        }

        var validation = PlacementValidator.Validate(problem, placements, rulesForDraft);
        // Три честных ведра: блокирует выгрузку / не блокирует, но проверь глазами /
        // разрешено школой. Полнота отдельно — списком Unplaced (не дублируем строкой
        // «placement-count»). Набор неблокирующих кодов — один на проект (см. выше).
        var blocking = validation.HardViolations
            .Where(v => !IsNonBlockingForDraft(v.Code)).ToList();
        var noted = validation.HardViolations
            .Where(v => IsNonBlockingForDraft(v.Code) && v.Code != "placement-count").ToList();
        var summary = BuildSummary(problem, placements, greedy.Unplaced, blocking, noted,
            validation.Warnings, soft);

        return new DraftResult(placements, greedy.Unplaced, blocking, noted,
            validation.Warnings, soft, summary);
    }

    /// <summary>
    /// Копия задачи с поднятыми дневными лимитами (класс/учитель) — только для
    /// тех правил, которые школа разрешила нарушить. Исходная задача не мутируется.
    /// </summary>
    private static SchedulingProblem RelaxDayLimits(SchedulingProblem problem, DraftOptions options)
    {
        int extra = Math.Max(0, options.ExtraLessonsPerDay);
        bool relaxClass = extra > 0 && options.Allows(ClassMaxPerDay);
        bool relaxTeacher = extra > 0 && options.Allows(TeacherMaxPerDay);
        if (!relaxClass && !relaxTeacher) return problem;

        Dictionary<Guid, SchoolClass> classes = problem.Classes;
        if (relaxClass)
        {
            classes = new Dictionary<Guid, SchoolClass>(problem.Classes.Count);
            foreach (var (id, c) in problem.Classes)
                classes[id] = new SchoolClass
                {
                    Id = c.Id,
                    AcademicYearId = c.AcademicYearId,
                    Name = c.Name,
                    Grade = c.Grade,
                    ShiftId = c.ShiftId,
                    StudentCount = c.StudentCount,
                    ClassTeacherId = c.ClassTeacherId,
                    MaxLessonsPerDay = Math.Min(problem.SlotsPerDay, c.MaxLessonsPerDay + extra),
                };
        }

        Dictionary<Guid, Teacher> teachers = problem.Teachers;
        if (relaxTeacher)
        {
            teachers = new Dictionary<Guid, Teacher>(problem.Teachers.Count);
            foreach (var (id, t) in problem.Teachers)
                teachers[id] = new Teacher
                {
                    Id = t.Id,
                    Name = t.Name,
                    PreferredStartSlot = t.PreferredStartSlot,
                    PreferredEndSlot = t.PreferredEndSlot,
                    MaxLessonsPerDay = Math.Min(problem.SlotsPerDay, t.MaxLessonsPerDay + extra),
                };
        }

        return new SchedulingProblem
        {
            Occurrences = problem.Occurrences,
            Classes = classes,
            Teachers = teachers,
            Rooms = problem.Rooms,
            Subjects = problem.Subjects,
            AllowedDays = problem.AllowedDays,
            AllowedSlots = problem.AllowedSlots,
            GroupParents = problem.GroupParents,
            RoomCaps = problem.RoomCaps,
            BannedTimes = problem.BannedTimes,
            Relations = problem.Relations,
            DaysCount = problem.DaysCount,
            SlotsPerDay = problem.SlotsPerDay,
            ShiftBands = problem.ShiftBands,
            Options = problem.Options,
            Flex = problem.Flex,
            Assignments = problem.Assignments,
        };
    }

    private static List<string> BuildSummary(
        SchedulingProblem problem,
        IReadOnlyList<PlacedLesson> placements,
        IReadOnlyList<Guid> unplaced,
        IReadOnlyList<ValidationIssue> blocking,
        IReadOnlyList<ValidationIssue> noted,
        IReadOnlyList<ValidationIssue> allowed,
        long soft)
    {
        var lines = new List<string>
        {
            $"Черновик: размещено {placements.Count} из {problem.Occurrences.Count} уроков " +
            $"(оценка качества {soft}).",
        };

        if (unplaced.Count > 0)
        {
            var occById = problem.Occurrences.ToDictionary(o => o.Id);
            var names = unplaced.Take(5).Select(id =>
                occById.TryGetValue(id, out var o)
                    ? (string.IsNullOrEmpty(o.StableKey) ? id.ToString("N")[..8] : o.StableKey)
                    : id.ToString("N")[..8]);
            lines.Add($"Не поставлено: {unplaced.Count} — {string.Join("; ", names)}" +
                (unplaced.Count > 5 ? $" и ещё {unplaced.Count - 5}." : "."));
        }

        foreach (var group in allowed.GroupBy(v => v.Code).OrderBy(g => g.Key, StringComparer.Ordinal))
            lines.Add($"Разрешено школой — {Label(group.Key)}: {group.Count()}. " +
                $"Например: {group.First().Message}");

        foreach (var group in noted.GroupBy(v => v.Code).OrderBy(g => g.Key, StringComparer.Ordinal))
            lines.Add($"Проверь глазами (выгрузку не блокирует) — {Label(group.Key)}: " +
                $"{group.Count()}. Например: {group.First().Message}");

        foreach (var group in blocking.GroupBy(v => v.Code).OrderBy(g => g.Key, StringComparer.Ordinal))
            lines.Add($"Выгрузка невозможна — {Label(group.Key)}: {group.Count()}. " +
                $"Например: {group.First().Message}");

        return lines;
    }

    private static string Label(string code) => code switch
    {
        ClassMaxPerDay => "перегруз класса по урокам в день",
        TeacherMaxPerDay => "перегруз учителя по урокам в день",
        StudentLateStart => "позднее начало дня у класса",
        StudentGap => "окна у класса",
        SanpinPeSpacing => "физкультура не разнесена по дням",
        _ => code,
    };
}
