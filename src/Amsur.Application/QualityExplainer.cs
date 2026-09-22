using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Application;

// E5 — объяснимость качества (слой вокруг E4, не вместо него).
// Источник истины: PenaltyBreakdown кандидата (SoftEvaluator) + имена из SchedulingProblem.
// Честность:
// - объясняет ТОЛЬКО то, что движок реально считает ( student-gap / teacher-gap /
//   subject-maxperday ); коды без продьюсера — общей строкой без выдуманных причин;
// - sanpin-* при value>0 — только с пометкой «требует сверки с нормами» (D-11);
// - никогда не пишет «оптимально/идеально/невозможно/нормативно» (проверено тестами);
// - жёсткие нарушения здесь НЕ объясняются (разделение hard/soft: hard — валидатор,
//   карточка показывает их отдельной строкой E4);
// - словарь пользовательский: без CP-SAT/Inсumbent/proxy/fingerprint/solver/seed.

public static class QualityExplainer
{
    public static string HumanName(string code) => code switch
    {
        "student-gap" => "Окна у учеников",
        "student-late-start" => "Позднее начало дня у учеников",
        "teacher-gap" => "Окна у учителей",
        "teacher-cross-shift-gap" => "Перерывы между сменами у учителей",
        "teacher-active-day" => "Занятые дни учителей",
        "primary-early-start" => "Раннее начало у начальной школы",
        "heavy-edge" => "Тяжёлые уроки на краю дня",
        "room-preference" => "Неподходящие кабинеты",
        "room-crowding" => "Переполнение кабинетов",
        "teacher-split" => "Разрыв закрепления учителей",
        "subject-maxperday" => "Повторы предмета за день",
        "relation-violation" => "Нарушения связей уроков",
        "sanpin-peak-days" => "СанПиН: пиковые дни",
        "sanpin-heavy-edge-limit" => "СанПиН: тяжёлые на краю",
        "sanpin-pe-spacing" => "СанПиН: разнос физкультуры",
        "sanpin-doubles" => "СанПиН: сдвоенные",
        "sanpin-primary-early" => "СанПиН: ранние у начальной школы",
        _ => $"Прочее ({code})",
    };

