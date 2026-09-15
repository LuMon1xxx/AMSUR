namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// E12/D-24 — жадный конструктивный старт для Phase A (семантику не меняет:
// только hints; validator по-прежнему gate). Детерминирован (порядок — StableKey,
// выбор — первый минимум). Частичен честно: неразмещённые возвращает списком.
public sealed record GreedyPlacement(
    Dictionary<Guid, (int Day, int Slot, Guid? RoomId)> Placed,
    List<Guid> Unplaced);

public static class GreedyPlacer
{
    // LNS-пересев (D-28d): досеять заданные юниты поверх ЗАМОРОЖЕННОГО состояния
    // (занятость от kept-размещений). Детерминирован сидом. Частичен честно.
    internal static GreedyPlacement Replant(
        SchedulingProblem problem,
        IReadOnlyDictionary<Guid, (int Day, int Slot, Guid? RoomId)> frozen,
        List<List<Guid>> units,
        int seed)
    {
        var occById = problem.Occurrences.ToDictionary(o => o.Id);
        var placed = new Dictionary<Guid, (int Day, int Slot, Guid? RoomId)>(frozen);
        var unplaced = new List<Guid>();

        var teacherBusy = new HashSet<(Guid, int, int)>();
        var groupBusy = new HashSet<(Guid, int, int)>();
        var wholeBusy = new HashSet<(Guid, int, int)>();
        var subBusy = new HashSet<(Guid, int, int)>();
        var roomUsers = new Dictionary<(Guid, int, int), int>();
        var teacherDay = new Dictionary<(Guid, int), int>();
        var classDay = new Dictionary<(Guid, int), HashSet<int>>();
        foreach (var kv in frozen)
        {
            var o = occById[kv.Key];
            teacherBusy.Add((o.TeacherId, kv.Value.Day, kv.Value.Slot));
            groupBusy.Add(((o.GroupId ?? o.ClassId), kv.Value.Day, kv.Value.Slot));
            if (o.GroupId.HasValue) subBusy.Add((o.ClassId, kv.Value.Day, kv.Value.Slot));
            else wholeBusy.Add((o.ClassId, kv.Value.Day, kv.Value.Slot));
            if (kv.Value.RoomId.HasValue)
                roomUsers[(kv.Value.RoomId.Value, kv.Value.Day, kv.Value.Slot)] =
                    roomUsers.GetValueOrDefault((kv.Value.RoomId.Value, kv.Value.Day, kv.Value.Slot)) + 1;
            teacherDay[(o.TeacherId, kv.Value.Day)] = teacherDay.GetValueOrDefault((o.TeacherId, kv.Value.Day)) + 1;
            if (!classDay.TryGetValue((o.ClassId, kv.Value.Day), out var set))
                classDay[(o.ClassId, kv.Value.Day)] = set = [];
            set.Add(kv.Value.Slot);
        }

        Guid OccKey(LessonOccurrence o) => o.GroupId ?? o.ClassId;
        var rng = new Random(seed);
        var ordered = units
            .Select(u => (Ids: u, Key: occById[u[0]].StableKey))
            .OrderBy(u => u.Key, StringComparer.Ordinal)
            .GroupBy(u => OptionCount(problem, occById, u.Ids))
            .OrderBy(g => g.Key)
            .SelectMany(g => g.OrderBy(_ => rng.Next()))
            .Select(u => u.Ids)
            .ToList();

        foreach (var ids in ordered)
        {
            if (!TryPlaceUnit(problem, occById, ids, placed,
                    teacherBusy, groupBusy, wholeBusy, subBusy, roomUsers, teacherDay, classDay, OccKey))
                unplaced.AddRange(ids);
        }
        return new GreedyPlacement(placed, unplaced);
    }

