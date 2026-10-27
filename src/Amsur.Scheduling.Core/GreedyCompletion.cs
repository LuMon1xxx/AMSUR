namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// D-61 completion: досев неразмещённых greedy-юнитов поверх старта — порт
// пайплайна ScratchGen (прямая постановка → каскад с точечным выселением ≤8,
// sync-группы целиком, внеурочка/классный час не трогаем) на быстрых
// множествах занятости (по образцу GreedyPlacer.UnitFits/RoomFree) вместо
// полного PlacementValidator на каждую клетку (~50× быстрее при той же
// защите hard-коллизий/капов/кабинетов). Как и GreedyPlacer: детерминирован
// сидом, честно частичен — финальный гейт остаётся PlacementValidator (INV-01).
public static class GreedyCompletion
{
    // НДТП-7: внеурочка (IsExtra / IsNonLesson) — уроком не считается:
    // дневные капы не тратит; выселять её нельзя (ручная зона завуча).
    private static bool IsCounted(SchedulingProblem problem, LessonOccurrence o) =>
        !o.IsExtra &&
        !(problem.Subjects.TryGetValue(o.SubjectId, out var s) && s.IsNonLesson);

    private static bool IsProtected(SchedulingProblem problem, LessonOccurrence o) =>
        o.IsExtra ||
        (problem.Subjects.TryGetValue(o.SubjectId, out var s) && s.IsNonLesson);

    /// <summary>
    /// Досев: прямая постановка каждого юнита, затем каскад с выселением
    /// конфликтующих (≤8, sync-партнёры целиком, внеурочка защищена).
    /// Возвращает полный старт (размещённые + досеянные; остаток — Unplaced).
    /// </summary>
    public static GreedyPlacement Complete(SchedulingProblem problem, GreedyPlacement start, int seed)
    {
        if (start.Unplaced.Count == 0) return start;
        var occById = problem.Occurrences.ToDictionary(o => o.Id);
        var pos = new Dictionary<Guid, PlacedLesson>(start.Placed.Count);
        foreach (var kv in start.Placed)
            pos[kv.Key] = new PlacedLesson
            {
                OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
                SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId,
            };
        var occ = new Occupancy();
        foreach (var kv in start.Placed)
            occ.Add(problem, occById, kv.Key, pos[kv.Key]);

        // Юниты: sync-группы целиком (INV-03), внутри — StableKey (детерминизм);
        // порядок — сначала мало вариантов времени, seed-тасовка внутри равных
        // (как GreedyPlacer.Place).
        var units = start.Unplaced
            .GroupBy(id => occById[id].SyncGroupId ?? id)
            .Select(g => g.OrderBy(id => occById[id].StableKey, StringComparer.Ordinal).ToList())
            .ToList();
        var rng = new Random(seed);
        var ordered = units
            .GroupBy(u => DomainSize(problem, occById, u))
            .OrderBy(g => g.Key)
            .SelectMany(g => g.OrderBy(_ => rng.Next()))
            .ToList();

        var failed = new List<List<Guid>>();
        foreach (var u in ordered)
            if (!PlaceUnitDirect(problem, occById, pos, u, occ))
                failed.Add(u);
        var still = new List<List<Guid>>();
        foreach (var u in failed)
            if (!PlaceUnitCascade(problem, occById, pos, u, occ))
                still.Add(u);

        return new GreedyPlacement(
            pos.ToDictionary(
                kv => kv.Key,
                kv => (kv.Value.DayIndex, kv.Value.SlotIndex, kv.Value.RoomId)),
            still.SelectMany(u => u).ToList());
    }

    private static int DomainSize(
        SchedulingProblem problem, Dictionary<Guid, LessonOccurrence> occById, List<Guid> unit)
    {
        var cells = Cells(problem, occById, unit);
        return cells.Count;
    }

