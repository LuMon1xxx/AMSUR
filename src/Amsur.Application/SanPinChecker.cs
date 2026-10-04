using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Application;

// P-SANPIN-CHECK — СанПиН-чекер РБ как ОТЧЁТ (не gate: INV-01 не трогаем).
// Проверяет активное/любое размещение и возвращает человекочитаемые находки
// «кто/где/что» для UI («Проверить СанПиН», группа «Нормы СанПиН» в «Почему так»).
// Все числовые нормы — NEEDS-CHECK (точный текст №206/№35+№525 не выверен):
// каждая находка несёт флаг, UI показывает бейдж «требует сверки с НПА».
// Лимиты — данные (SanPinLimits): школа/эксперт перенастраивает без кода.

/// <summary>Лимиты СанПиН-РБ (NEEDS-CHECK; дефолты — SANPIN_RB.md §3).</summary>
public sealed record SanPinLimits(
    int Grade1Max = 5,
    int Grade24Max = 5,
    int Grade56Max = 6,
    int Grade711Max = 7,
    int Grade1FiveLessonDaysMax = 1,
    bool PeFirstLessonWarning = true,
    int TeacherWeeklyMax = 25)
{
    public static SanPinLimits Default => new();

    public int CapForGrade(int grade) =>
        grade <= 1 ? Grade1Max :
        grade <= 4 ? Grade24Max :
        grade <= 6 ? Grade56Max : Grade711Max;

    /// <summary>
    /// Недельная норма класса, ч/нед (база, без факультативов).
    /// ИСТОЧНИК: фото из школьного сборника СанПиН
    /// (данные/photo_2026-10-02_23-09-15.jpg): «Максимальная допустимая недельная
    /// учебная нагрузка», столбец «количество учебных часов».
    /// Для школ с изучением предметов на повышенном уровне — WeeklyClassAdvanced.
    /// ВНИМАНИЕ: строки 5–7 фото curled — подобрано единственным непротиворечивым
    /// набором (база ≤ макс); сверить с оригиналом при пилоте. NEEDS-CHECK.
    /// </summary>
    public static IReadOnlyDictionary<int, int> WeeklyClassBase { get; } =
        new Dictionary<int, int>
        {
            [1] = 18, [2] = 19, [3] = 22, [4] = 22, [5] = 25, [6] = 27,
            [7] = 28, [8] = 29, [9] = 29, [10] = 28, [11] = 28,
        };

    /// <summary>То же для школ с повышенным уровнем (пометка &lt;*&gt; в таблице).</summary>
    public static IReadOnlyDictionary<int, int> WeeklyClassAdvanced { get; } =
        new Dictionary<int, int>
        {
            [5] = 27, [6] = 29, [7] = 30, [8] = 31, [9] = 31, [10] = 31, [11] = 31,
        };

    /// <summary>
    /// Профильная надбавка к потолку (классы с предметами «(проф)»):
    /// 10А/11А несут ~42 ч строками (21 обычных + 21 профильных), но профили —
    /// ПАРАЛЛЕЛЬНЫЕ подгруппы (в один слот разные предметы разных профилей),
    /// каждый ученик — свою норму. Надбавка +8 — допуск на случай несинхронных
    /// пар (подтверждено школой 04.10.2026).
    /// </summary>
    public const int WeeklyProfileExtra = 8;
    public static IReadOnlyDictionary<int, int> WeeklyClassMax { get; } =
        new Dictionary<int, int>
        {
            [1] = 22, [2] = 22, [3] = 24, [4] = 24, [5] = 27, [6] = 30,
            [7] = 30, [8] = 31, [9] = 33, [10] = 34, [11] = 34,
        };
}

public sealed record SanPinFinding(bool IsError, string Text, bool NeedsCheck);

