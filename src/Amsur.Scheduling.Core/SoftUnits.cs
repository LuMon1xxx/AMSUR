namespace Amsur.Scheduling.Core;

using Amsur.Domain;

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

    // D-51 / decision-teacher-gaps §1: разбросанный сдвоенный урок дня.
    // Единица K=(classId, subjectId, dayIndex), только eligible:
    // GroupId==null && SyncGroupId==null && InScope (v1: все eligible в скоупе;
    // предикат isInScope — крючок v2 под предметный вес, в формуле v1 не участвует).
    // units(K) = 1 iff eligCount==2 && distinct==2 && |s1-s2|>1 else 0.
    // Инварианты: INV-D1 eligibility (сплиты/синхрон исключены), INV-D2 count==2
    // (тройки 0), INV-D3 distinct==2 (один слот дважды 0), INV-D4 аддитивность
    // с subject-maxperday (никаких guards — оба терма считают независимо),
    // INV-D8 только GradeW (учителя/кабинеты не взвешиваются).
    public static bool IsDoubleEligible(
        LessonOccurrence occ, Func<LessonOccurrence, bool>? isInScope = null) =>
        occ.GroupId is null && occ.SyncGroupId is null &&
        (isInScope?.Invoke(occ) ?? true);

    /// <summary>Ядро единицы дубля: ровно 2 eligible-слота в 2 distinct несмежных.</summary>
    public static int DoublesUnits(IReadOnlyList<int> eligibleSlots)
    {
        if (eligibleSlots.Count != 2) return 0; // INV-D2: тройки (и одиночки) — 0
        if (eligibleSlots[0] == eligibleSlots[1]) return 0; // INV-D3: один слот дважды — 0
        return Math.Abs(eligibleSlots[0] - eligibleSlots[1]) > 1 ? 1 : 0;
    }

    /// <summary>Единицы разбросанных дублей по всем K (без весов; вес × GradeW — у вызывателя).</summary>
    public static int DoublesScattered(
        IEnumerable<(LessonOccurrence Occ, int Day, int Slot)> lessons,
        Func<LessonOccurrence, bool>? isInScope = null)
    {
        int units = 0;
        foreach (var g in lessons.GroupBy(x => (x.Occ.ClassId, x.Occ.SubjectId, x.Day)))
            units += DoublesUnits(g
                .Where(x => IsDoubleEligible(x.Occ, isInScope))
                .Select(x => x.Slot).ToList());
        return units;
    }
}
