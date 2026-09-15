namespace Amsur.Scheduling.Core;

using Amsur.Domain;

public enum EvaluationSeverity { Allowed = 0, Warning = 1, Forbidden = 2 }

public sealed record CandidateMove(Guid OccurrenceId, int DayIndex, int SlotIndex, Guid? RoomId);

public sealed record PlacementEvaluation(
    Guid OccurrenceId,
    CandidateMove Candidate,
    EvaluationSeverity Severity,
    IReadOnlyList<ValidationIssue> HardViolations,
    long DeltaTotal,
    IReadOnlyList<string> Reasons)
{
    public bool IsAllowed => Severity != EvaluationSeverity.Forbidden;
}

// Инкрементальный evaluator для preview DnD (без solver).
// Hard-проверки — скоуповые (затронутые сущности); soft-дельта —
// полным пересчётом SoftEvaluator до/после (паритет по построению, D-08).
public static class IncrementalEvaluator
{
    public static PlacementEvaluation Evaluate(
        SchedulingProblem problem,
        IReadOnlyList<PlacedLesson> current,
        CandidateMove move) =>
        Evaluate(problem, current, move, null);

    public static PlacementEvaluation Evaluate(
        SchedulingProblem problem,
        IReadOnlyList<PlacedLesson> current,
        CandidateMove move,
        EffectiveRuleSet? rules)
    {
        var occById = problem.Occurrences.ToDictionary(o => o.Id);
        var hard = new List<ValidationIssue>();
        var reasons = new List<string>();

        if (!occById.TryGetValue(move.OccurrenceId, out var node))
        {
            hard.Add(new ValidationIssue { Code = "unknown-occurrence", Message = "Занятие не найдено." });
            return new PlacementEvaluation(move.OccurrenceId, move, EvaluationSeverity.Forbidden, hard, 0, hard.Select(h => h.Message).ToList());
        }

        // Вне предфильтрованного домена?
        if (problem.AllowedDays.TryGetValue(node.Id, out var days) && !days.Contains(move.DayIndex))
        {
            hard.Add(new ValidationIssue
            {
                Code = PhysicalRuleCodes.ShiftDomain,
                Message = "Нельзя поставить в это время (вне сетки/доступности).",
                OccurrenceId = node.Id, ClassId = node.ClassId, TeacherId = node.TeacherId
            });
        }
        if (problem.AllowedSlots.TryGetValue(node.Id, out var slots) && !slots.Contains(move.SlotIndex))
        {
            hard.Add(new ValidationIssue
            {
                Code = PhysicalRuleCodes.ShiftDomain,
                Message = "Нельзя поставить в этот номер урока.",
                OccurrenceId = node.Id, ClassId = node.ClassId, TeacherId = node.TeacherId
            });
        }

        // Forbidden room capability (Hard) + P2/R1 ONLY-кабинет (чужой предмет — hard).
        if (move.RoomId.HasValue &&
            problem.RoomCaps.TryGetValue((move.RoomId.Value, node.SubjectId), out var cap) &&
            cap == RoomCapabilityKind.Forbidden)
        {
            hard.Add(new ValidationIssue
            {
                Code = "forbidden-room",
                Message = "Кабинет запрещён для этого предмета.",
                OccurrenceId = node.Id, RoomId = move.RoomId
            });
        }
        if (move.RoomId.HasValue &&
            problem.Rooms.TryGetValue(move.RoomId.Value, out var onlyRoom) &&
            onlyRoom.OnlySubjectId.HasValue && onlyRoom.OnlySubjectId.Value != node.SubjectId)
        {
            hard.Add(new ValidationIssue
            {
                Code = "forbidden-room",
                Message = $"Кабинет «{onlyRoom.Name}» — только для своего предмета.",
                OccurrenceId = node.Id, RoomId = move.RoomId
            });
        }

        var cur = current.ToDictionary(p => p.OccurrenceId);
        var hypo = current
            .Where(p => p.OccurrenceId != move.OccurrenceId)
            .Concat([new PlacedLesson
            {
                OccurrenceId = move.OccurrenceId, DayIndex = move.DayIndex,
                SlotIndex = move.SlotIndex, RoomId = move.RoomId
            }])
            .ToList();
        var hypoByOcc = hypo.ToDictionary(p => p.OccurrenceId);

        // Teacher scope.
        foreach (var other in problem.Occurrences.Where(o => o.TeacherId == node.TeacherId && o.Id != node.Id))
        {
            if (!hypoByOcc.TryGetValue(other.Id, out var p)) continue;
            if (p.DayIndex == move.DayIndex && p.SlotIndex == move.SlotIndex)
            {
                hard.Add(new ValidationIssue
                {
                    Code = PhysicalRuleCodes.TeacherCollision,
                    Message = "Учитель уже ведёт урок в это время.",
                    TeacherId = node.TeacherId, OccurrenceId = node.Id
                });
                break;
            }
        }

        // Class/subgroup scope: тот же occupancy-ключ, что в FullValidator.
        Guid Key(LessonOccurrence n) => n.GroupId ?? n.ClassId;
        foreach (var other in problem.Occurrences.Where(o => o.Id != node.Id))
        {
            if (!hypoByOcc.TryGetValue(other.Id, out var p)) continue;
            var o = occById[other.Id];
            bool sameTime = p.DayIndex == move.DayIndex && p.SlotIndex == move.SlotIndex;
            if (!sameTime) continue;
            // whole (GroupId null) блокирует всех одноклассников; subgroup — свой ключ.
            bool blocks = !node.GroupId.HasValue || !o.GroupId.HasValue
                ? o.ClassId == node.ClassId
                : Key(o) == Key(node);
            // whole-class с другой стороны тоже блокирует.
            if (!o.GroupId.HasValue && o.ClassId == node.ClassId) blocks = true;
            if (!node.GroupId.HasValue && o.ClassId == node.ClassId) blocks = true;
            if (blocks)
            {
                hard.Add(new ValidationIssue
                {
                    Code = PhysicalRuleCodes.GroupCollision,
                    Message = "Класс/группа уже заняты в это время.",
                    ClassId = node.ClassId, OccurrenceId = node.Id
                });
                break;
            }
        }

        // Room scope (P2/R5: единицы key-aware).
        if (move.RoomId.HasValue && problem.Rooms.TryGetValue(move.RoomId.Value, out var room))
        {
            var cell = hypo.Where(p => p.RoomId == move.RoomId &&
                p.DayIndex == move.DayIndex && p.SlotIndex == move.SlotIndex).ToList();
            int units = RoomPolicy.CellUnits(room, cell.Count,
                cell.Select(p => occById.TryGetValue(p.OccurrenceId, out var o) ? o.ClassId : Guid.Empty)
                    .Distinct().Count());
            if (units > room.MaxSimultaneousGroups)
                hard.Add(new ValidationIssue
                {
                    Code = PhysicalRuleCodes.RoomOverflow,
                    Message = $"Кабинет занят ({units} при лимите {room.MaxSimultaneousGroups}).",
                    RoomId = room.Id, OccurrenceId = node.Id
                });
        }

        // Sync: смена времени одной половины без второй — Forbidden; смена только кабинета — разрешена.
        if (node.SyncGroupId.HasValue)
        {
            foreach (var mate in problem.Occurrences.Where(o => o.SyncGroupId == node.SyncGroupId && o.Id != node.Id))
            {
                if (!hypoByOcc.TryGetValue(mate.Id, out var p)) continue;
                if (!cur.TryGetValue(mate.Id, out _)) continue;
                if (p.DayIndex != move.DayIndex || p.SlotIndex != move.SlotIndex)
                {
                    hard.Add(new ValidationIssue
                    {
                        Code = PhysicalRuleCodes.SubgroupSync,
                        Message = "Нарушается синхронизация подгрупп: вторая половина остаётся на месте.",
                        OccurrenceId = node.Id, ClassId = node.ClassId
                    });
                    break;
                }
            }
        }

        // P2/R7: один учитель на (класс,предмет)/(параллель,предмет) — по hypo.
        var assignMode = problem.Flex.AssignMode;
        if (!node.GroupId.HasValue &&
            assignMode is TeacherAssignMode.HardClass or TeacherAssignMode.HardParallel)
        {
            string KeyOf(LessonOccurrence o) =>
                assignMode == TeacherAssignMode.HardClass
                    ? $"{o.ClassId:D}|{o.SubjectId:D}"
                    : $"{(problem.Classes.TryGetValue(o.ClassId, out var c) ? c.Grade : 0)}|{o.SubjectId:D}";
            string key = KeyOf(node);
            var teachers = hypo
                .Select(p => occById.TryGetValue(p.OccurrenceId, out var o) ? o : null)
                .Where(o => o is not null && !o.GroupId.HasValue && KeyOf(o!) == key)
                .Select(o => o!.TeacherId)
                .Distinct().ToList();
            if (teachers.Count > 1)
                hard.Add(new ValidationIssue
                {
                    Code = "teacher-assign",
                    Message = "Предмет в классе/параллели ведут несколько учителей.",
                    ClassId = node.ClassId, OccurrenceId = node.Id, TeacherId = node.TeacherId
                });
        }

        // Teacher MaxPerDay (Hard FROZEN D-04): скоупово.
        if (problem.Teachers.TryGetValue(node.TeacherId, out var teacher))
        {
            int count = hypo.Count(p => occById.TryGetValue(p.OccurrenceId, out var o) &&
                o.TeacherId == node.TeacherId && p.DayIndex == move.DayIndex);
            if (count > teacher.MaxLessonsPerDay)
                hard.Add(new ValidationIssue
                {
                    Code = "teacher-maxperday",
                    Message = $"Превышен дневной лимит учителя ({count} > {teacher.MaxLessonsPerDay}).",
                    TeacherId = node.TeacherId
                });
        }

        // Компактность ученика (D-28, SANPIN_RB.md §5): окна и старт позже anchor+1 —
        // Forbidden в ручной правке (solver обходит через soft-градиент + CompactRepair).
        {
            int anchor = StudentCompactness.AnchorFor(problem, node.ClassId);
            foreach (int day in new[] { hypo.First(p => p.OccurrenceId == move.OccurrenceId).DayIndex,
                                        current.First(p => p.OccurrenceId == move.OccurrenceId).DayIndex })
            {
                var daySlots = hypo
                    .Where(p => occById.TryGetValue(p.OccurrenceId, out var o) &&
                                o.ClassId == node.ClassId && p.DayIndex == day)
                    .Select(p => p.SlotIndex).OrderBy(s => s).ToList();
                if (daySlots.Count == 0) continue;
                int gap = StudentCompactness.GapOf(daySlots);
                if (gap > 0)
                    hard.Add(new ValidationIssue
                    {
                        Code = "student-gap",
                        Message = "Окно у класса: между уроками появится пустой час.",
                        ClassId = node.ClassId, OccurrenceId = node.Id
                    });
                int late = StudentCompactness.LateExcess(daySlots, anchor);
                if (late > 0)
                    hard.Add(new ValidationIssue
                    {
                        Code = "student-late-start",
                        Message = $"Класс будет начинать день с урока {daySlots[0]} (допустимо с {anchor} или {anchor + 1}).",
                        ClassId = node.ClassId, OccurrenceId = node.Id
                    });
                if (problem.Classes.TryGetValue(node.ClassId, out var cls))
                {
                    // distinct-слоты: сплит-час — 1 слот дня.
                    int dayCount = daySlots.Distinct().Count();
                    if (dayCount > cls.MaxLessonsPerDay)
                        hard.Add(new ValidationIssue
                        {
                            Code = "class-maxperday",
                            Message = $"Превышена дневная норма класса ({dayCount} > {cls.MaxLessonsPerDay}).",
                            ClassId = node.ClassId, OccurrenceId = node.Id
                        });
                }
            }
        }

        if (hard.Count > 0)
            return new PlacementEvaluation(node.Id, move, EvaluationSeverity.Forbidden, hard, 0,
                hard.Select(h => h.Message).ToList());

        // Soft-дельта полным пересчётом (паритет по построению).
        var rs = rules ?? EffectiveRuleSet.Default;
        var before = SoftEvaluator.Evaluate(problem, current, rs);
        var after = SoftEvaluator.Evaluate(problem, hypo, rs);
        long delta = SoftEvaluator.Delta(before, after);
        var severity = delta > 0 ? EvaluationSeverity.Warning : EvaluationSeverity.Allowed;
        if (delta != 0) reasons.Add($"Изменение штрафа: {(delta > 0 ? "+" : "")}{delta}.");
        return new PlacementEvaluation(node.Id, move, severity, [], delta, reasons);
    }
}
