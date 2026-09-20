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
    bool PeFirstLessonWarning = true)
{
    public static SanPinLimits Default => new();

    public int CapForGrade(int grade) =>
        grade <= 1 ? Grade1Max :
        grade <= 4 ? Grade24Max :
        grade <= 6 ? Grade56Max : Grade711Max;
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

        // Нагрузка класса по дням: distinct-слоты (подгруппы A/B в одном слоте — 1 урок дня).
        var daySlots = new Dictionary<(Guid ClassId, int Day), HashSet<int>>();
        var peFirst = new List<(string Class, int Day)>();
        foreach (var p in placements)
        {
            if (!occById.TryGetValue(p.OccurrenceId, out var occ)) continue;
            if (!daySlots.TryGetValue((occ.ClassId, p.DayIndex), out var set))
                daySlots[(occ.ClassId, p.DayIndex)] = set = [];
            set.Add(p.SlotIndex);
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

        return out_;
    }

    private static string ClassName(SchedulingProblem problem, Guid classId) =>
        problem.Classes.TryGetValue(classId, out var c) ? c.Name : "?";
}
