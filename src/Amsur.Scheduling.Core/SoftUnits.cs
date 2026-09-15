namespace Amsur.Scheduling.Core;

// P2: чистые функции единиц soft-штрафов (без весов). Единый источник для
// SoftEvaluator (полный пересчёт) и QualityExplainer.CountUnits — паритет по построению.
// SearchIndex.SoftDelta зеркалит вручную (инкрементально) + паритет-тесты.
public static class SoftUnits
{
    /// <summary>Тяжёлые на краю дня (R6): первый или последний distinct-слот дня тяжёлый.</summary>
    public static int HeavyEdge(IReadOnlyList<int> slots, Func<int, bool> isHeavy)
    {
        var d = slots.Distinct().OrderBy(s => s).ToList();
        if (d.Count == 0) return 0;
        int u = 0;
        if (isHeavy(d[0])) u++;
        if (d.Count > 1 && isHeavy(d[^1])) u++;
        return u;
    }

    /// <summary>Теснота (R5): единицы сверх «желательно».</summary>
    public static int Crowding(int units, int desired) => Math.Max(0, units - desired);

    /// <summary>Разрыв закрепления (R7-Soft): лишние учителя сверх одного.</summary>
    public static int Split(int distinctTeachers) => Math.Max(0, distinctTeachers - 1);
}