    /// <summary>Единицы нарушений по кодам (зеркало арифметики SoftEvaluator, без весов).</summary>
    internal static Dictionary<string, long> CountUnits(
        SchedulingProblem reference, ScheduleCandidate candidate)
    {
        var placements = Resolve(reference, candidate);
        var units = RuleCatalog.AllCodes.ToDictionary(c => c, _ => 0L);

        foreach (var g in placements.GroupBy(p => p.Occ.ClassId))
            foreach (var day in g.GroupBy(p => p.Placed.DayIndex))
            {
                // DISTINCT-слоты (сплит-пары делят слот, D-28d).
                var slots = day.Select(p => p.Placed.SlotIndex).Distinct().OrderBy(s => s).ToList();
                if (slots.Count > 1)
                    units["student-gap"] += (slots[^1] - slots[0] + 1) - slots.Count;
            }

        foreach (var g in placements.GroupBy(p => p.Occ.ClassId))
        {
            int anchor = StudentCompactness.AnchorFor(reference, g.Key);
            foreach (var day in g.GroupBy(p => p.Placed.DayIndex))
            {
                var slots = day.Select(p => p.Placed.SlotIndex).OrderBy(s => s).ToList();
                units["student-late-start"] += StudentCompactness.LateExcess(slots, anchor);
            }
        }

        foreach (var g in placements.GroupBy(p => p.Occ.TeacherId))
            foreach (var day in g.GroupBy(p => p.Placed.DayIndex))
            {
                var slots = day.Select(p => p.Placed.SlotIndex).Distinct().OrderBy(s => s).ToList();
                if (slots.Count > 1)
                {
                    var (ord, cross, _) = GapUtils.SplitTeacherDay(slots, reference.ShiftBands);
                    units["teacher-gap"] += ord;
                    if (units.ContainsKey("teacher-cross-shift-gap"))
                        units["teacher-cross-shift-gap"] += cross;
                }
            }
        // D-50: занятые учителе-дни (единицы для teacher-active-day).
        if (units.ContainsKey("teacher-active-day"))
            units["teacher-active-day"] = placements
                .GroupBy(p => p.Occ.TeacherId)
                .Sum(g => g.Select(p => p.Placed.DayIndex).Distinct().Count());

        foreach (var g in placements.GroupBy(p => (p.Occ.ClassId, p.Occ.SubjectId, p.Placed.DayIndex)))
        {
            if (reference.Subjects.TryGetValue(g.First().Occ.SubjectId, out var subj)
                && g.Count() > subj.MaxPerDay)
                units["subject-maxperday"] += g.Count() - subj.MaxPerDay;
        }

        // P2/R5: теснота (единицы сверх «желательно», без весов).
        foreach (var g in placements
                     .Where(p => p.Placed.RoomId.HasValue)
                     .GroupBy(p => (p.Placed.RoomId!.Value, p.Placed.DayIndex, p.Placed.SlotIndex)))
        {
            if (!reference.Rooms.TryGetValue(g.Key.Value, out var room)) continue;
            int cellUnits = RoomPolicy.CellUnits(room, g.Count(),
                g.Select(p => p.Occ.ClassId).Distinct().Count());
            units["room-crowding"] += SoftUnits.Crowding(cellUnits, RoomPolicy.EffectiveDesired(room));
        }

        // P2/R6: тяжёлые на краю дня (без весов; порог из Flex).
        foreach (var g in placements.GroupBy(p => (p.Occ.ClassId, p.Placed.DayIndex)))
        {
            var slots = g.Select(p => p.Placed.SlotIndex).Distinct().OrderBy(s => s).ToList();
            bool HeavyAt(int s) => g.Where(p => p.Placed.SlotIndex == s)
                .Any(p => reference.Subjects.TryGetValue(p.Occ.SubjectId, out var subj) &&
                    subj.Difficulty >= reference.Flex.IsHeavyThreshold);
            units["heavy-edge"] += SoftUnits.HeavyEdge(slots, HeavyAt);
        }

        // P2/R7-Soft: лишние учителя на (класс,предмет) среди целых (без весов).
        if (reference.Flex.AssignMode != Amsur.Domain.TeacherAssignMode.Off)
            foreach (var g in placements
                         .Where(p => p.Occ.GroupId is null)
                         .GroupBy(p => (p.Occ.ClassId, p.Occ.SubjectId)))
                units["teacher-split"] += SoftUnits.Split(
                    g.Select(p => p.Occ.TeacherId).Distinct().Count());

        return units;
    }

    // Привязка размещений к эталонной задаче ЧЕРЕЗ StableKey (D-18): Guid occurrence
    // различаются между запусками, ключи — нет. Нераспознанные пропускаем молча
    // (чужой вход — не наша задача; строк просто будет меньше, без исключений).
    private sealed record Resolved(LessonOccurrence Occ, PlacedLesson Placed);

    private static List<Resolved> Resolve(SchedulingProblem reference, ScheduleCandidate candidate)
    {
        var refByKey = reference.Occurrences
            .GroupBy(o => o.StableKey)
            .ToDictionary(g => g.Key, g => g.First());
        var list = new List<Resolved>();
        foreach (var p in candidate.Placements)
        {
            if (!candidate.OccKeys.TryGetValue(p.OccurrenceId, out var key)) continue;
            if (!refByKey.TryGetValue(key, out var occ)) continue;
            list.Add(new Resolved(occ, p));
        }
        return list;
    }

    /// <summary>Почему такая оценка: строки по ненулевым компонентам с привязкой к месту.</summary>
    public static IReadOnlyList<string> Explain(SchedulingProblem reference, ScheduleCandidate candidate)
    {
        if (candidate.SoftTotal == 0)
            return ["Мягких нарушений нет — расписание без окон и превышений дневных норм."];

        var lines = new List<string>();

        foreach (var comp in candidate.Breakdown.Components)
        {
            if (comp.Value == 0) continue;
            switch (comp.Code)
            {
                case "student-gap":
                    lines.AddRange(StudentGapLines(reference, candidate));
                    break;
                case "student-late-start":
                    lines.AddRange(LateStartLines(reference, candidate));
                    break;
                case "teacher-gap":
                    lines.AddRange(TeacherGapLines(reference, candidate));
                    break;
                case "teacher-cross-shift-gap":
                    lines.AddRange(TeacherCrossShiftLines(reference, candidate));
                    break;
                case "subject-maxperday":
                    lines.AddRange(SubjectLines(reference, candidate));
                    break;
                case "room-crowding":
                    lines.AddRange(CrowdingLines(reference, candidate));
                    break;
                case "teacher-split":
                    lines.AddRange(SplitLines(reference, candidate));
                    break;
                case "heavy-edge":
                    lines.AddRange(HeavyLines(reference, candidate));
                    break;
                default:
                    lines.Add(OtherCodeLine(comp.Code, comp.Value));
                    break;
            }
        }

        if (lines.Count == 0)
            lines.Add($"Есть мягкие нарушения (оценка {candidate.SoftTotal}), детализация недоступна.");
        return lines;
    }