    public static GreedyPlacement Place(SchedulingProblem problem, int seed = 0)
    {
        var occById = problem.Occurrences.ToDictionary(o => o.Id);
        var placed = new Dictionary<Guid, (int Day, int Slot, Guid? RoomId)>();
        var unplaced = new List<Guid>();

        var teacherBusy = new HashSet<(Guid, int, int)>();
        var groupBusy = new HashSet<(Guid, int, int)>(); // occupancy-ключ как в validator
        var wholeBusy = new HashSet<(Guid, int, int)>(); // (classId, day, slot) whole-размещений
        var subBusy = new HashSet<(Guid, int, int)>(); // (classId, day, slot) subgroup-размещений
        var roomUsers = new Dictionary<(Guid, int, int), int>();
        var teacherDay = new Dictionary<(Guid, int), int>();
        // (classId, day) -> занятые слоты (distinct: сплит-час — 1 слот, D-28).
        var classDay = new Dictionary<(Guid, int), HashSet<int>>();

        Guid OccKey(LessonOccurrence o) => o.GroupId ?? o.ClassId;

        // Юниты: sync-группы целиком (INV-03), остальные по одному; порядок —
        // сначала мало вариантов времени, затем StableKey (детерминизм).
        var syncUnits = problem.Occurrences
            .Where(o => o.SyncGroupId.HasValue)
            .GroupBy(o => o.SyncGroupId!.Value)
            .Select(g => (Ids: g.Select(o => o.Id).ToList(), Key: g.First().StableKey))
            .ToList();
        var synced = new HashSet<Guid>(syncUnits.SelectMany(u => u.Ids));
        var singles = problem.Occurrences
            .Where(o => !synced.Contains(o.Id))
            .Select(o => (Ids: new List<Guid> { o.Id }, Key: o.StableKey))
            .ToList();
        var rng = new Random(seed);
        var ordered = syncUnits.Concat(singles)
            .OrderBy(u => u.Key, StringComparer.Ordinal) // детерминированная база
            .GroupBy(u => OptionCount(problem, occById, u.Ids))
            .OrderBy(g => g.Key) // сначала мало вариантов времени
            .SelectMany(g => g.OrderBy(_ => rng.Next())) // seed-тасовка внутри равных
            .Select(u => u.Ids)
            .ToList();

        foreach (var ids in ordered)
        {
            if (!TryPlaceUnit(problem, occById, ids, placed,
                    teacherBusy, groupBusy, wholeBusy, subBusy, roomUsers, teacherDay, classDay, OccKey))
                unplaced.AddRange(ids);
        }
        return new GreedyPlacement(placed, unplaced);
    }

    private static int OptionCount(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        List<Guid> ids)
    {
        // Пересечение allowed-доменов юнита (sync: общее время обязательно).
        HashSet<(int Day, int Slot)>? common = null;
        foreach (var id in ids)
        {
            var days = problem.AllowedDays.GetValueOrDefault(id, []);
            var slots = problem.AllowedSlots.GetValueOrDefault(id, []);
            var set = new HashSet<(int, int)>(days.SelectMany(d => slots.Select(s => (d, s))));
            common = common is null ? set : new HashSet<(int, int)>(common.Where(set.Contains));
        }
        return common?.Count ?? 0;
    }

    private static bool TryPlaceUnit(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        List<Guid> ids,
        Dictionary<Guid, (int Day, int Slot, Guid? RoomId)> placed,
        HashSet<(Guid, int, int)> teacherBusy,
        HashSet<(Guid, int, int)> groupBusy,
        HashSet<(Guid, int, int)> wholeBusy,
        HashSet<(Guid, int, int)> subBusy,
        Dictionary<(Guid, int, int), int> roomUsers,
        Dictionary<(Guid, int), int> teacherDay,
        Dictionary<(Guid, int), HashSet<int>> classDay,
        Func<LessonOccurrence, Guid> occKey)
    {
        var first = occById[ids[0]];
        var days = problem.AllowedDays.GetValueOrDefault(first.Id, []);
        var slots = problem.AllowedSlots.GetValueOrDefault(first.Id, []);
        // D-28: балансировка по дням (least-loaded first) — при СанПиН-кэпах
        // day-major заливка фрагментирует (слак 1–2 клетки на 5×7) и даёт unplaced.
        // D-28c: first-fit (покрытие — главный приоритет; разброс нагрузки учителей
        // пробовали скорингом — режет покрытие 1196→1192, откачено).
        // Детерминировано: (заполнение, день), слоты по возрастанию от якоря.
        foreach (int day in days
                     .OrderBy(d => classDay.GetValueOrDefault((first.ClassId, d))?.Count ?? 0)
                     .ThenBy(d => d))
        {
            foreach (int slot in slots.OrderBy(x => x))
            {
                if (!UnitFits(problem, occById, ids, day, slot,
                        teacherBusy, groupBusy, wholeBusy, subBusy, teacherDay, classDay))
                    continue;
                // Комнаты — независимо по членам юнита (sync делит ВРЕМЯ, не комнату):
                // каждому своя минимально загруженная (детерминированно по имени).
                // Атомарность: сначала все комнаты, потом коммит (откат не нужен).
                var unitUse = new Dictionary<(Guid, int, int), int>();
                var perMember = new Dictionary<Guid, Guid>();
                foreach (var id in ids)
                {
                    var cands = RoomFree(problem, occById[id], day, slot, roomUsers, unitUse);
                    if (cands.Count == 0) { perMember.Clear(); break; }
                    var pick = cands
                        .OrderBy(r => roomUsers.GetValueOrDefault((r, day, slot)) + unitUse.GetValueOrDefault((r, day, slot)))
                        .ThenBy(r => RoomName(problem, r), StringComparer.Ordinal)
                        .First();
                    perMember[id] = pick;
                    if (pick != Guid.Empty)
                        unitUse[(pick, day, slot)] = unitUse.GetValueOrDefault((pick, day, slot)) + 1;
                }
                if (perMember.Count != ids.Count) continue; // в этой клетке мест нет — дальше
                foreach (var id in ids)
                {
                    var o = occById[id];
                    var room = perMember[id];
                    placed[id] = (day, slot, room == Guid.Empty ? null : room);
                    teacherBusy.Add((o.TeacherId, day, slot));
                    groupBusy.Add(((o.GroupId ?? o.ClassId), day, slot));
                    if (!o.GroupId.HasValue) wholeBusy.Add((o.ClassId, day, slot));
                    else subBusy.Add((o.ClassId, day, slot));
                    teacherDay[(o.TeacherId, day)] = teacherDay.GetValueOrDefault((o.TeacherId, day)) + 1;
                    if (!classDay.TryGetValue((o.ClassId, day), out var set))
                        classDay[(o.ClassId, day)] = set = [];
                    set.Add(slot);
                }
                foreach (var kv in unitUse)
                    roomUsers[kv.Key] = roomUsers.GetValueOrDefault(kv.Key) + kv.Value;
                return true;
            }
        }
        return false;
    }

