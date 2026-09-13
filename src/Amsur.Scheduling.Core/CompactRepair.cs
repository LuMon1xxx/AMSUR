namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// Дефрагментация классо-дней к якорю смены (D-28): каждый (класс, день) сдвигается
// в сплошной блок от anchor или anchor+1. Только внутри дня (дневные счётчики
// учителей/предметов не меняются). Sync-пары едут вместе (тайм-группы).
// Детерминирован (классы по имени, дни по возрастанию, комнаты по имени).
// Честно частичен: заблокированные дни остаются как есть + Notes для диагностики.
public static class CompactRepair
{
    public sealed record RepairResult(
        List<PlacedLesson> Placements, int RepairedDays, int FailedDays,
        IReadOnlyList<string> Notes);

    public static RepairResult Repair(
        SchedulingProblem problem, IReadOnlyList<PlacedLesson> placements)
    {
        var occById = problem.Occurrences.ToDictionary(o => o.Id);
        var pos = placements.ToDictionary(p => p.OccurrenceId);
        int repaired = 0, failed = 0;
        var notes = new List<string>();

        var classDays = placements
            .GroupBy(p => (ClassId: occById[p.OccurrenceId].ClassId, p.DayIndex))
            .OrderBy(g => problem.Classes.TryGetValue(g.Key.ClassId, out var c) ? c.Name : "", StringComparer.Ordinal)
            .ThenBy(g => g.Key.DayIndex)
            .ToList();

        foreach (var cd in classDays)
        {
            var classId = cd.Key.ClassId;
            int day = cd.Key.DayIndex;
            var slots = cd.Select(p => p.SlotIndex).OrderBy(s => s).ToList();
            int anchor = StudentCompactness.AnchorFor(problem, classId);
            if (StudentCompactness.GapOf(slots) == 0 && StudentCompactness.LateExcess(slots, anchor) == 0)
                continue; // уже компактно

            if (TryCompactDay(problem, occById, pos, classId, day, anchor))
                repaired++;
            else
            {
                failed++;
                if (notes.Count < 10)
                    notes.Add($"Класс {ClsName(problem, classId)} день {day + 1}: " +
                        $"не удалось убрать окно/поздний старт (занято учителями/кабинетами).");
            }
        }
        return new RepairResult(pos.Values.ToList(), repaired, failed, notes);
    }

    private static string ClsName(SchedulingProblem problem, Guid classId) =>
        problem.Classes.TryGetValue(classId, out var c) ? c.Name : "?";

    private static bool TryCompactDay(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        Dictionary<Guid, PlacedLesson> pos,
        Guid classId, int day, int anchor)
    {
        // Тайм-группы дня (один слот → все занятия в нём, sync-пары не рвутся).
        var groups = pos.Values
            .Where(p => occById[p.OccurrenceId].ClassId == classId && p.DayIndex == day)
            .GroupBy(p => p.SlotIndex)
            .OrderBy(g => g.Key)
            .Select(g => g.ToList())
            .ToList();
        var moving = groups.SelectMany(g => g).Select(p => p.OccurrenceId).ToHashSet();

        // Занятость БЕЗ двигаемого блока.
        var teacherBusy = new HashSet<(Guid, int, int)>();
        var groupBusy = new HashSet<(Guid, int, int)>();
        var wholeBusy = new HashSet<(Guid, int, int)>();
        var subBusy = new HashSet<(Guid, int, int)>();
        var roomUsers = new Dictionary<(Guid, int, int), int>();
        foreach (var p in pos.Values)
        {
            if (moving.Contains(p.OccurrenceId)) continue;
            var o = occById[p.OccurrenceId];
            teacherBusy.Add((o.TeacherId, p.DayIndex, p.SlotIndex));
            groupBusy.Add(((o.GroupId ?? o.ClassId), p.DayIndex, p.SlotIndex));
            if (o.GroupId.HasValue) subBusy.Add((o.ClassId, p.DayIndex, p.SlotIndex));
            else wholeBusy.Add((o.ClassId, p.DayIndex, p.SlotIndex));
            if (p.RoomId.HasValue)
                roomUsers[(p.RoomId.Value, p.DayIndex, p.SlotIndex)] =
                    roomUsers.GetValueOrDefault((p.RoomId.Value, p.DayIndex, p.SlotIndex)) + 1;
        }

        foreach (int s0 in new[] { anchor, anchor + 1 })
        {
            var targets = Enumerable.Range(s0, groups.Count).ToList();
            if (TryAssign(problem, occById, pos, moving, groups, day, targets,
                    teacherBusy, groupBusy, wholeBusy, subBusy, roomUsers))
                return true;
        }
        return false;
    }