    /// <summary>Почему вариант хуже/лучше лучшего: дельты по кодам человеческим языком.</summary>
    public static IReadOnlyList<string> Compare(
        SchedulingProblem reference, ScheduleCandidate best, ScheduleCandidate other)
    {
        var bu = CountUnits(reference, best);
        var ou = CountUnits(reference, other);
        var bb = best.Breakdown.Components.ToDictionary(c => c.Code, c => c.Value);
        var ob = other.Breakdown.Components.ToDictionary(c => c.Code, c => c.Value);
        var lines = new List<string>();

        foreach (var code in RuleCatalog.AllCodes)
        {
            long db = ob.GetValueOrDefault(code) - bb.GetValueOrDefault(code);
            if (db == 0) continue;
            long du = ou.GetValueOrDefault(code) - bu.GetValueOrDefault(code);
            string dir = db > 0 ? "хуже" : "лучше";
            string detail = (code, du) switch
            {
                ("student-gap", _) when du != 0 => $"окон у учеников {(du > 0 ? "больше" : "меньше")} на {Math.Abs(du)}",
                ("student-late-start", _) when du != 0 => $"поздних начал {(du > 0 ? "больше" : "меньше")} на {Math.Abs(du)}",
                ("teacher-gap", _) when du != 0 => $"окон у учителей {(du > 0 ? "больше" : "меньше")} на {Math.Abs(du)}",
                ("teacher-cross-shift-gap", _) when du != 0 => $"перерывов между сменами {(du > 0 ? "больше" : "меньше")} на {Math.Abs(du)}",
                ("teacher-active-day", _) when du != 0 => $"занятых дней {(du > 0 ? "больше" : "меньше")} на {Math.Abs(du)}",
                ("subject-maxperday", _) when du != 0 => $"повторов {(du > 0 ? "больше" : "меньше")} на {Math.Abs(du)}",
                ("room-crowding", _) when du != 0 => $"тесноты в кабинетах {(du > 0 ? "больше" : "меньше")} на {Math.Abs(du)}",
                ("teacher-split", _) when du != 0 => $"разрывов закрепления {(du > 0 ? "больше" : "меньше")} на {Math.Abs(du)}",
                ("heavy-edge", _) when du != 0 => $"тяжёлых на краю дня {(du > 0 ? "больше" : "меньше")} на {Math.Abs(du)}",
                _ => HumanName(code).ToLowerInvariant(),
            };
            lines.Add($"Здесь {dir}: {detail} ({(db > 0 ? "+" : "")}{db} к оценке)");
        }

        if (lines.Count == 0)
            lines.Add("Та же оценка, что у варианта 1, но уроки распределены иначе.");
        return lines;
    }

    /// <summary>Короткий итог для карточки.</summary>
    public static string QualitySummary(ScheduleCandidate candidate)
    {
        if (candidate.SoftTotal == 0) return "Без мягких нарушений";
        var top = candidate.Breakdown.Components
            .Where(c => c.Value > 0)
            .MaxBy(c => c.Value);
        return top is null
            ? $"Мягкие нарушения (оценка {candidate.SoftTotal})"
            : $"Основное: {HumanName(top.Code).ToLowerInvariant()} ({top.Value})";
    }

    // --- детализация с привязкой к месту (дни 1-based для человека; слоты уже 1-based, E4) ---

    private static IEnumerable<string> StudentGapLines(
        SchedulingProblem reference, ScheduleCandidate candidate)
    {
        var placements = Resolve(reference, candidate);
        foreach (var g in placements.GroupBy(p => p.Occ.ClassId)
                     .OrderBy(g => ClassName(reference, g.Key)))
            foreach (var day in g.GroupBy(p => p.Placed.DayIndex).OrderBy(d => d.Key))
            {
                var slots = day.Select(p => p.Placed.SlotIndex).Distinct().OrderBy(s => s).ToList();
                if (slots.Count <= 1) continue;
                int gap = (slots[^1] - slots[0] + 1) - slots.Count;
                if (gap > 0)
                    yield return $"Класс {ClassName(reference, g.Key)}, день {day.Key + 1}: " +
                        $"{GapWord(gap)} (уроки {slots[0]}–{slots[^1]})";
            }
    }

