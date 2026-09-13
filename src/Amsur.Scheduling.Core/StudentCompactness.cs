namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// Компактность дня ученика (SANPIN_RB.md §5, D-28): внутренние окна запрещены,
// старт — не позже anchor+1 (якорь смены: min AllowedSlots класса, обычно 1 или 8),
// дневной максимум класса — HARD.
// Якорь выводится из задачи (без новых полей модели): min слотов класса.
public static class StudentCompactness
{
    /// <summary>Якорь смены класса: минимальный допустимый слот (1-я смена → 1, 2-я → 8).</summary>
    public static int AnchorFor(SchedulingProblem problem, Guid classId)
    {
        int anchor = int.MaxValue;
        foreach (var occ in problem.Occurrences)
        {
            if (occ.ClassId != classId) continue;
            if (problem.AllowedSlots.TryGetValue(occ.Id, out var slots))
                foreach (int s in slots)
                    if (s < anchor) anchor = s;
        }
        return anchor == int.MaxValue ? 1 : anchor;
    }

    /// <summary>Внутренние окна: (last-first+1)-count по DISTINCT-слотам
    /// (сплит-пары делят слот — дубликаты не должны схлопывать окна, D-28d).</summary>
    public static int GapOf(IReadOnlyList<int> sortedSlots)
    {
        if (sortedSlots.Count <= 1) return 0;
        var d = sortedSlots.Distinct().OrderBy(s => s).ToList();
        if (d.Count <= 1) return 0;
        int gap = (d[^1] - d[0] + 1) - d.Count;
        return gap > 0 ? gap : 0;
    }

    /// <summary>Поздний старт сверх бесплатного (anchor, anchor+1): слотов опоздания.</summary>
    public static int LateExcess(IReadOnlyList<int> sortedSlots, int anchor)
    {
        if (sortedSlots.Count == 0) return 0;
        int late = sortedSlots[0] - (anchor + 1);
        return late > 0 ? late : 0;
    }

    public static long LatePenalty(IReadOnlyList<int> sortedSlots, int anchor) =>
        LatePenalty(sortedSlots, anchor, RuleCatalog.StudentLateStart);

    public static long LatePenalty(IReadOnlyList<int> sortedSlots, int anchor, long weight) =>
        (long)LateExcess(sortedSlots, anchor) * weight;
}