    private static bool TryAssign(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        Dictionary<Guid, PlacedLesson> pos,
        HashSet<Guid> moving,
        List<List<PlacedLesson>> groups,
        int day, List<int> targets,
        HashSet<(Guid, int, int)> teacherBusy,
        HashSet<(Guid, int, int)> groupBusy,
        HashSet<(Guid, int, int)> wholeBusy,
        HashSet<(Guid, int, int)> subBusy,
        Dictionary<(Guid, int, int), int> roomUsers)
    {
        // Матчинг тайм-групп на целевые слоты с бэктрекингом (D-28b): какая группа
        // на какой слот встанет — влияет на коллизии учителей/кабинетов;
        // позиционное (по порядку) назначение — лишь частный случай.
        // Sync-пары не рвутся (группа едет целиком). Детерминировано.
        var order = groups
            .Select((g, i) => i)
            .OrderBy(i => groups[i].Count)
            .ThenBy(i => groups[i].Min(p => p.OccurrenceId))
            .ToList();
        var assign = new Dictionary<int, int>(); // groupIdx -> targetSlot
        var usedTargets = new HashSet<int>();
        var plan = new Dictionary<Guid, (int Slot, Guid? Room)>();
        var planRoomUse = new Dictionary<(Guid, int, int), int>();

        bool Dfs(int k)
        {
            if (k == order.Count) return true;
            int gi = order[k];
            foreach (int slot in targets)
            {
                if (!usedTargets.Add(slot)) continue;
                if (TryFitGroup(problem, occById, pos, groups[gi], day, slot,
                        teacherBusy, groupBusy, wholeBusy, subBusy,
                        roomUsers, planRoomUse, plan))
                {
                    assign[gi] = slot;
                    if (Dfs(k + 1)) return true;
                    assign.Remove(gi);
                    // Откат комнат группы.
                    foreach (var p in groups[gi])
                    {
                        if (plan.Remove(p.OccurrenceId, out var old) && old.Room.HasValue)
                        {
                            var key = (old.Room.Value, day, slot);
                            int v = planRoomUse[key] - 1;
                            if (v <= 0) planRoomUse.Remove(key);
                            else planRoomUse[key] = v;
                        }
                    }
                }
                usedTargets.Remove(slot);
            }
            return false;
        }

        if (!Dfs(0)) return false;
        foreach (var kv in plan)
            pos[kv.Key] = new PlacedLesson
            {
                OccurrenceId = kv.Key, DayIndex = day,
                SlotIndex = kv.Value.Slot, RoomId = kv.Value.Room
            };
        return true;
    }