    private static IEnumerable<string> LateStartLines(
        SchedulingProblem reference, ScheduleCandidate candidate)
    {
        var placements = Resolve(reference, candidate);
        foreach (var g in placements.GroupBy(p => p.Occ.ClassId)
                     .OrderBy(g => ClassName(reference, g.Key)))
        {
            int anchor = StudentCompactness.AnchorFor(reference, g.Key);
            foreach (var day in g.GroupBy(p => p.Placed.DayIndex).OrderBy(d => d.Key))
            {
                var slots = day.Select(p => p.Placed.SlotIndex).OrderBy(s => s).ToList();
                int late = StudentCompactness.LateExcess(slots, anchor);
                if (late > 0)
                    yield return $"Класс {ClassName(reference, g.Key)}, день {day.Key + 1}: " +
                        $"начинает с урока {slots[0]} (допустимо с {anchor} или {anchor + 1})";
            }
        }
    }

    private static IEnumerable<string> TeacherGapLines(
        SchedulingProblem reference, ScheduleCandidate candidate)
    {
        var placements = Resolve(reference, candidate);
        foreach (var g in placements.GroupBy(p => p.Occ.TeacherId)
                     .OrderBy(g => TeacherName(reference, g.Key)))
            foreach (var day in g.GroupBy(p => p.Placed.DayIndex).OrderBy(d => d.Key))
            {
                var slots = day.Select(p => p.Placed.SlotIndex).Distinct().OrderBy(s => s).ToList();
                if (slots.Count <= 1) continue;
                var (ord, _, _) = GapUtils.SplitTeacherDay(slots, reference.ShiftBands);
                if (ord > 0)
                    yield return $"Учитель {TeacherName(reference, g.Key)}, день {day.Key + 1}: " +
                        $"{GapWord(ord)} внутри смены (уроки {slots[0]}–{slots[^1]})";
            }
    }

    private static IEnumerable<string> TeacherCrossShiftLines(
        SchedulingProblem reference, ScheduleCandidate candidate)
    {
        var placements = Resolve(reference, candidate);
        foreach (var g in placements.GroupBy(p => p.Occ.TeacherId)
                     .OrderBy(g => TeacherName(reference, g.Key)))
            foreach (var day in g.GroupBy(p => p.Placed.DayIndex).OrderBy(d => d.Key))
            {
                var slots = day.Select(p => p.Placed.SlotIndex).Distinct().OrderBy(s => s).ToList();
                if (slots.Count <= 1) continue;
                var (_, cross, isCross) = GapUtils.SplitTeacherDay(slots, reference.ShiftBands);
                if (isCross && cross > 0)
                    yield return $"Учитель {TeacherName(reference, g.Key)}, день {day.Key + 1}: " +
                        $"перерыв между сменами (уроки {slots[0]}–{slots[^1]}, обычно неизбежен)";
                else if (isCross)
                    yield return $"Учитель {TeacherName(reference, g.Key)}, день {day.Key + 1}: " +
                        $"работа в две смены без разрыва (уроки {slots[0]}–{slots[^1]})";
            }
    }

    private static IEnumerable<string> SubjectLines(
        SchedulingProblem reference, ScheduleCandidate candidate)
    {
        var placements = Resolve(reference, candidate);
        foreach (var g in placements.GroupBy(p =>
                     (p.Occ.ClassId, p.Occ.SubjectId, p.Placed.DayIndex))
                     .OrderBy(g => g.Key.DayIndex))
        {
            var (classId, subjectId, day) = g.Key;
            if (!reference.Subjects.TryGetValue(subjectId, out var subj)) continue;
            if (g.Count() <= subj.MaxPerDay) continue;
            yield return $"Класс {ClassName(reference, classId)}, {subj.Name}: " +
                $"{LessonWord(g.Count())} в день {day + 1} (норма {subj.MaxPerDay})";
        }
    }

