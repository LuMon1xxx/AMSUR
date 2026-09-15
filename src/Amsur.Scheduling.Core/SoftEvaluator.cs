namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// Quality engine: scalar + stable breakdown.
// Окна: student-gap 100 (HARD-gate в validator, здесь soft-градиент для LS) >>
// teacher ordinary 10 + cross-shift 2 (S5: межсменка дешевле, видна отдельно).
// P2/R1–R9: student-компоненты (gap/late/subj-double) и heavy-edge взвешиваются
// по параллели (R8: 11-е ×3, 9-е ×2 — вес класса, не учителя); room-crowding (R5)
// и teacher-gap — невзвешенные (общий ресурс).
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
        long wCrowd = rules.Weight("room-crowding");
        long wSplit = rules.Weight("teacher-split");
        long wHeavy = rules.Weight("heavy-edge");
        // R8: вес параллели класса (1 при выключенном приоритете / Neutral).
        int GradeW(Guid classId) =>
            problem.Flex.WeightForGrade(
                problem.Classes.TryGetValue(classId, out var c) ? c.Grade : 0);
        bool IsHeavy(Guid subjectId) =>
            problem.Subjects.TryGetValue(subjectId, out var s) &&
            s.Difficulty >= problem.Flex.IsHeavyThreshold;

        // Student gaps: max(0, last-first+1-cnt) по классу (whole+subgroups вместе).
        // HARD-гейт в PlacementValidator (D-28); здесь soft-градиент для LS (вес 100).
        foreach (var g in placements.GroupBy(p => occById[p.OccurrenceId].ClassId))
        {
            int gw = GradeW(g.Key);
            int anchor = StudentCompactness.AnchorFor(problem, g.Key);
            foreach (var day in g.GroupBy(p => p.DayIndex))
            {
                // DISTINCT-слоты: сплит-пары делят слот (D-28d).
                var slots = day.Select(p => p.SlotIndex).Distinct().OrderBy(s => s).ToList();
                if (slots.Count <= 1)
                {
                    // Одинокий урок: окон нет, но поздний старт считается ниже.
                    if (slots.Count == 1)
                        comps["student-late-start"] += StudentCompactness.LatePenalty(slots, anchor) * gw;
                    // R6: одинокий тяжёлый урок — тоже край дня.
                    if (slots.Count == 1 && day.Any(p => IsHeavy(occById[p.OccurrenceId].SubjectId)))
                        comps["heavy-edge"] += wHeavy * gw;
                    continue;
                }
                var gap = (slots[^1] - slots[0] + 1) - slots.Count;
                if (gap > 0) comps["student-gap"] += gap * wStudentGap * gw;
                comps["student-late-start"] += StudentCompactness.LatePenalty(slots, anchor, wLate) * gw;
                // R6: тяжёлый на первом/последнем слоте дня.
                var heavyAt = day.GroupBy(p => p.SlotIndex)
                    .ToDictionary(sg => sg.Key,
                        sg => sg.Any(p => IsHeavy(occById[p.OccurrenceId].SubjectId)));
                comps["heavy-edge"] += SoftUnits.HeavyEdge(slots, s => heavyAt[s]) * wHeavy * gw;
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

        // Subject maxperday (× вес параллели, R8).
        foreach (var g in placements.GroupBy(p => (occById[p.OccurrenceId].ClassId, occById[p.OccurrenceId].SubjectId, p.DayIndex)))
        {
            if (problem.Subjects.TryGetValue(occById[g.First().OccurrenceId].SubjectId, out var subj)
                && g.Count() > subj.MaxPerDay)
                comps["subject-maxperday"] += (g.Count() - subj.MaxPerDay) * wSubj * GradeW(g.Key.ClassId);
        }

        // R5 room-crowding: единицы сверх «желательно» (флаг-aware через RoomPolicy).
        // Невзвешенный: кабинет — общий ресурс (как окна учителей).
        foreach (var g in placements.Where(p => p.RoomId.HasValue).GroupBy(p => (p.RoomId!.Value, p.DayIndex, p.SlotIndex)))
        {
            if (!problem.Rooms.TryGetValue(g.Key.Value, out var room)) continue;
            int distinctClasses = g.Select(p => occById[p.OccurrenceId].ClassId).Distinct().Count();
            int units = RoomPolicy.CellUnits(room, g.Count(), distinctClasses);
            int over = SoftUnits.Crowding(units, RoomPolicy.EffectiveDesired(room));
            if (over > 0) comps["room-crowding"] += over * wCrowd;
        }

        // R7-Soft teacher-split: лишние учителя на (класс,предмет) среди целых
        // (сплит-половины GroupId!=null — штатно два учителя, исключены).
        // Report-only: учителя зафиксированы occurrence (INV-02), ходы времени состав
        // не меняют — на выбор вариантов не влияет, только строка в оценке.
        // Режим Off = «не волнует» → 0.
        if (problem.Flex.AssignMode != Amsur.Domain.TeacherAssignMode.Off)
        {
            foreach (var g in placements
                         .Where(p => occById[p.OccurrenceId].GroupId is null)
                         .GroupBy(p => (occById[p.OccurrenceId].ClassId, occById[p.OccurrenceId].SubjectId)))
            {
                int extra = SoftUnits.Split(g.Select(p => occById[p.OccurrenceId].TeacherId).Distinct().Count());
                if (extra > 0) comps["teacher-split"] += extra * wSplit * GradeW(g.Key.ClassId);
            }
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