    // Пересечение allowed-доменов членов юнита (sync: общее время обязательно).
    private static HashSet<(int Day, int Slot)> Cells(
        SchedulingProblem problem, Dictionary<Guid, LessonOccurrence> occById, List<Guid> unit)
    {
        var cells = new HashSet<(int, int)>(
            problem.AllowedDays.GetValueOrDefault(unit[0], [])
                .SelectMany(d => problem.AllowedSlots.GetValueOrDefault(unit[0], [])
                    .Select(s => (d, s))));
        foreach (var id in unit.Skip(1))
        {
            var set = new HashSet<(int, int)>(
                problem.AllowedDays.GetValueOrDefault(id, [])
                    .SelectMany(d => problem.AllowedSlots.GetValueOrDefault(id, [])
                        .Select(s => (d, s))));
            cells.IntersectWith(set);
        }
        return cells;
    }

    // Прямая постановка: первая клетка, проходящая fast-гейты (коллизии/капы/
    // кабинеты) — порядок клеток по приросту ученических окон (порт GapDelta).
    private static bool PlaceUnitDirect(
        SchedulingProblem problem, Dictionary<Guid, LessonOccurrence> occById,
        Dictionary<Guid, PlacedLesson> pos, List<Guid> unit, Occupancy occ)
    {
        foreach (var (d, s) in OrderedCells(problem, occById, occ, unit, Cells(problem, occById, unit)))
            if (occ.TryCell(problem, occById, unit, d, s, pos))
                return true;
        return false;
    }

    // Каскад: точечное выселение только конфликтующих в клетке + по одному
    // для переполненных day-капов; sync-партнёры выселяемых — целиком;
    // внеурочка защищена, лимит выселения 8; выгнанные пересеиваются прямым
    // методом. Откат целиком при любой неудаче (порт ScratchGen, fast-гейты).
    private static bool PlaceUnitCascade(
        SchedulingProblem problem, Dictionary<Guid, LessonOccurrence> occById,
        Dictionary<Guid, PlacedLesson> pos, List<Guid> unit, Occupancy occ)
    {
        foreach (var (d, s) in OrderedCells(problem, occById, occ, unit, Cells(problem, occById, unit)))
        {
            var evict = new HashSet<Guid>();
            foreach (var mid in unit)
            {
                var mo = occById[mid];
                Guid mkey = mo.GroupId ?? mo.ClassId;
                foreach (var pid in occ.CellIds.GetValueOrDefault((d, s), []))
                {
                    var o = occById[pid];
                    if (o.TeacherId == mo.TeacherId) evict.Add(pid);
                    else if ((o.GroupId ?? o.ClassId) == mkey) evict.Add(pid);
                    else if (o.ClassId == mo.ClassId &&
                             (o.GroupId.HasValue != mo.GroupId.HasValue))
                        evict.Add(pid);
                }
                // Учительский дневной кап: +1 сверх — выселить один его урок дня.
                if (problem.Teachers.TryGetValue(mo.TeacherId, out var tt) &&
                    CountedTeacherDay(problem, occById, occ, mo.TeacherId, d) + 1 > tt.MaxLessonsPerDay)
                {
                    foreach (var pid in occ.TeacherDayIds.GetValueOrDefault((mo.TeacherId, d), []))
                        if (IsCounted(problem, occById[pid])) { evict.Add(pid); break; }
                }
                // Дневной кап класса (distinct-слоты): выселить один урок дня.
                var use = occ.ClassSlotUse.GetValueOrDefault((mo.ClassId, d));
                int cslots = (use?.Count ?? 0) + (use is not null && use.ContainsKey(s) ? 0 : 1);
                if (problem.Classes.TryGetValue(mo.ClassId, out var cc) && cslots > cc.MaxLessonsPerDay)
                {
                    foreach (var pid in occ.ClassDayIds.GetValueOrDefault((mo.ClassId, d), []))
                        if (IsCounted(problem, occById[pid])) { evict.Add(pid); break; }
                }
            }
            // Sync-партнёры выселяемых — целиком.
            foreach (var id in evict.ToList())
            {
                var o = occById[id];
                if (o.SyncGroupId.HasValue)
                    foreach (var pid in pos.Values
                                 .Where(p => occById[p.OccurrenceId].SyncGroupId == o.SyncGroupId)
                                 .Select(p => p.OccurrenceId))
                        evict.Add(pid);
            }
            if (evict.Count > 8) continue;
            if (evict.Any(id => IsProtected(problem, occById[id]))) continue;

            var saved = evict.ToDictionary(id => id, id => pos[id]);
            foreach (var id in evict)
            {
                occ.Remove(problem, occById, id, pos[id]);
                pos.Remove(id);
            }
            if (!occ.TryCell(problem, occById, unit, d, s, pos))
            {
                foreach (var kv in saved)
                {
                    pos[kv.Key] = kv.Value;
                    occ.Add(problem, occById, kv.Key, kv.Value);
                }
                continue;
            }
            var evUnits = evict.GroupBy(id => occById[id].SyncGroupId ?? id)
                .Select(g => g.ToList()).ToList();
            bool allBack = true;
            var added = new List<Guid>();
            foreach (var eu in evUnits)
            {
                if (!PlaceUnitDirect(problem, occById, pos, eu, occ))
                {
                    allBack = false;
                    break;
                }
                added.AddRange(eu);
            }
            if (allBack) return true;
            foreach (var id in added.Concat(unit))
            {
                occ.Remove(problem, occById, id, pos[id]);
                pos.Remove(id);
            }
            foreach (var kv in saved)
            {
                pos[kv.Key] = kv.Value;
                occ.Add(problem, occById, kv.Key, kv.Value);
            }
        }
        return false;
    }

