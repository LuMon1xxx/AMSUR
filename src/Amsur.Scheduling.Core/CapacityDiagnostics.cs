using System.Globalization;
using Amsur.Domain;

namespace Amsur.Scheduling.Core;

// Почему расписания нет — арифметика, а не догадки.
// Проверяет, влезает ли учебный план в доступные слоты: у класса N уроков в неделю,
// а помещается не больше (лимит уроков в день × число дней). При перегрузе задача
// неразрешима ни одним солвером, и пользователь должен видеть цифры, а не «нет вариантов».
public static class CapacityDiagnostics
{
    public const int MaxLines = 6;

    public static IReadOnlyList<string> Analyze(
        SchedulingProblem problem, EffectiveRuleSet? rules = null)
    {
        var rs = rules ?? EffectiveRuleSet.Default;
        bool ClassLimitRelaxed = rs.RelaxedStrict.Contains("class-maxperday");
        bool TeacherLimitRelaxed = rs.RelaxedStrict.Contains("teacher-maxperday");
        int days = Math.Max(1, problem.DaysCount);
        int slots = Math.Max(1, problem.SlotsPerDay);

        // Внеурочка и предметы-не-уроки ёмкость класса не тратят (НДТП-7).
        bool Counted(LessonOccurrence o) =>
            !o.IsExtra &&
            !(problem.Subjects.TryGetValue(o.SubjectId, out var s) && s.IsNonLesson);

        var perClass = new Dictionary<Guid, int>();
        var perTeacher = new Dictionary<Guid, int>();
        foreach (var o in problem.Occurrences)
        {
            if (!Counted(o)) continue;
            perClass[o.ClassId] = perClass.GetValueOrDefault(o.ClassId) + 1;
            if (o.TeacherId != Guid.Empty)
                perTeacher[o.TeacherId] = perTeacher.GetValueOrDefault(o.TeacherId) + 1;
        }

        var lines = new List<(int Over, string Text)>();

        if (!ClassLimitRelaxed && problem.Classes.Count > 0)
            foreach (var (id, need) in perClass.OrderByDescending(kv => kv.Value))
            {
                if (!problem.Classes.TryGetValue(id, out var c)) continue;
                int limit = Math.Min(slots, Math.Max(1, c.MaxLessonsPerDay));
                int cap = limit * days;
                if (need <= cap) continue;
                lines.Add((need - cap,
                    $"{c.Name}: уроков в неделю {need}, помещается не больше {cap} " +
                    $"({limit} в день × {days} дн.). Уберите {need - cap} ч или поднимите лимит/добавьте день."));
            }

        if (!TeacherLimitRelaxed && problem.Teachers.Count > 0)
            foreach (var (id, need) in perTeacher.OrderByDescending(kv => kv.Value))
            {
                if (!problem.Teachers.TryGetValue(id, out var t)) continue;
                int limit = Math.Min(slots, Math.Max(1, t.MaxLessonsPerDay));
                int cap = limit * days;
                if (need <= cap) continue;
                lines.Add((need - cap,
                    $"Учитель {t.Name}: часов в неделю {need}, помещается не больше {cap} " +
                    $"({limit} в день × {days} дн.). Разгрузите на {need - cap} ч."));
            }

        return lines
            .OrderByDescending(x => x.Over)
            .Take(MaxLines)
            .Select(x => x.Text)
            .ToList();
    }

    // Готовая подпись для пустого результата генерации.
    public static string? EmptyReason(SchedulingProblem problem, EffectiveRuleSet? rules = null)
    {
        var lines = Analyze(problem, rules);
        if (lines.Count == 0) return null;
        return "Расписание не помещается в учебную неделю — " +
            string.Join(" ", lines) +
            " Без этого ни один режим не найдёт вариант.";
    }
}
