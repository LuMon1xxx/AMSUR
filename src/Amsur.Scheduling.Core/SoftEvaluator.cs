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
        long wActiveDay = rules.Weight("teacher-active-day");
        long wDoubles = rules.Weight("doubles-adjacency");
        long wSubj = rules.Weight("subject-maxperday");
        long wCrowd = rules.Weight("room-crowding");
        long wSplit = rules.Weight("teacher-split");
        long wHeavy = rules.Weight("heavy-edge");
        long wPeak = rules.Weight("peak-days");
        long wEdgeOnce = rules.Weight("edge-once");
        long wAlternation = rules.Weight("alternation");
        long wPe = rules.Weight("pe-consecutive");
        // НДТП-7: внеурочка не считается (но занимает слот — коллизии в валидаторе).
        bool IsCounted(Guid occId) =>
            occById.TryGetValue(occId, out var o) && !o.IsExtra &&
            !(problem.Subjects.TryGetValue(o.SubjectId, out var s) && s.IsNonLesson);
        // R8: вес параллели класса (1 при выключенном приоритете / Neutral).
        int GradeW(Guid classId) =>
            problem.Flex.WeightForGrade(
                problem.Classes.TryGetValue(classId, out var c) ? c.Grade : 0);
        bool IsHeavy(Guid subjectId) =>
            problem.Subjects.TryGetValue(subjectId, out var s) &&
            s.Difficulty >= problem.Flex.IsHeavyThreshold;

        // Student gaps: max(0, last-first+1-cnt) по классу (whole+subgroups вместе).
        // HARD-гейт в PlacementValidator (D-28); здесь soft-градиент для LS (вес 100).
        // НДТП-7: внеурочка исключена из компактности (край дня, окном не считается).
        var counted = placements.Where(p => IsCounted(p.OccurrenceId)).ToList();
        foreach (var g in counted.GroupBy(p => occById[p.OccurrenceId].ClassId))
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
        // D-50 teacher-active-day: цена занятого учителе-дня (bin-packing-давление:
        // сшить нагрузку в меньшее число дней). Дефолт 0 = не считается.
        int activeDays = 0;
        foreach (var g in counted.GroupBy(p => occById[p.OccurrenceId].TeacherId))
        {
            activeDays += g.Select(p => p.DayIndex).Distinct().Count();
            foreach (var day in g.GroupBy(p => p.DayIndex))
            {
                var slots = day.Select(p => p.SlotIndex).OrderBy(s => s).ToList();
                if (slots.Count <= 1) continue;
                var (ord, cross, _) = GapUtils.SplitTeacherDay(slots, problem.ShiftBands);
                if (ord > 0) comps["teacher-gap"] += ord * wOrdinary;
                if (cross > 0) comps["teacher-cross-shift-gap"] += cross * wCross;
            }
        }
        if (wActiveDay != 0 && activeDays > 0)
            comps["teacher-active-day"] += (long)activeDays * wActiveDay;

        // Subject maxperday (× вес параллели, R8). НДТП-7: внеурочка не в счёт.
        foreach (var g in counted.GroupBy(p => (occById[p.OccurrenceId].ClassId, occById[p.OccurrenceId].SubjectId, p.DayIndex)))
        {
            if (problem.Subjects.TryGetValue(occById[g.First().OccurrenceId].SubjectId, out var subj)
                && g.Count() > subj.MaxPerDay)
                comps["subject-maxperday"] += (g.Count() - subj.MaxPerDay) * wSubj * GradeW(g.Key.ClassId);
        }

        // D-51 doubles-adjacency: разбросанный дубль дня (× вес параллели, R8/INV-D8).
        // INV-D4: аддитивен с subject-maxperday без guards — оба терма считают независимо.
        if (wDoubles != 0)
            foreach (var g in counted.GroupBy(p => (occById[p.OccurrenceId].ClassId, occById[p.OccurrenceId].SubjectId, p.DayIndex)))
            {
                int units = SoftUnits.DoublesScattered(g.Select(p => (occById[p.OccurrenceId], p.DayIndex, p.SlotIndex)));
                if (units > 0)
                    comps["doubles-adjacency"] += (long)units * wDoubles * GradeW(g.Key.ClassId);
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

        // НДТП-7: пик Вт/Ср/Пт — тяжёлый урок вне пика (× вес параллели).
        if (wPeak != 0)
            foreach (var p in counted)
            {
                var occ = occById[p.OccurrenceId];
                if (IsHeavy(occ.SubjectId))
                    comps["peak-days"] += SoftUnits.PeakOutside(p.DayIndex, true) * wPeak * GradeW(occ.ClassId);
            }

        // НДТП-7: край 1 раз/нед — 7 предметов на краю дня (× вес параллели).
        if (wEdgeOnce != 0)
        {
            var edgeSlots = new HashSet<(Guid ClassId, Guid SubjectId, int Day, int Slot)>();
            foreach (var day in counted.GroupBy(p => (occById[p.OccurrenceId].ClassId, p.DayIndex)))
            {
                var slots = day.Select(p => p.SlotIndex).Distinct().OrderBy(s => s).ToList();
                if (slots.Count == 0) continue;
                int first = slots[0], last = slots[^1];
                foreach (var p in day)
                {
                    if (p.SlotIndex != first && (slots.Count <= 1 || p.SlotIndex != last)) continue;
                    var occ = occById[p.OccurrenceId];
                    if (!problem.Subjects.TryGetValue(occ.SubjectId, out var subj)) continue;
                    if (!SoftUnits.IsEdgeOnceSubject(subj)) continue;
                    edgeSlots.Add((occ.ClassId, occ.SubjectId, p.DayIndex, p.SlotIndex));
                }
            }
            foreach (var g in edgeSlots.GroupBy(e => (e.ClassId, e.SubjectId)))
                comps["edge-once"] += SoftUnits.EdgeOnceExcess(g.Count()) * wEdgeOnce * GradeW(g.Key.ClassId);
        }

        // НДТП-7: чередование — соседние слоты одинаковой тяжести (× вес параллели).
        if (wAlternation != 0)
            foreach (var day in counted.GroupBy(p => (occById[p.OccurrenceId].ClassId, p.DayIndex)))
            {
                var bySlot = day.GroupBy(p => p.SlotIndex)
                    .OrderBy(sg => sg.Key)
                    .Select(sg => sg.Any(p => IsHeavy(occById[p.OccurrenceId].SubjectId)))
                    .ToList();
                if (bySlot.Count <= 1) continue;
                comps["alternation"] += SoftUnits.AlternationBreaks(bySlot) * wAlternation * GradeW(day.Key.ClassId);
            }

        // PE-видимость: тройки физры подряд у класса (× вес параллели).
        // Зеркало Hard-гейта валидатора (там же relaxed-код sanpin-pe-spacing),
        // чтобы поиск чинил то, что гейт запрещает.
        if (wPe != 0)
        {
            bool IsPe(Guid occId) =>
                occById.TryGetValue(occId, out var o) &&
                problem.Subjects.TryGetValue(o.SubjectId, out var ps) && ps.IsPhysicalEducation;
            foreach (var g in counted.Where(p => IsPe(p.OccurrenceId))
                         .GroupBy(p => occById[p.OccurrenceId].ClassId))
                comps["pe-consecutive"] += SoftUnits.PeRuns(
                    g.Select(p => p.DayIndex).ToList()) * wPe * GradeW(g.Key);
        }
        // R7-Soft teacher-split: лишние учителя на (класс,предмет) среди целых
        // (сплит-половины GroupId!=null — штатно два учителя, исключены).
        // Report-only: учителя зафиксированы occurrence (INV-02), ходы времени состав
        // не меняют — на выбор вариантов не влияет, только строка в оценке.
        // Режим Off = «не волнует» → 0. НДТП-7: внеурочка исключена.
        if (problem.Flex.AssignMode != Amsur.Domain.TeacherAssignMode.Off)
        {
            foreach (var g in counted
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