    private static bool UnitFits(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        List<Guid> ids,
        int day, int slot,
        HashSet<(Guid, int, int)> teacherBusy,
        HashSet<(Guid, int, int)> groupBusy,
        HashSet<(Guid, int, int)> wholeBusy,
        HashSet<(Guid, int, int)> subBusy,
        Dictionary<(Guid, int), int> teacherDay,
        Dictionary<(Guid, int), HashSet<int>> classDay)
    {
        // Внутри юнита учителя различны (guard builder), времена общие.
        var unitTeachers = new HashSet<Guid>();
        // Юнит занимает РОВНО 1 слот дня (sync — общее время).
        foreach (var id in ids)
        {
            var o = occById[id];
            if (!problem.AllowedDays.GetValueOrDefault(id, []).Contains(day)) return false;
            if (!problem.AllowedSlots.GetValueOrDefault(id, []).Contains(slot)) return false;
            if (!unitTeachers.Add(o.TeacherId)) return false;
            if (teacherBusy.Contains((o.TeacherId, day, slot))) return false;
            if (groupBusy.Contains(((o.GroupId ?? o.ClassId), day, slot))) return false;
            if (o.GroupId.HasValue && wholeBusy.Contains((o.ClassId, day, slot))) return false;
            if (!o.GroupId.HasValue && subBusy.Contains((o.ClassId, day, slot))) return false;
            if (problem.Teachers.TryGetValue(o.TeacherId, out var t) &&
                teacherDay.GetValueOrDefault((o.TeacherId, day)) + 1 > t.MaxLessonsPerDay)
                return false;
            // СанПиН-кэп класса (D-28): distinct-слоты дня; юнит — 1 слот.
            if (problem.Classes.TryGetValue(o.ClassId, out var cls))
            {
                classDay.TryGetValue((o.ClassId, day), out var used);
                int after = (used is not null && used.Contains(slot)) ? used.Count : (used?.Count ?? 0) + 1;
                if (after > cls.MaxLessonsPerDay)
                    return false;
            }
        }
        return true;
    }

    // Кандидаты-кабинеты с учётом текущей загрузки + уже взятых юнитом (unitUse).
    // Без кабинетов в задаче — sentinel Guid.Empty (RoomId null, как в validator).
    private static List<Guid> RoomFree(
        SchedulingProblem problem, LessonOccurrence o, int day, int slot,
        Dictionary<(Guid, int, int), int> roomUsers,
        Dictionary<(Guid, int, int), int> unitUse)
    {
        if (problem.Rooms.Count == 0) return [Guid.Empty];
        var res = new List<Guid>();
        foreach (var room in problem.Rooms.Values.OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            // P2/R1: фильтр кандидатов общий (RoomPolicy); подсчёт мест —
            // консервативный по размещениям (key-aware — в валидаторе/LS).
            if (!RoomPolicy.IsCandidate(problem, room, o)) continue;
            int used = roomUsers.GetValueOrDefault((room.Id, day, slot)) +
                unitUse.GetValueOrDefault((room.Id, day, slot));
            if (used + 1 > Math.Max(1, room.MaxSimultaneousGroups))
                continue;
            res.Add(room.Id);
        }
        return res;
    }

    private static string RoomName(SchedulingProblem problem, Guid roomId) =>
        roomId == Guid.Empty ? "" : problem.Rooms.TryGetValue(roomId, out var r) ? r.Name : "";
}