    // P2: теснота с привязкой к кабинету и дню.
    private static IEnumerable<string> CrowdingLines(
        SchedulingProblem reference, ScheduleCandidate candidate)
    {
        var placements = Resolve(reference, candidate);
        foreach (var g in placements
                     .Where(p => p.Placed.RoomId.HasValue)
                     .GroupBy(p => (p.Placed.RoomId!.Value, p.Placed.DayIndex, p.Placed.SlotIndex))
                     .OrderBy(g => g.Key.DayIndex))
        {
            if (!reference.Rooms.TryGetValue(g.Key.Value, out var room)) continue;
            int units = RoomPolicy.CellUnits(room, g.Count(),
                g.Select(p => p.Occ.ClassId).Distinct().Count());
            int over = SoftUnits.Crowding(units, RoomPolicy.EffectiveDesired(room));
            if (over > 0)
                yield return $"Кабинет «{room.Name}», день {g.Key.DayIndex + 1}, урок {g.Key.SlotIndex}: " +
                    $"занятий {units} при желательно {RoomPolicy.EffectiveDesired(room)}";
        }
    }

    // P2: разрыв закрепления с привязкой к классу и предмету.
    private static IEnumerable<string> SplitLines(
        SchedulingProblem reference, ScheduleCandidate candidate)
    {
        var placements = Resolve(reference, candidate);
        foreach (var g in placements
                     .Where(p => p.Occ.GroupId is null)
                     .GroupBy(p => (p.Occ.ClassId, p.Occ.SubjectId))
                     .OrderBy(g => ClassName(reference, g.Key.ClassId)))
        {
            int extra = SoftUnits.Split(g.Select(p => p.Occ.TeacherId).Distinct().Count());
            if (extra <= 0) continue;
            reference.Subjects.TryGetValue(g.Key.SubjectId, out var subj);
            yield return $"Класс {ClassName(reference, g.Key.ClassId)}, {subj?.Name ?? "предмет"}: " +
                $"ведут {g.Select(p => p.Occ.TeacherId).Distinct().Count()} учителя";
        }
    }

    // P2: тяжёлые на краю с привязкой к классу и дню.
    private static IEnumerable<string> HeavyLines(
        SchedulingProblem reference, ScheduleCandidate candidate)
    {
        var placements = Resolve(reference, candidate);
        foreach (var g in placements
                     .GroupBy(p => (p.Occ.ClassId, p.Placed.DayIndex))
                     .OrderBy(g => ClassName(reference, g.Key.ClassId)))
        {
            var slots = g.Select(p => p.Placed.SlotIndex).Distinct().OrderBy(s => s).ToList();
            if (slots.Count == 0) continue;
            bool HeavyAt(int s) => g.Where(p => p.Placed.SlotIndex == s)
                .Any(p => reference.Subjects.TryGetValue(p.Occ.SubjectId, out var subj) &&
                    subj.Difficulty >= reference.Flex.IsHeavyThreshold);
            var edges = new List<int>();
            if (HeavyAt(slots[0])) edges.Add(slots[0]);
            if (slots.Count > 1 && HeavyAt(slots[^1])) edges.Add(slots[^1]);
            if (edges.Count > 0)
                yield return $"Класс {ClassName(reference, g.Key.ClassId)}, день {g.Key.DayIndex + 1}: " +
                    $"тяжёлый урок на краю дня (урок {string.Join(" и ", edges)})";
        }
    }

    private static string OtherCodeLine(string code, long value) =>
        RuleCatalog.NeedsConfirmation(code)
            ? $"{HumanName(code)}: {value} — требует сверки с нормами"
            : $"{HumanName(code)}: {value}";

    private static string ClassName(SchedulingProblem problem, Guid classId) =>
        problem.Classes.TryGetValue(classId, out var c) ? c.Name : "класс";

    private static string TeacherName(SchedulingProblem problem, Guid teacherId) =>
        problem.Teachers.TryGetValue(teacherId, out var t) ? t.Name : "учитель";

    private static string GapWord(int n) =>
        (n % 10 == 1 && n % 100 != 11) ? $"окно {n} урок"
        : (n % 10 >= 2 && n % 10 <= 4 && (n % 100 < 10 || n % 100 >= 20)) ? $"окна {n} урока"
        : $"окон {n} уроков";

    private static string LessonWord(int n) =>
        (n % 10 == 1 && n % 100 != 11) ? $"{n} урок"
        : (n % 10 >= 2 && n % 10 <= 4 && (n % 100 < 10 || n % 100 >= 20)) ? $"{n} урока"
        : $"{n} уроков";
}
