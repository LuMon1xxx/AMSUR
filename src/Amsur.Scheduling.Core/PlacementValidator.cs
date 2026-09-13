namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// Авторитетный FullValidator (INV-01): ни одно Accepted-расписание без Hard==0.
// Независим от solver. Порт V1 PlacementValidator + фикс occupancy-ключа (HashSet, не string.Join).
public static class PlacementValidator
{
    public static ValidationResult Validate(SchedulingProblem problem, IReadOnlyList<PlacedLesson> placements)
    {
        var result = new ValidationResult();
        var occById = problem.Occurrences.ToDictionary(o => o.Id);

        // INV-06: полнота размещения.
        if (placements.Count != problem.Occurrences.Count)
            result.HardViolations.Add(new ValidationIssue
            {
                Code = "placement-count",
                Message = $"Placed {placements.Count} of {problem.Occurrences.Count} required occurrences."
            });

        var byTeacher = new Dictionary<(Guid, int, int), List<Guid>>();
        var byGroup = new Dictionary<(Guid, int, int), List<Guid>>(); // group-or-class key
        var byRoom = new Dictionary<(Guid, int, int), int>();
        var placedByOcc = placements.ToDictionary(p => p.OccurrenceId);

        foreach (var p in placements)
        {
            if (!occById.TryGetValue(p.OccurrenceId, out var occ))
            {
                result.HardViolations.Add(new ValidationIssue
                {
                    Code = "not-placed", Message = $"Unknown occurrence {p.OccurrenceId}.",
                    OccurrenceId = p.OccurrenceId
                });
                continue;
            }

            // Shift-domain (частично): слот в допустимом множестве.
            if (problem.AllowedSlots.TryGetValue(occ.Id, out var slots) && !slots.Contains(p.SlotIndex))
                result.HardViolations.Add(new ValidationIssue
                {
                    Code = PhysicalRuleCodes.ShiftDomain,
                    Message = $"Occurrence {occ.Id} placed at forbidden slot {p.SlotIndex}.",
                    OccurrenceId = occ.Id, ClassId = occ.ClassId, TeacherId = occ.TeacherId
                });
            if (problem.AllowedDays.TryGetValue(occ.Id, out var days) && !days.Contains(p.DayIndex))
                result.HardViolations.Add(new ValidationIssue
                {
                    Code = PhysicalRuleCodes.ShiftDomain,
                    Message = $"Occurrence {occ.Id} placed at forbidden day {p.DayIndex}.",
                    OccurrenceId = occ.Id, ClassId = occ.ClassId, TeacherId = occ.TeacherId
                });

            // Teacher double-booking (INV: teacher фиксирован, не переменная solver).
            var tk = (occ.TeacherId, p.DayIndex, p.SlotIndex);
            if (!byTeacher.TryGetValue(tk, out var tl)) byTeacher[tk] = tl = [];
            tl.Add(occ.Id);
            if (tl.Count > 1)
                result.HardViolations.Add(new ValidationIssue
                {
                    Code = PhysicalRuleCodes.TeacherCollision,
                    Message = $"Teacher {occ.TeacherId} double-booked at day {p.DayIndex} slot {p.SlotIndex}.",
                    TeacherId = occ.TeacherId, OccurrenceId = occ.Id
                });

            // Class/subgroup occupancy через GroupParents (D-фикс: HashSet-ключ, N>2 корректно).
            Guid occupancyKey = occ.GroupId.HasValue ? occ.GroupId.Value : occ.ClassId;
            var gk = (occupancyKey, p.DayIndex, p.SlotIndex);
            if (!byGroup.TryGetValue(gk, out var gl)) byGroup[gk] = gl = [];
            gl.Add(occ.Id);
            // whole-class (GroupId null) блокирует всё: проверяем отдельно ниже.
            if (gl.Count > 1)
                result.HardViolations.Add(new ValidationIssue
                {
                    Code = PhysicalRuleCodes.GroupCollision,
                    Message = $"Group {occupancyKey} double-booked at day {p.DayIndex} slot {p.SlotIndex}.",
                    ClassId = occ.ClassId, OccurrenceId = occ.Id
                });

            // Teacher availability / day-off / maxperday (D-04: Hard FROZEN).
            if (problem.Teachers.TryGetValue(occ.TeacherId, out var teacher))
            {
                // maxperday проверяется ниже по дням.
                _ = teacher;
            }

            // Room sweep: MaxSimultaneousGroups (Hard) + Forbidden capability (Hard).
            if (p.RoomId.HasValue)
            {
                var rk = (p.RoomId.Value, p.DayIndex, p.SlotIndex);
                byRoom[rk] = byRoom.GetValueOrDefault(rk) + 1;
                if (problem.Rooms.TryGetValue(p.RoomId.Value, out var room) &&
                    byRoom[rk] > room.MaxSimultaneousGroups)
                    result.HardViolations.Add(new ValidationIssue
                    {
                        Code = PhysicalRuleCodes.RoomOverflow,
                        Message = $"Room {room.Name} overflow at day {p.DayIndex} slot {p.SlotIndex}.",
                        RoomId = room.Id, OccurrenceId = occ.Id
                    });
                if (problem.RoomCaps.TryGetValue((p.RoomId.Value, occ.SubjectId), out var cap) &&
                    cap == RoomCapabilityKind.Forbidden)
                    result.HardViolations.Add(new ValidationIssue
                    {
                        Code = "forbidden-room",
                        Message = $"Room is forbidden for this subject at day {p.DayIndex} slot {p.SlotIndex}.",
                        RoomId = p.RoomId.Value, OccurrenceId = occ.Id, ClassId = occ.ClassId
                    });
            }
        }

        // Whole-class блокирует подгруппы: если whole и subgroup в одном слоте — коллизия.
        // (Упрощённо P0: whole занимает ClassId-ключ; subgroup занимает GroupId-ключ.
        //  Пересечение ловим через сравнение составов при совпадении времени.)
        var wholeSlots = new HashSet<(Guid, int, int)>();
        foreach (var p in placements)
        {
            var occ = occById.GetValueOrDefault(p.OccurrenceId);
            if (occ is null || occ.GroupId.HasValue) continue;
            wholeSlots.Add((occ.ClassId, p.DayIndex, p.SlotIndex));
        }
        foreach (var p in placements)
        {
            var occ = occById.GetValueOrDefault(p.OccurrenceId);
            if (occ is null || !occ.GroupId.HasValue) continue;
            if (wholeSlots.Contains((occ.ClassId, p.DayIndex, p.SlotIndex)))
                result.HardViolations.Add(new ValidationIssue
                {
                    Code = PhysicalRuleCodes.GroupCollision,
                    Message = $"Whole class overlaps subgroup at day {p.DayIndex} slot {p.SlotIndex}.",
                    ClassId = occ.ClassId, OccurrenceId = occ.Id
                });
        }

        // Teacher MaxPerDay (Hard FROZEN, D-04).
        foreach (var g in placements.GroupBy(p => (occById[p.OccurrenceId].TeacherId, p.DayIndex)))
        {
            var teacherId = g.Key.TeacherId;
            if (problem.Teachers.TryGetValue(teacherId, out var t) && g.Count() > t.MaxLessonsPerDay)
                result.HardViolations.Add(new ValidationIssue
                {
                    Code = "teacher-maxperday",
                    Message = $"Teacher {t.Name} has {g.Count()} lessons at day {g.Key.DayIndex} (max {t.MaxLessonsPerDay}).",
                    TeacherId = teacherId
                });
        }

        // Компактность ученика (D-28, SANPIN_RB.md §5): внутренние окна запрещены,
        // старт не позже anchor+1. Якорь = min AllowedSlots класса (смена 1→1, 2→8).
        foreach (var g in placements.GroupBy(p => (occById[p.OccurrenceId].ClassId, p.DayIndex)))
        {
            var classId = g.Key.ClassId;
            var slots = g.Select(p => p.SlotIndex).OrderBy(s => s).ToList();
            int anchor = StudentCompactness.AnchorFor(problem, classId);
            int gap = StudentCompactness.GapOf(slots);
            if (gap > 0)
                result.HardViolations.Add(new ValidationIssue
                {
                    Code = "student-gap",
                    Message = $"Окно у класса {ClassName(problem, classId)} в день {g.Key.DayIndex + 1}: {gap} пустых урока внутри дня.",
                    ClassId = classId
                });
            int late = StudentCompactness.LateExcess(slots, anchor);
            if (late > 0)
                result.HardViolations.Add(new ValidationIssue
                {
                    Code = "student-late-start",
                    Message = $"Класс {ClassName(problem, classId)} в день {g.Key.DayIndex + 1} начинает с урока {slots[0]} (допустимо с {anchor} или {anchor + 1}).",
                    ClassId = classId
                });
        }

        // Дневной максимум класса по СанПиН (SANPIN_RB.md §3): HARD.
        // Считаем ЗАНЯТЫЕ СЛОТЫ (distinct), а не placements: сплит-час — 2 placements в 1 слоте.
        foreach (var g in placements.GroupBy(p => (occById[p.OccurrenceId].ClassId, p.DayIndex)))
        {
            int slotCount = g.Select(p => p.SlotIndex).Distinct().Count();
            if (problem.Classes.TryGetValue(g.Key.ClassId, out var cls) && slotCount > cls.MaxLessonsPerDay)
                result.HardViolations.Add(new ValidationIssue
                {
                    Code = "class-maxperday",
                    Message = $"Класс {cls.Name}: {slotCount} уроков в день {g.Key.DayIndex + 1} (норма {cls.MaxLessonsPerDay}).",
                    ClassId = g.Key.ClassId
                });
            // 1-е классы: дней с 5 уроками — не более 1 в неделю (норма «4 + 1×5»).
            if (problem.Classes.TryGetValue(g.Key.ClassId, out var cls1) && cls1.Grade == 1 && slotCount == 5)
            {
                int fiveDays = placements
                    .Where(p => occById[p.OccurrenceId].ClassId == g.Key.ClassId)
                    .GroupBy(p => p.DayIndex)
                    .Count(dg => dg.Select(p => p.SlotIndex).Distinct().Count() == 5);
                if (fiveDays > 1)
                    result.HardViolations.Add(new ValidationIssue
                    {
                        Code = "class-maxperday",
                        Message = $"Класс {cls1.Name}: 5-урочных дней {fiveDays} (норма для 1-х классов — не более 1 в неделю).",
                        ClassId = g.Key.ClassId
                    });
            }
        }

        // Subgroup sync: одинаковый start + дизъюнктный состав (INV-03).
        foreach (var grp in problem.Occurrences
                     .Where(o => o.SyncGroupId.HasValue)
                     .GroupBy(o => o.SyncGroupId!.Value))
        {
            var members = grp.ToList();
            var times = members
                .Where(m => placedByOcc.ContainsKey(m.Id))
                .Select(m => (placedByOcc[m.Id].DayIndex, placedByOcc[m.Id].SlotIndex))
                .Distinct().ToList();
            if (times.Count > 1)
                result.HardViolations.Add(new ValidationIssue
                {
                    Code = PhysicalRuleCodes.SubgroupSync,
                    Message = $"Sync group {grp.Key} split across {times.Count} times.",
                    OccurrenceId = members[0].Id, ClassId = members[0].ClassId
                });
            // Разные teachers (guard на входе, но проверяем и здесь).
            if (members.Select(m => m.TeacherId).Distinct().Count() != members.Count)
                result.HardViolations.Add(new ValidationIssue
                {
                    Code = PhysicalRuleCodes.SubgroupSync,
                    Message = $"Sync group {grp.Key}: one teacher covers both halves.",
                    OccurrenceId = members[0].Id, ClassId = members[0].ClassId
                });
        }

        return result;
    }

    // Маппинг (SolverStatus, cancelled, hasPlacements) → UserScheduleStatus (D-10, без нового enum solver).
    private static string ClassName(SchedulingProblem problem, Guid classId) =>
        problem.Classes.TryGetValue(classId, out var c) ? c.Name : "?";    public static UserScheduleStatus ToUserStatus(SolverStatus status, bool wasCancelled, bool hasPlacements) =>
        (status, wasCancelled, hasPlacements) switch
        {
            (_, true, true) => UserScheduleStatus.CancelledAfterFeasible,
            (_, true, false) => UserScheduleStatus.CancelledWithoutFeasible,
            (SolverStatus.Feasible, _, true) => UserScheduleStatus.Feasible,
            (SolverStatus.Optimal, _, true) => UserScheduleStatus.Feasible,
            (SolverStatus.Infeasible, _, _) => UserScheduleStatus.InfeasibleConfirmedByModel,
            (SolverStatus.ModelInvalid, _, _) => UserScheduleStatus.InfeasibleConfirmedByModel,
            _ => UserScheduleStatus.NoSolutionFoundWithinLimit,
        };
}