public static class SanPinChecker
{
    public static IReadOnlyList<SanPinFinding> Check(
        SchedulingProblem problem,
        IReadOnlyList<PlacedLesson> placements,
        SanPinLimits? limits = null)
    {
        limits ??= SanPinLimits.Default;
        var out_ = new List<SanPinFinding>();
        var occById = problem.Occurrences.ToDictionary(o => o.Id);
        // НДТП-7: внеурочка не считается (но слот занимает).
        bool IsCounted(Guid occId) =>
            occById.TryGetValue(occId, out var o) && !o.IsExtra &&
            !(problem.Subjects.TryGetValue(o.SubjectId, out var cs) && cs.IsNonLesson);

        // Нагрузка класса по дням: distinct-слоты (подгруппы A/B в одном слоте — 1 урок дня).
        var daySlots = new Dictionary<(Guid ClassId, int Day), HashSet<int>>();
        var peFirst = new List<(string Class, int Day)>();
        var teacherHours = new Dictionary<Guid, int>();
        foreach (var p in placements)
        {
            if (!occById.TryGetValue(p.OccurrenceId, out var occ)) continue;
            if (!IsCounted(p.OccurrenceId)) continue;
            if (!daySlots.TryGetValue((occ.ClassId, p.DayIndex), out var set))
                daySlots[(occ.ClassId, p.DayIndex)] = set = [];
            set.Add(p.SlotIndex);
            teacherHours[occ.TeacherId] = teacherHours.GetValueOrDefault(occ.TeacherId) + 1;
            if (limits.PeFirstLessonWarning &&
                problem.Subjects.TryGetValue(occ.SubjectId, out var subj) &&
                subj.IsPhysicalEducation &&
                p.SlotIndex == StudentCompactness.AnchorFor(problem, occ.ClassId))
                peFirst.Add((ClassName(problem, occ.ClassId), p.DayIndex));
        }

        foreach (var ((classId, day), set) in daySlots
                     .OrderBy(kv => ClassName(problem, kv.Key.ClassId))
                     .ThenBy(kv => kv.Key.Day))
        {
            if (!problem.Classes.TryGetValue(classId, out var cls)) continue;
            int cap = limits.CapForGrade(cls.Grade);
            if (set.Count > cap)
                out_.Add(new SanPinFinding(true,
                    $"Класс {cls.Name}, {ScheduleExcelExporter.DayName(day)}: " +
                    $"{set.Count} уроков — больше нормы {cap}/день (СанПиН: требует сверки с НПА).",
                    NeedsCheck: true));
        }

        // 1-е классы: дней с 5 уроками — не более 1 в неделю.
        foreach (var g in daySlots.GroupBy(kv => kv.Key.ClassId))
        {
            if (!problem.Classes.TryGetValue(g.Key, out var cls) || cls.Grade != 1) continue;
            int fiveDays = g.Count(kv => kv.Value.Count >= 5);
            if (fiveDays > limits.Grade1FiveLessonDaysMax)
                out_.Add(new SanPinFinding(true,
                    $"Класс {cls.Name}: дней с 5 уроками — {fiveDays}, норма — не более " +
                    $"{limits.Grade1FiveLessonDaysMax} в неделю (СанПиН: требует сверки с НПА).",
                    NeedsCheck: true));
        }

        foreach (var (cls, day) in peFirst.Distinct().OrderBy(x => x.Class).ThenBy(x => x.Day))
            out_.Add(new SanPinFinding(false,
                $"Класс {cls}, {ScheduleExcelExporter.DayName(day)}: физкультура первым уроком " +
                "— лучше не ставить (пожелание, требует сверки с НПА).",
                NeedsCheck: true));

        // НДТП-7: недельная норма учителя 25 ч (без внеурочки).
        foreach (var (tid, hours) in teacherHours.OrderBy(kv =>
                     problem.Teachers.TryGetValue(kv.Key, out var t) ? t.Name : "?"))
        {
            if (hours <= limits.TeacherWeeklyMax) continue;
            string tname = problem.Teachers.TryGetValue(tid, out var t) ? t.Name : "?";
            out_.Add(new SanPinFinding(true,
                $"Учитель {tname}: {hours} ч/нед — больше нормы {limits.TeacherWeeklyMax} ч/нед " +
                "(перегруз, требует решения завуча).",
                NeedsCheck: false));
        }

        // Фото СанПиН 02.10: недельная нагрузка класса — база (без факультативов)
        // и потолок (с факультативами). Внеурочка в базу не входит, в потолок — да.
        var weekCounted = new Dictionary<Guid, HashSet<(int Day, int Slot)>>();
        var weekTotal = new Dictionary<Guid, HashSet<(int Day, int Slot)>>();
        foreach (var p in placements)
        {
            if (!occById.TryGetValue(p.OccurrenceId, out var occ)) continue;
            if (!weekTotal.TryGetValue(occ.ClassId, out var all))
                weekTotal[occ.ClassId] = all = [];
            all.Add((p.DayIndex, p.SlotIndex));
            if (!IsCounted(p.OccurrenceId)) continue;
            if (!weekCounted.TryGetValue(occ.ClassId, out var cnt))
                weekCounted[occ.ClassId] = cnt = [];
            cnt.Add((p.DayIndex, p.SlotIndex));
        }
        foreach (var (classId, all) in weekTotal.OrderBy(kv => ClassName(problem, kv.Key)))
        {
            if (!problem.Classes.TryGetValue(classId, out var cls)) continue;
            int counted = weekCounted.TryGetValue(classId, out var cnt) ? cnt.Count : 0;
            if (SanPinLimits.WeeklyClassBase.TryGetValue(cls.Grade, out int norm) && counted > norm)
                out_.Add(new SanPinFinding(false,
                    $"Класс {cls.Name}: {counted} ч/нед — выше базовой нормы {norm} ч/нед " +
                    "(план и СанПиН расходятся — сверить с завучем).",
                    NeedsCheck: true));
            // Профиль (04.10.2026, подтверждено школой): 10А/11А — 21 обычных +
            // 21 профильных = норма для них; потолок +8 к стандартному.
            bool profile = problem.Occurrences.Any(o => o.ClassId == classId &&
                problem.Subjects.TryGetValue(o.SubjectId, out var ps) &&
                ps.Name.Contains("(проф)"));
            int max = 0;
            bool hasMax = SanPinLimits.WeeklyClassMax.TryGetValue(cls.Grade, out max);
            if (profile) max += SanPinLimits.WeeklyProfileExtra;
            if (hasMax && all.Count > max)
                out_.Add(new SanPinFinding(true,
                    $"Класс {cls.Name}: {all.Count} ч/нед с внеурочкой — выше потолка {max} ч/нед.",
                    NeedsCheck: true));
        }

        return out_;
    }

    private static string ClassName(SchedulingProblem problem, Guid classId) =>
        problem.Classes.TryGetValue(classId, out var c) ? c.Name : "?";
}
