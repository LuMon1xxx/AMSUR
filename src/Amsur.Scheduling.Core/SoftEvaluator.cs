namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// Quality engine: scalar + stable breakdown.
// Окна: student-gap 100 (HARD-gate в validator, здесь soft-градиент для LS) >>
// teacher ordinary 10 + cross-shift 2 (S5: межсменка дешевле, видна отдельно).
public static class SoftEvaluator
{
    public static PenaltyBreakdown Evaluate(SchedulingProblem problem, IReadOnlyList<PlacedLesson> placements) =>
        Evaluate(problem, placements, EffectiveRuleSet.Default);

    public static PenaltyBreakdown Evaluate(
        SchedulingProblem problem, IReadOnlyList<PlacedLesson> placements, EffectiveRuleSet rules)
    {
        var occById = problem.Occurrences.ToDictionary(o => o.Id);
        var comps = new Dictionary<string, long>();
        foreach (var code in RuleCatalog.AllCodes) comps[code] = 0;
        long wStudentGap = rules.Weight("student-gap");
        long wLate = rules.Weight("student-late-start");
        long wOrdinary = rules.Weight("teacher-gap");
        long wCross = rules.Weight("teacher-cross-shift-gap");
        long wSubj = rules.Weight("subject-maxperday");

        // Student gaps: max(0, last-first+1-cnt) по классу (whole+subgroups вместе).
        // HARD-гейт в PlacementValidator (D-28); здесь soft-градиент для LS (вес 100).
        foreach (var g in placements.GroupBy(p => occById[p.OccurrenceId].ClassId))
        {
            int anchor = StudentCompactness.AnchorFor(problem, g.Key);
            foreach (var day in g.GroupBy(p => p.DayIndex))
            {
                // DISTINCT-слоты: сплит-пары делят слот (D-28d).
                var slots = day.Select(p => p.SlotIndex).Distinct().OrderBy(s => s).ToList();
                if (slots.Count <= 1)
                {
                    // Одинокий урок: окон нет, но поздний старт считается ниже.
                    if (slots.Count == 1)
                        comps["student-late-start"] += StudentCompactness.LatePenalty(slots, anchor);
                    continue;
                }
                var gap = (slots[^1] - slots[0] + 1) - slots.Count;
                if (gap > 0) comps["student-gap"] += gap * wStudentGap;
                comps["student-late-start"] += StudentCompactness.LatePenalty(slots, anchor, wLate);
            }
        }

        // Teacher gaps: split ordinary (внутри смен) / cross-shift (между сменами, S5).
        foreach (var g in placements.GroupBy(p => occById[p.OccurrenceId].TeacherId))
        {
            foreach (var day in g.GroupBy(p => p.DayIndex))
            {
                var slots = day.Select(p => p.SlotIndex).OrderBy(s => s).ToList();
                if (slots.Count <= 1) continue;
                var (ord, cross, _) = GapUtils.SplitTeacherDay(slots, problem.ShiftBands);
                if (ord > 0) comps["teacher-gap"] += ord * wOrdinary;
                if (cross > 0) comps["teacher-cross-shift-gap"] += cross * wCross;
            }
        }

        // Subject maxperday.
        foreach (var g in placements.GroupBy(p => (occById[p.OccurrenceId].ClassId, occById[p.OccurrenceId].SubjectId, p.DayIndex)))
        {
            if (problem.Subjects.TryGetValue(occById[g.First().OccurrenceId].SubjectId, out var subj)
                && g.Count() > subj.MaxPerDay)
                comps["subject-maxperday"] += (g.Count() - subj.MaxPerDay) * wSubj;
        }

        var total = comps.Values.Sum();
        return new PenaltyBreakdown
        {
            Total = total,
            Components = comps.Select(kv => new PenaltyComponent { Code = kv.Key, Value = kv.Value }).ToList()
        };
    }

    public static long Delta(PenaltyBreakdown before, PenaltyBreakdown after) => after.Total - before.Total;
}
