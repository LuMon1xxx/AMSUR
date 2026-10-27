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
    // НДТП-7/фикс часа: классный час (внеурочка) дневной кап класса
    // не тратит — ни сам не блокируется капом, ни слот его не занимает
    // в счётчике. Учительских лимитов не касается (там час не в счёт
    // не идёт — у классного руководителя 1 занятие, лимит не жмёт).
    private static bool IsCounted(SchedulingProblem problem, LessonOccurrence o) =>
        !o.IsExtra &&
        !(problem.Subjects.TryGetValue(o.SubjectId, out var s) && s.IsNonLesson);
    /// <summary>D-50 шаг ②: компакт-подсказка для пересева — упаковать день
    /// учителя (день первым + слоты вплотную к его занятиям). null = как раньше.</summary>
    public sealed record CompactHint(Guid Teacher, int Day);

    /// <summary>Фаза 1 (DayClose): подсказка переиспользования дней — сначала
    /// ОТКРЫТЫЕ дни учителя (где teacherDay &gt; 0), потом остальные; внутри дня
    /// обычный порядок. Opt-in: null = бит-в-бит старое поведение.</summary>
    public sealed record DayReuseHint(Guid Teacher);

    /// <summary>Фаза E (CrossShift): держать учителя в одной смене — слоты
    /// мажоритарной смены дня первыми; ничья — пропуск цели (естественный порядок).
    /// Opt-in: null = бит-в-бит старое поведение.</summary>
    public sealed record ShiftCohesionHint();

    // LNS-пересев (D-28d): досеять заданные юниты поверх ЗАМОРОЖЕННОГО состояния
    // (занятость от kept-размещений). Детерминирован сидом. Частичен честно.
    internal static GreedyPlacement Replant(
        SchedulingProblem problem,
        IReadOnlyDictionary<Guid, (int Day, int Slot, Guid? RoomId)> frozen,
        List<List<Guid>> units,
        int seed,
        CompactHint? hint = null,
        DayReuseHint? reuse = null,
        bool anchorSlots = false,
        ShiftCohesionHint? shift = null)
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
            if (IsCounted(problem, o))
            {
                if (!classDay.TryGetValue((o.ClassId, kv.Value.Day), out var set))
                    classDay[(o.ClassId, kv.Value.Day)] = set = [];
                set.Add(kv.Value.Slot);
            }
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
                    teacherBusy, groupBusy, wholeBusy, subBusy, roomUsers, teacherDay, classDay, OccKey, hint,
                    reuse: reuse, anchorSlots: anchorSlots, shift: shift))
                unplaced.AddRange(ids);
        }
        return new GreedyPlacement(placed, unplaced);
    }

    public static GreedyPlacement Place(SchedulingProblem problem, int seed = 0) =>
        Place(problem, seed, compact: false);

    /// <summary>D-50 шаг ③: compact=true — слоты вплотную к занятиям того же
    /// учителя в тот же день (плотный старт для teacher-LNS). default false =
    /// бит-в-бит старое поведение (D-28c: покрытие — главный приоритет).</summary>
    /// <summary>Фаза 3 (bin-pack): dayReuse=true — day-ordering как в DayReuseHint
    /// для ВСЕХ учителей с первого размещения. default false = старое поведение.</summary>
    public static GreedyPlacement Place(SchedulingProblem problem, int seed, bool compact, bool dayReuse = false)
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
                    teacherBusy, groupBusy, wholeBusy, subBusy, roomUsers, teacherDay, classDay, OccKey, null, compact,
                    dayReuse: dayReuse))
                unplaced.AddRange(ids);
        }
        // D-61: тяжёлым — внутренние слоты смены (пост-пасс, покрытие не меняет).
        PreferHeavyInterior(problem, occById, placed, teacherBusy, groupBusy, wholeBusy, subBusy,
            roomUsers, classDay);
        // D-61: сдвоенность пост-пассом — пары ставим рядом, не теряя покрытие.
        PairUpAdjacent(problem, occById, placed, teacherBusy, groupBusy, wholeBusy, subBusy,
            roomUsers, classDay);
        // НДТП-7: разнос физкультуры по дням (без троек подряд).
        SpreadPe(problem, occById, placed, teacherBusy, groupBusy, wholeBusy, subBusy,
            roomUsers, teacherDay, classDay);
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
        Func<LessonOccurrence, Guid> occKey,
        CompactHint? hint = null,
        bool compact = false,
        DayReuseHint? reuse = null,
        bool anchorSlots = false,
        ShiftCohesionHint? shift = null,
        bool dayReuse = false)
    {
        var first = occById[ids[0]];
        var days = problem.AllowedDays.GetValueOrDefault(first.Id, []);
        var slots = problem.AllowedSlots.GetValueOrDefault(first.Id, []);
        // D-28: балансировка по дням (least-loaded first) — при СанПиН-кэпах
        // day-major заливка фрагментирует (слак 1–2 клетки на 5×7) и даёт unplaced.
        // D-28c: first-fit (покрытие — главный приоритет; разброс нагрузки учителей
        // пробовали скорингом — режет покрытие 1196→1192, откачено).
        // Детерминировано: (заполнение, день), слоты по возрастанию от якоря.
        // D-50: hint==null → порядок бит-в-бит как раньше (Place и старый Replant
        // не меняются); hint → день учителя первым + слоты вплотную к его занятиям.
        bool pack = hint is not null && first.TeacherId == hint.Teacher;
        // D-50 шаг ③: compact-режим — слоты вплотную к своим же занятиям дня
        // для ЛЮБОГО учителя (плотный старт); day-порядок при этом обычный.
        bool adj = pack || compact;
        // Фаза 1/2 (DayClose/Sync-joint): reuse покрывает юнит, если reuse-учитель
        // среди участников (для одиночки — равенство; для sync — любой участник).
        // Фаза 3: dayReuse — тот же day-ordering для всех учителей с первого размещения.
        var unitTeachers = ids.Select(id => occById[id].TeacherId).ToHashSet();
        bool reuseApplies = reuse is not null && unitTeachers.Contains(reuse.Teacher);
        HashSet<Guid>? openTeachers = (reuseApplies || dayReuse) ? unitTeachers : null;
        IEnumerable<int> orderedDays;
        if (openTeachers is not null)
        {
            // Открытые дни (где ВСЕ участники уже заняты) первыми, затем
            // класс-заполнение как было, затем день. Sync-joint: дни, где все
            // участники открыты.
            orderedDays = days
                .OrderBy(d => openTeachers.All(t => teacherDay.GetValueOrDefault((t, d)) > 0) ? 0 : 1)
                .ThenBy(d => classDay.GetValueOrDefault((first.ClassId, d))?.Count ?? 0)
                .ThenBy(d => d);
        }
        else orderedDays = pack
            ? days.OrderBy(d => d == hint!.Day ? 0 : 1)
                .ThenBy(d => classDay.GetValueOrDefault((first.ClassId, d))?.Count ?? 0)
                .ThenBy(d => d)
            : days.OrderBy(d => classDay.GetValueOrDefault((first.ClassId, d))?.Count ?? 0)
                .ThenBy(d => d);
        foreach (int day in orderedDays)
        {
            // Якорные учителя для слот-близости: anchorSlots+reuse — занятия
            // reuse-учителя (вплотную к ним); reuse/sync-joint — все участники
            // (слот смежный со всеми — якорь, не hard); pack/compact — свой учитель.
            List<Guid> anchorTeachers;
            if (anchorSlots && reuse is not null)
                anchorTeachers = [reuse.Teacher];
            else if (reuseApplies)
                anchorTeachers = unitTeachers.OrderBy(t => t).ToList();
            else if (adj)
                anchorTeachers = [first.TeacherId];
            else
                anchorTeachers = [];
            var busyByTeacher = anchorTeachers.ToDictionary(
                t => t,
                t => teacherBusy.Where(k => k.Item1 == t && k.Item2 == day)
                    .Select(k => k.Item3).ToList());
            bool hasAnchor = busyByTeacher.Values.Any(v => v.Count > 0);
            // Фаза E: слоты мажоритарной смены дня первыми; ничья — пропуск цели.
            int majorityBand = -1;
            if (shift is not null && problem.ShiftBands is { Count: >= 2 })
            {
                var bandCount = new Dictionary<int, int>();
                foreach (var k in teacherBusy)
                {
                    if (k.Item2 != day) continue;
                    int b = GapUtils.ShiftIndex(k.Item3, problem.ShiftBands);
                    if (b < 0) continue;
                    bandCount[b] = bandCount.GetValueOrDefault(b) + 1;
                }
                if (bandCount.Count > 0)
                {
                    int top = bandCount.Values.Max();
                    var leaders = bandCount.Where(kv => kv.Value == top).Select(kv => kv.Key).ToList();
                    if (leaders.Count == 1) majorityBand = leaders[0];
                }
            }
            int CohesionRank(int s) =>
                majorityBand < 0 ? 0 :
                GapUtils.ShiftIndex(s, problem.ShiftBands) == majorityBand ? 0 : 1;
            int AdjScore(int s)
            {
                if (!hasAnchor) return 0;
                // Близость ко ВСЕМ якорным (худшая дистанция первой):
                // одиночка сводится к старой формуле min-дистанции.
                int worst = 0;
                foreach (var t in anchorTeachers)
                {
                    var mine = busyByTeacher[t];
                    if (mine.Count == 0) continue;
                    int d = mine.Min(tt => Math.Abs(tt - s));
                    if (d > worst) worst = d;
                }
                return worst;
            }
            // D-61: предпочтение внутренних слотов смены для тяжёлых предметов
            // делается пост-пассом (PreferHeavyInterior) — порядок слотов не трогаем,
            // иначе страдает покрытие (проверено OurIdeal: 880/882).
            IEnumerable<int> orderedSlots = slots
                .OrderBy(CohesionRank)
                .ThenBy(AdjScore)
                .ThenBy(x => x);
            foreach (int slot in orderedSlots)
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
                    if (IsCounted(problem, o))
                    {
                        if (!classDay.TryGetValue((o.ClassId, day), out var set))
                            classDay[(o.ClassId, day)] = set = [];
                        set.Add(slot);
                    }
                }
                foreach (var kv in unitUse)
                    roomUsers[kv.Key] = roomUsers.GetValueOrDefault(kv.Key) + kv.Value;
                return true;
            }
        }
        return false;
    }

    // D-61: тяжёлые предметы — внутренние слоты смены (не 1-й и не последний),
    // пост-пассом: переставляем урок внутри его дня, если внутренний слот свободен.
    // Порядок слотов при упаковке не трогаем — иначе падает покрытие (OurIdeal 880/882).
    private static void PreferHeavyInterior(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        Dictionary<Guid, (int Day, int Slot, Guid? RoomId)> placed,
        HashSet<(Guid, int, int)> teacherBusy,
        HashSet<(Guid, int, int)> groupBusy,
        HashSet<(Guid, int, int)> wholeBusy,
        HashSet<(Guid, int, int)> subBusy,
        Dictionary<(Guid, int, int), int> roomUsers,
        Dictionary<(Guid, int), HashSet<int>> classDay)
    {
        foreach (var id in placed.Keys.OrderBy(k => occById[k].StableKey, StringComparer.Ordinal).ToList())
        {
            var o = occById[id];
            if (o.SyncGroupId.HasValue) continue; // sync живёт одним слотом
            if (!problem.Subjects.TryGetValue(o.SubjectId, out var s) ||
                s.Difficulty < problem.Flex.IsHeavyThreshold) continue;
            int slot = placed[id].Slot;
            int b = GapUtils.ShiftIndex(slot, problem.ShiftBands);
            if (b < 0) continue;
            var band = problem.ShiftBands[b];
            if (slot != band.FromSlot && slot != band.ToSlot) continue; // уже внутренний
            foreach (int target in Enumerable.Range(band.FromSlot, band.ToSlot - band.FromSlot + 1)
                         .Where(x => x != band.FromSlot && x != band.ToSlot)
                         .OrderBy(x => x))
                if (TryShiftTo(problem, occById, placed, id, target,
                        teacherBusy, groupBusy, wholeBusy, subBusy, roomUsers, classDay))
                    break;
        }
    }

    // D-61: сдвоенность пост-пассом. Для каждого (класс, день, предмет) с 2+
    // уроками двигаем урок вплотную к соседнему того же предмета. Слот
    // освобождается, другой занимается — набор занятых клеток класса не растёт,
    // поэтому покрытие и дневные капы не страдают (в отличие от бонуса в
    // порядке слотов, который ронял Grind/Staffing).
    private static void PairUpAdjacent(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        Dictionary<Guid, (int Day, int Slot, Guid? RoomId)> placed,
        HashSet<(Guid, int, int)> teacherBusy,
        HashSet<(Guid, int, int)> groupBusy,
        HashSet<(Guid, int, int)> wholeBusy,
        HashSet<(Guid, int, int)> subBusy,
        Dictionary<(Guid, int, int), int> roomUsers,
        Dictionary<(Guid, int), HashSet<int>> classDay)
    {
        var groups = placed
            .Where(kv =>
            {
                var o = occById[kv.Key];
                if (o.SyncGroupId.HasValue) return false; // sync-пара живёт одним слотом
                return !(problem.Subjects.TryGetValue(o.SubjectId, out var s) &&
                         (s.IsPhysicalEducation || s.IsNonLesson));
            })
            .GroupBy(kv => (Class: occById[kv.Key].ClassId, kv.Value.Day, Subject: occById[kv.Key].SubjectId))
            .OrderBy(g => g.Key.Class).ThenBy(g => g.Key.Day).ThenBy(g => g.Key.Subject)
            .ToList();
        foreach (var g in groups)
        {
            var items = g.OrderBy(kv => kv.Value.Slot).Select(kv => kv.Key).ToList();
            for (int i = 1; i < items.Count; i++)
            {
                var prevId = items[i - 1];
                var curId = items[i];
                int prevSlot = placed[prevId].Slot;
                int curSlot = placed[curId].Slot;
                if (curSlot - prevSlot == 1) continue;
                if (TryShiftTo(problem, occById, placed, curId, prevSlot + 1,
                        teacherBusy, groupBusy, wholeBusy, subBusy, roomUsers, classDay))
                    continue;
                TryShiftTo(problem, occById, placed, curId, prevSlot - 1,
                    teacherBusy, groupBusy, wholeBusy, subBusy, roomUsers, classDay);
            }
        }
        RebuildClassDay(problem, occById, placed, classDay);
    }

    // Переставить урок внутри его дня в конкретный слот, если клетка свободна
    // для учителя/группы/класса и в кабинете есть место. Иначе false.
    private static bool TryShiftTo(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        Dictionary<Guid, (int Day, int Slot, Guid? RoomId)> placed,
        Guid id, int toSlot,
        HashSet<(Guid, int, int)> teacherBusy,
        HashSet<(Guid, int, int)> groupBusy,
        HashSet<(Guid, int, int)> wholeBusy,
        HashSet<(Guid, int, int)> subBusy,
        Dictionary<(Guid, int, int), int> roomUsers,
        Dictionary<(Guid, int), HashSet<int>> classDay)
    {
        var (day, slot, room) = placed[id];
        var o = occById[id];
        if (slot == toSlot) return false;
        if (!problem.AllowedSlots.GetValueOrDefault(id, []).Contains(toSlot)) return false;
        if (teacherBusy.Contains((o.TeacherId, day, toSlot))) return false;
        if (groupBusy.Contains(((o.GroupId ?? o.ClassId), day, toSlot))) return false;
        var cls = occById[id].ClassId;
        if (wholeBusy.Contains((cls, day, toSlot)) || subBusy.Contains((cls, day, toSlot))) return false;
        if (classDay.GetValueOrDefault((cls, day))?.Contains(toSlot) == true) return false;
        if (room.HasValue &&
            problem.Rooms.TryGetValue(room.Value, out var r) &&
            roomUsers.GetValueOrDefault((room.Value, day, toSlot)) + 1 > Math.Max(1, r.MaxSimultaneousGroups))
            return false;

        // Снять со старого слота (соседи по группе/кабинету не затрагиваются).
        teacherBusy.Remove((o.TeacherId, day, slot));
        groupBusy.Remove(((o.GroupId ?? o.ClassId), day, slot));
        if (o.GroupId.HasValue) subBusy.Remove((cls, day, slot));
        else wholeBusy.Remove((cls, day, slot));
        bool otherSameClassAtOld = placed.Any(p =>
            p.Key != id && occById[p.Key].ClassId == cls && p.Value.Day == day &&
            p.Value.Slot == slot && IsCounted(problem, occById[p.Key]));
        if (!otherSameClassAtOld && IsCounted(problem, o))
            classDay.GetValueOrDefault((cls, day))?.Remove(slot);
        if (room.HasValue && roomUsers.TryGetValue((room.Value, day, slot), out var n))
        {
            if (n <= 1) roomUsers.Remove((room.Value, day, slot));
            else roomUsers[(room.Value, day, slot)] = n - 1;
        }
        // Поставить на новый.
        teacherBusy.Add((o.TeacherId, day, toSlot));
        groupBusy.Add(((o.GroupId ?? o.ClassId), day, toSlot));
        if (o.GroupId.HasValue) subBusy.Add((cls, day, toSlot));
        else wholeBusy.Add((cls, day, toSlot));
        if (IsCounted(problem, o))
        {
            if (!classDay.TryGetValue((cls, day), out var cset)) classDay[(cls, day)] = cset = [];
            cset.Add(toSlot);
        }
        if (room.HasValue)
            roomUsers[(room.Value, day, toSlot)] =
                roomUsers.GetValueOrDefault((room.Value, day, toSlot)) + 1;
        placed[id] = (day, toSlot, room);
        return true;
    }

    private static void RebuildClassDay(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        Dictionary<Guid, (int Day, int Slot, Guid? RoomId)> placed,
        Dictionary<(Guid, int), HashSet<int>> classDay)
    {
        classDay.Clear();
        foreach (var kv in placed)
        {
            var o = occById[kv.Key];
            if (!IsCounted(problem, o)) continue;
            if (!classDay.TryGetValue((o.ClassId, kv.Value.Day), out var set))
                classDay[(o.ClassId, kv.Value.Day)] = set = [];
            set.Add(kv.Value.Slot);
        }
    }

    // НДТП-7: точечный разнос физкультуры — если у класса 3 дня ПЕ подряд,
    // переносим один урок тройки на свободный день. Мягко: нет места — оставляем
    // (финальный гейт — валидатор).
    private static void SpreadPe(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        Dictionary<Guid, (int Day, int Slot, Guid? RoomId)> placed,
        HashSet<(Guid, int, int)> teacherBusy,
        HashSet<(Guid, int, int)> groupBusy,
        HashSet<(Guid, int, int)> wholeBusy,
        HashSet<(Guid, int, int)> subBusy,
        Dictionary<(Guid, int, int), int> roomUsers,
        Dictionary<(Guid, int), int> teacherDay,
        Dictionary<(Guid, int), HashSet<int>> classDay)
    {
        var classes = placed.Keys.Select(id => occById[id].ClassId).Distinct()
            .OrderBy(c => problem.Classes.TryGetValue(c, out var cl) ? cl.Name : "", StringComparer.Ordinal)
            .ToList();
        foreach (var classId in classes)
        {
            for (int guard = 0; guard < 8; guard++)
            {
                var pe = placed
                    .Where(kv =>
                    {
                        var o = occById[kv.Key];
                        return o.ClassId == classId && !o.SyncGroupId.HasValue &&
                               problem.Subjects.TryGetValue(o.SubjectId, out var s) && s.IsPhysicalEducation;
                    })
                    .Select(kv => (kv.Key, kv.Value.Day))
                    .OrderBy(x => x.Day).ThenBy(x => x.Key)
                    .ToList();
                if (pe.Count < 3) break;
                var peDays = pe.Select(x => x.Day).Distinct().OrderBy(d => d).ToList();
                int tri = -1;
                for (int i = 2; i < peDays.Count; i++)
                    if (peDays[i] == peDays[i - 1] + 1 && peDays[i - 1] == peDays[i - 2] + 1)
                    {
                        tri = i;
                        break;
                    }
                if (tri < 0) break;
                bool moved = false;
                foreach (var (id, day) in pe.Where(x => x.Day == peDays[tri]).OrderBy(x => x.Key))
                    if (TryMoveToDay(problem, occById, placed, id, peDays.Where(d => d != day).ToList(),
                            teacherBusy, groupBusy, wholeBusy, subBusy, roomUsers, teacherDay, classDay))
                    {
                        moved = true;
                        break;
                    }
                if (!moved) break;
            }
        }
    }

    private static bool HasTripleDays(IEnumerable<int> days)
    {
        var ds = days.Distinct().OrderBy(x => x).ToList();
        for (int i = 2; i < ds.Count; i++)
            if (ds[i] == ds[i - 1] + 1 && ds[i - 1] == ds[i - 2] + 1) return true;
        return false;
    }

    private static bool TryMoveToDay(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        Dictionary<Guid, (int Day, int Slot, Guid? RoomId)> placed,
        Guid id, List<int> peDays,
        HashSet<(Guid, int, int)> teacherBusy,
        HashSet<(Guid, int, int)> groupBusy,
        HashSet<(Guid, int, int)> wholeBusy,
        HashSet<(Guid, int, int)> subBusy,
        Dictionary<(Guid, int, int), int> roomUsers,
        Dictionary<(Guid, int), int> teacherDay,
        Dictionary<(Guid, int), HashSet<int>> classDay)
    {
        var o = occById[id];
        var (oldDay, oldSlot, room) = placed[id];
        var allowedDays = problem.AllowedDays.GetValueOrDefault(id, []);
        var allowedSlots = problem.AllowedSlots.GetValueOrDefault(id, []);
        bool counted = IsCounted(problem, o);
        var candidates = Enumerable.Range(0, problem.DaysCount)
            .Where(d => d != oldDay && allowedDays.Contains(d))
            .Where(d => !HasTripleDays(peDays.Append(d)))
            .OrderBy(d => classDay.GetValueOrDefault((o.ClassId, d))?.Count ?? 0)
            .ThenBy(d => d);
        foreach (int day in candidates)
        {
            if (counted && problem.Classes.TryGetValue(o.ClassId, out var cls) &&
                (classDay.GetValueOrDefault((o.ClassId, day))?.Count ?? 0) + 1 > cls.MaxLessonsPerDay)
                continue;
            if (counted && problem.Teachers.TryGetValue(o.TeacherId, out var t) &&
                teacherDay.GetValueOrDefault((o.TeacherId, day)) + 1 > t.MaxLessonsPerDay)
                continue;
            foreach (int slot in allowedSlots.OrderBy(x => x))
            {
                if (teacherBusy.Contains((o.TeacherId, day, slot))) continue;
                if (groupBusy.Contains(((o.GroupId ?? o.ClassId), day, slot))) continue;
                if (wholeBusy.Contains((o.ClassId, day, slot)) || subBusy.Contains((o.ClassId, day, slot)))
                    continue;
                if (classDay.GetValueOrDefault((o.ClassId, day))?.Contains(slot) == true) continue;
                if (room.HasValue && problem.Rooms.TryGetValue(room.Value, out var r) &&
                    roomUsers.GetValueOrDefault((room.Value, day, slot)) + 1 > Math.Max(1, r.MaxSimultaneousGroups))
                    continue;
                // Перенос: снять со старого дня/слота.
                teacherBusy.Remove((o.TeacherId, oldDay, oldSlot));
                groupBusy.Remove(((o.GroupId ?? o.ClassId), oldDay, oldSlot));
                if (o.GroupId.HasValue) subBusy.Remove((o.ClassId, oldDay, oldSlot));
                else wholeBusy.Remove((o.ClassId, oldDay, oldSlot));
                if (room.HasValue && roomUsers.TryGetValue((room.Value, oldDay, oldSlot), out var n))
                {
                    if (n <= 1) roomUsers.Remove((room.Value, oldDay, oldSlot));
                    else roomUsers[(room.Value, oldDay, oldSlot)] = n - 1;
                }
                teacherDay[(o.TeacherId, oldDay)] = Math.Max(0, teacherDay.GetValueOrDefault((o.TeacherId, oldDay)) - 1);
                if (counted && classDay.TryGetValue((o.ClassId, oldDay), out var oldSet)) oldSet.Remove(oldSlot);
                // Поставить на новый.
                teacherBusy.Add((o.TeacherId, day, slot));
                groupBusy.Add(((o.GroupId ?? o.ClassId), day, slot));
                if (o.GroupId.HasValue) subBusy.Add((o.ClassId, day, slot));
                else wholeBusy.Add((o.ClassId, day, slot));
                if (room.HasValue)
                    roomUsers[(room.Value, day, slot)] = roomUsers.GetValueOrDefault((room.Value, day, slot)) + 1;
                teacherDay[(o.TeacherId, day)] = teacherDay.GetValueOrDefault((o.TeacherId, day)) + 1;
                if (counted)
                {
                    if (!classDay.TryGetValue((o.ClassId, day), out var nset)) classDay[(o.ClassId, day)] = nset = [];
                    nset.Add(slot);
                }
                placed[id] = (day, slot, room);
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
            // НДТП-7/фикс часа: сам час капом не режется и слотов не занимает.
            if (IsCounted(problem, o) && problem.Classes.TryGetValue(o.ClassId, out var cls))
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