    private static int CountedTeacherDay(
        SchedulingProblem problem, Dictionary<Guid, LessonOccurrence> occById,
        Occupancy occ, Guid teacher, int day) =>
        occ.TeacherDayIds.GetValueOrDefault((teacher, day), [])
            .Count(id => IsCounted(problem, occById[id]));

    // Прирост окон/позднего старта ученика от постановки юнита в (d,s) —
    // для выбора клетки (порт GapDelta из ScratchGen; слоты — distinct-вид
    // валидатора через ClassSlotUse).
    private static int GapDelta(
        SchedulingProblem problem, Occupancy occ, Guid classId, int day, int slot)
    {
        var use = occ.ClassSlotUse.GetValueOrDefault((classId, day));
        List<int> cur = use is null ? [] : use.Keys.OrderBy(x => x).ToList();
        int anchor = StudentCompactness.AnchorFor(problem, classId);
        int before = cur.Count == 0 ? 0 :
            StudentCompactness.GapOf(cur) + StudentCompactness.LateExcess(cur, anchor);
        var after = cur.Append(slot).OrderBy(x => x).ToList();
        return StudentCompactness.GapOf(after) +
               StudentCompactness.LateExcess(after, anchor) - before;
    }

    private static IEnumerable<(int Day, int Slot)> OrderedCells(
        SchedulingProblem problem, Dictionary<Guid, LessonOccurrence> occById,
        Occupancy occ, List<Guid> unit, HashSet<(int Day, int Slot)> cells)
    {
        var classId = occById[unit[0]].ClassId;
        return cells
            .Select(c => (c.Day, c.Slot, Gap: GapDelta(problem, occ, classId, c.Day, c.Slot)))
            .OrderBy(t => t.Gap).ThenBy(t => t.Day).ThenBy(t => t.Slot)
            .Select(t => (t.Day, t.Slot));
    }