    // Группа целиком на слот: все учителя свободны, occupancy-ключи свободны
    // (включая соседей по ПЛАНУ — частично назначенный день), комнаты подбираются.
    private static bool TryFitGroup(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        Dictionary<Guid, PlacedLesson> pos,
        List<PlacedLesson> group,
        int day, int slot,
        HashSet<(Guid, int, int)> teacherBusy,
        HashSet<(Guid, int, int)> groupBusy,
        HashSet<(Guid, int, int)> wholeBusy,
        HashSet<(Guid, int, int)> subBusy,
        Dictionary<(Guid, int, int), int> roomUsers,
        Dictionary<(Guid, int, int), int> planRoomUse,
        Dictionary<Guid, (int Slot, Guid? Room)> plan)
    {
        // Сначала проверки без комнат (дешево), комнаты — после.
        foreach (var p in group.OrderBy(x => x.OccurrenceId))
        {
            var o = occById[p.OccurrenceId];
            if (problem.AllowedDays.TryGetValue(o.Id, out var days) && !days.Contains(day))
                return false;
            if (problem.AllowedSlots.TryGetValue(o.Id, out var slots) && !slots.Contains(slot))
                return false;
            if (teacherBusy.Contains((o.TeacherId, day, slot))) return false;
            if (groupBusy.Contains(((o.GroupId ?? o.ClassId), day, slot))) return false;
            if (o.GroupId.HasValue && wholeBusy.Contains((o.ClassId, day, slot))) return false;
            if (!o.GroupId.HasValue && subBusy.Contains((o.ClassId, day, slot))) return false;
            foreach (var q in plan.Keys)
            {
                var qo = occById[q];
                if (plan[q].Slot != slot) continue;
                if (qo.TeacherId == o.TeacherId) return false;
                bool clash = (!o.GroupId.HasValue || !qo.GroupId.HasValue)
                    ? qo.ClassId == o.ClassId
                    : ((qo.GroupId ?? qo.ClassId) == (o.GroupId ?? o.ClassId));
                if (!o.GroupId.HasValue && qo.ClassId == o.ClassId) clash = true;
                if (!qo.GroupId.HasValue && qo.ClassId == o.ClassId) clash = true;
                if (clash) return false;
            }
        }
        // Комнаты жадно по имени (детерминировано); учёт — СРАЗУ на каждый урок,
        // иначе sync-пары берут один кабинет дважды (D-28d: room overflow).
        // При неудаче — вся группа не встала (ничего не записано).
        foreach (var p in group.OrderBy(x => x.OccurrenceId))
        {
            var o = occById[p.OccurrenceId];
            Guid? room = PickRoom(problem, occById, o, day, slot, roomUsers, planRoomUse, pos[p.OccurrenceId].RoomId);
            if (room is null && problem.Rooms.Count > 0 && pos[p.OccurrenceId].RoomId.HasValue)
                return false; // кабинет нужен, но всё занято
            plan[p.OccurrenceId] = (slot, room);
            if (room.HasValue)
                planRoomUse[(room.Value, day, slot)] =
                    planRoomUse.GetValueOrDefault((room.Value, day, slot)) + 1;
        }
        return true;
    }

    private static Guid? PickRoom(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        LessonOccurrence occ, int day, int slot,
        Dictionary<(Guid, int, int), int> roomUsers,
        Dictionary<(Guid, int, int), int> planRoomUse,
        Guid? prefer)
    {
        if (problem.Rooms.Count == 0) return null;
        int students = problem.Classes.TryGetValue(occ.ClassId, out var cls) ? cls.StudentCount : 0;
        int need = occ.GroupId.HasValue ? (students + 1) / 2 : students;
        int Used(Guid r) => roomUsers.GetValueOrDefault((r, day, slot)) + planRoomUse.GetValueOrDefault((r, day, slot));
        bool Fits(Guid r)
        {
            if (!problem.Rooms.TryGetValue(r, out var room)) return false;
            if (problem.RoomCaps.TryGetValue((r, occ.SubjectId), out var cap) && cap == RoomCapabilityKind.Forbidden)
                return false;
            if (room.PhysicalCapacity < need) return false;
            return Used(r) + 1 <= Math.Max(1, room.MaxSimultaneousGroups);
        }
        if (prefer.HasValue && Fits(prefer.Value)) return prefer;
        foreach (var room in problem.Rooms.Values.OrderBy(r => r.Name, StringComparer.Ordinal))
            if (Fits(room.Id)) return room.Id;
        return null;
    }
}
