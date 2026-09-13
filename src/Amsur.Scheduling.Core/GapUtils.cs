namespace Amsur.Scheduling.Core;

// Единая арифметика разрывов (S5 decision): DISTINCT везде, один источник истины для
// SoftEvaluator / SearchIndex / IncrementalEvaluator / QualityExplainer.
// Слоты 1-based (1..SlotsPerDay). Смены — bands из SchedulingProblem (структурно, не из весов).
public static class GapUtils
{
    /// <summary>Окна: (last-first+1)-count по DISTINCT-слотам, 0 при &lt;=1 distinct.</summary>
    public static int GapOf(IReadOnlyList<int> slots)
    {
        if (slots.Count <= 1) return 0;
        var d = slots.Distinct().OrderBy(s => s).ToList();
        if (d.Count <= 1) return 0;
        int gap = (d[^1] - d[0] + 1) - d.Count;
        return gap > 0 ? gap : 0;
    }

    /// <summary>
    /// Расклад teacher-day: ordinary (окна внутри смен) + crossUnits (кусок разрыва,
    /// висящий на границе смен) + isCrossDay (уроки в обеих сменах).
    /// bands пуст/одна полоса → (total, 0, false) — старое поведение.
    /// </summary>
    public static (int Ordinary, int CrossUnits, bool IsCrossDay) SplitTeacherDay(
        IReadOnlyList<int> slots, IReadOnlyList<ShiftBand>? bands)
    {
        var d = slots.Distinct().OrderBy(s => s).ToList();
        if (d.Count <= 1) return (0, 0, false);
        int total = GapOf(d);
        if (bands is null || bands.Count < 2) return (total, 0, false);
        int ord = 0, hit = 0;
        for (int i = 0; i < bands.Count; i++)
        {
            var seg = d.Where(s => s >= bands[i].FromSlot && s <= bands[i].ToSlot).ToList();
            if (seg.Count > 0) hit++;
            ord += GapOf(seg);
        }
        bool isCross = hit > 1;
        int cross = isCross ? total - ord : 0;
        if (cross < 0) cross = 0;
        return (ord, cross, isCross);
    }

    /// <summary>Индекс смены слота в bands (−1 если вне всех полос).</summary>
    public static int ShiftIndex(int slot, IReadOnlyList<ShiftBand>? bands)
    {
        if (bands is null) return -1;
        for (int i = 0; i < bands.Count; i++)
            if (slot >= bands[i].FromSlot && slot <= bands[i].ToSlot) return i;
        return -1;
    }
    /// <summary>Счётчик дней с работой в две смены (диагностика для объяснимости, не штраф).</summary>
    public static int CountCrossShiftDays(IEnumerable<IReadOnlyList<int>> teacherDays, IReadOnlyList<ShiftBand>? bands)
    {
        int n = 0;
        foreach (var slots in teacherDays)
        {
            var (_, _, isCross) = SplitTeacherDay(slots, bands);
            if (isCross) n++;
        }
        return n;
    }
}