    /// <summary>
    /// Занятость с обратимыми операциями (выселение): refcount-словари вместо
    /// HashSet — сплит-пары делят клетку, удаление одного члена не должно
    /// стирать занятость другого. Ключи — как в validator/GreedyPlacer.
    /// </summary>
    private sealed class Occupancy
    {
        public Dictionary<(Guid, int, int), int> TeacherBusy = [];
        public Dictionary<(Guid, int, int), int> GroupBusy = [];
        public Dictionary<(Guid, int, int), int> WholeBusy = [];
        public Dictionary<(Guid, int, int), int> SubBusy = [];
        public Dictionary<(Guid, int, int), int> RoomUsers = [];
        public Dictionary<(Guid, int), List<Guid>> TeacherDayIds = [];
        public Dictionary<(Guid, int), List<Guid>> ClassDayIds = [];
        // (classId, day) -> slot -> refcount СЧИТАЕМЫХ уроков (валидаторный взгляд).
        public Dictionary<(Guid, int), Dictionary<int, int>> ClassSlotUse = [];
        public Dictionary<(int, int), List<Guid>> CellIds = [];

        public void Add(
            SchedulingProblem problem, Dictionary<Guid, LessonOccurrence> occById,
            Guid id, PlacedLesson p)
        {
            var o = occById[id];
            int d = p.DayIndex, s = p.SlotIndex;
            Inc(TeacherBusy, (o.TeacherId, d, s));
            Inc(GroupBusy, ((o.GroupId ?? o.ClassId), d, s));
            if (o.GroupId.HasValue) Inc(SubBusy, (o.ClassId, d, s));
            else Inc(WholeBusy, (o.ClassId, d, s));
            if (p.RoomId.HasValue) Inc(RoomUsers, (p.RoomId.Value, d, s));
            if (!TeacherDayIds.TryGetValue((o.TeacherId, d), out var tl))
                TeacherDayIds[(o.TeacherId, d)] = tl = [];
            tl.Add(id);
            if (!ClassDayIds.TryGetValue((o.ClassId, d), out var cl))
                ClassDayIds[(o.ClassId, d)] = cl = [];
            cl.Add(id);
            if (!CellIds.TryGetValue((d, s), out var cell))
                CellIds[(d, s)] = cell = [];
            cell.Add(id);
            if (IsCounted(problem, o))
            {
                if (!ClassSlotUse.TryGetValue((o.ClassId, d), out var use))
                    ClassSlotUse[(o.ClassId, d)] = use = [];
                use[s] = use.GetValueOrDefault(s) + 1;
            }
        }

        public void Remove(
            SchedulingProblem problem, Dictionary<Guid, LessonOccurrence> occById,
            Guid id, PlacedLesson p)
        {
            var o = occById[id];
            int d = p.DayIndex, s = p.SlotIndex;
            Dec(TeacherBusy, (o.TeacherId, d, s));
            Dec(GroupBusy, ((o.GroupId ?? o.ClassId), d, s));
            if (o.GroupId.HasValue) Dec(SubBusy, (o.ClassId, d, s));
            else Dec(WholeBusy, (o.ClassId, d, s));
            if (p.RoomId.HasValue) Dec(RoomUsers, (p.RoomId.Value, d, s));
            TeacherDayIds.GetValueOrDefault((o.TeacherId, d), [])?.Remove(id);
            ClassDayIds.GetValueOrDefault((o.ClassId, d), [])?.Remove(id);
            CellIds.GetValueOrDefault((d, s), [])?.Remove(id);
            if (IsCounted(problem, o))
            {
                var use = ClassSlotUse.GetValueOrDefault((o.ClassId, d));
                if (use is not null && use.TryGetValue(s, out var n))
                {
                    if (n <= 1) use.Remove(s);
                    else use[s] = n - 1;
                }
            }
        }

        /// <summary>Fast-гейт клетки + коммит: коллизии (учитель/группа/
        /// whole-sub), дневные капы (IsCounted-семантика), кабинеты
        /// (кандидаты + вместимость, детерминированно по имени). Атомарно.</summary>
        public bool TryCell(
            SchedulingProblem problem, Dictionary<Guid, LessonOccurrence> occById,
            List<Guid> unit, int day, int slot,
            Dictionary<Guid, PlacedLesson> pos)
        {
            var unitTeachers = new HashSet<Guid>();
            foreach (var id in unit)
            {
                var o = occById[id];
                if (!problem.AllowedDays.GetValueOrDefault(id, []).Contains(day)) return false;
                if (!problem.AllowedSlots.GetValueOrDefault(id, []).Contains(slot)) return false;
                if (!unitTeachers.Add(o.TeacherId)) return false;
                if (TeacherBusy.ContainsKey((o.TeacherId, day, slot))) return false;
                if (GroupBusy.ContainsKey(((o.GroupId ?? o.ClassId), day, slot))) return false;
                if (o.GroupId.HasValue && WholeBusy.ContainsKey((o.ClassId, day, slot))) return false;
                if (!o.GroupId.HasValue && SubBusy.ContainsKey((o.ClassId, day, slot))) return false;
                if (problem.Teachers.TryGetValue(o.TeacherId, out var t) &&
                    CountedTeacherDay(problem, occById, this, o.TeacherId, day) + 1 > t.MaxLessonsPerDay)
                    return false;
                if (IsCounted(problem, o) &&
                    problem.Classes.TryGetValue(o.ClassId, out var cls))
                {
                    var use = ClassSlotUse.GetValueOrDefault((o.ClassId, day));
                    int after = (use is not null && use.ContainsKey(slot)) ? use.Count : (use?.Count ?? 0) + 1;
                    if (after > cls.MaxLessonsPerDay)
                        return false;
                }
            }
            // Кабинеты: каждому члену своя минимально загруженная клетка
            // (RoomFree-образец; без кабинетов — RoomId null, как в validator).
            var unitUse = new Dictionary<(Guid, int, int), int>();
            var perMember = new Dictionary<Guid, Guid?>();
            foreach (var id in unit)
            {
                if (problem.Rooms.Count == 0)
                {
                    perMember[id] = null;
                    continue;
                }
                var o = occById[id];
                var cands = new List<Room>();
                foreach (var room in problem.Rooms.Values.OrderBy(r => r.Name, StringComparer.Ordinal))
                {
                    if (!RoomPolicy.IsCandidate(problem, room, o)) continue;
                    int used = RoomUsers.GetValueOrDefault((room.Id, day, slot)) +
                        unitUse.GetValueOrDefault((room.Id, day, slot));
                    if (used + 1 > Math.Max(1, room.MaxSimultaneousGroups))
                        continue;
                    cands.Add(room);
                }
                if (cands.Count == 0) return false;
                var pick = cands
                    .OrderBy(r => RoomUsers.GetValueOrDefault((r.Id, day, slot)) +
                        unitUse.GetValueOrDefault((r.Id, day, slot)))
                    .ThenBy(r => r.Name, StringComparer.Ordinal)
                    .First();
                perMember[id] = pick.Id;
                unitUse[(pick.Id, day, slot)] = unitUse.GetValueOrDefault((pick.Id, day, slot)) + 1;
            }
            // Коммит (все проверки выше прошли — откат не нужен).
            foreach (var id in unit)
            {
                var room = perMember[id];
                var p = new PlacedLesson
                {
                    OccurrenceId = id, DayIndex = day, SlotIndex = slot, RoomId = room,
                };
                pos[id] = p;
                Add(problem, occById, id, p);
            }
            return true;
        }

        private static void Inc(Dictionary<(Guid, int, int), int> dict, (Guid, int, int) key) =>
            dict[key] = dict.GetValueOrDefault(key) + 1;

        private static void Dec(Dictionary<(Guid, int, int), int> dict, (Guid, int, int) key)
        {
            if (dict.TryGetValue(key, out var n))
            {
                if (n <= 1) dict.Remove(key);
                else dict[key] = n - 1;
            }
        }
    }
}
