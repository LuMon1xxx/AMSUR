namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// EPIC-H H5.1 — персистентный индекс размещений для быстрого локального поиска.
// Та же семантика, что IncrementalEvaluator (hard-скоупы + soft-дельта), но O(k)
// вместо O(n) на кандидата: индекс строится один раз, TryMove работает только
// с затронутыми группами (класс-день / учитель-день / предмет-день), Commit
// обновляет индекс инкрементально. Паритет с IncrementalEvaluator — тестами (D-08).
// IncrementalEvaluator НЕ меняется (публичный контракт E8-preview, H8).
public sealed class SearchIndex
{
    private readonly SchedulingProblem _problem;
    private readonly Dictionary<Guid, LessonOccurrence> _occById;

    private readonly Dictionary<Guid, (int Day, int Slot, Guid? Room)> _pos = [];

    private readonly Dictionary<(Guid Class, int Day), List<int>> _classSlots = [];
    private readonly Dictionary<(Guid Teacher, int Day), List<int>> _teacherSlots = [];
    private readonly Dictionary<(Guid Class, Guid Subject, int Day), int> _subjCount = [];

    private readonly Dictionary<(Guid Teacher, int Day, int Slot), int> _teacherCell = [];
    private readonly Dictionary<(Guid Class, int Day, int Slot), int> _wholeCell = [];
    private readonly Dictionary<(Guid Class, int Day, int Slot), int> _subCell = [];
    private readonly Dictionary<(Guid Group, int Day, int Slot), int> _groupCell = [];
    private readonly Dictionary<(Guid Room, int Day, int Slot), int> _roomCell = [];
    private readonly Dictionary<(Guid Teacher, int Day), int> _teacherDay = [];
    // P2/R5: классы в клетке — только для комнат с CountSubgroupAsGroup=false
    // (cell → class → счётчик; сплит A/B одного класса = 1 единица).
    private readonly Dictionary<(Guid Room, int Day, int Slot), Dictionary<Guid, int>> _roomKeys = [];
    // P2/R6: счётчик тяжёлых occurrence в клетке класса (для heavy-edge дельты).
    private readonly Dictionary<(Guid Class, int Day, int Slot), int> _heavyCount = [];
    // P2/R7: учителя целых (GroupId==null) по (класс,предмет) и (параллель,предмет).
    private readonly Dictionary<(Guid Class, Guid Subject), Dictionary<Guid, int>> _splitC = [];
    private readonly Dictionary<(int Grade, Guid Subject), Dictionary<Guid, int>> _splitP = [];

    // Настраиваемые веса (S5): дефолт = RuleCatalog v4; движение идёт только через них.
    private long _wStudentGap = RuleCatalog.StudentGap;
    private long _wLate;
    private long _wOrdinary = RuleCatalog.TeacherGap;
    private long _wCross = RuleCatalog.TeacherCrossShiftGap;
    private long _wSubj = RuleCatalog.SubjectMaxPerDay;
    private long _wCrowd = RuleCatalog.RoomCrowding;
    private long _wHeavy = RuleCatalog.HeavyEdge;

    private SearchIndex(SchedulingProblem problem)
    {
        _problem = problem;
        _occById = problem.Occurrences.ToDictionary(o => o.Id);
        _wLate = RuleCatalog.StudentLateStart;
    }

    public static SearchIndex Build(SchedulingProblem problem, IReadOnlyList<PlacedLesson> placements) =>
        Build(problem, placements, null);

    public static SearchIndex Build(
        SchedulingProblem problem, IReadOnlyList<PlacedLesson> placements, EffectiveRuleSet? rules)
    {
        var idx = new SearchIndex(problem);
        if (rules is not null)
        {
            idx._wStudentGap = rules.Weight("student-gap");
            idx._wLate = rules.Weight("student-late-start");
            idx._wOrdinary = rules.Weight("teacher-gap");
            idx._wCross = rules.Weight("teacher-cross-shift-gap");
            idx._wSubj = rules.Weight("subject-maxperday");
            idx._wCrowd = rules.Weight("room-crowding");
            idx._wHeavy = rules.Weight("heavy-edge");
        }
        foreach (var p in placements)
            idx.Insert(p.OccurrenceId, p.DayIndex, p.SlotIndex, p.RoomId);
        return idx;
    }

    public bool Contains(Guid occId) => _pos.ContainsKey(occId);

    public (int Day, int Slot, Guid? Room) Position(Guid occId) => _pos[occId];

    public List<PlacedLesson> Snapshot() => _pos.Select(kv => new PlacedLesson
    {
        OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
        SlotIndex = kv.Value.Slot, RoomId = kv.Value.Room
    }).ToList();

    // Чистый (без мутации): lift N → проверки/дельта против «остальных» → restore.
    public (bool Allowed, long DeltaTotal, IReadOnlyList<ValidationIssue> Hard) TryMove(CandidateMove move)
    {
        if (!_occById.TryGetValue(move.OccurrenceId, out var node))
        {
            var issue = new ValidationIssue { Code = "unknown-occurrence", Message = "Занятие не найдено." };
            return (false, 0, [issue]);
        }
        if (!_pos.TryGetValue(node.Id, out var old))
        {
            var issue = new ValidationIssue
            {
                Code = "unknown-occurrence", Message = "Занятие не размещено.",
                OccurrenceId = node.Id
            };
            return (false, 0, [issue]);
        }

        var hard = new List<ValidationIssue>();
        if (_problem.AllowedDays.TryGetValue(node.Id, out var days) && !days.Contains(move.DayIndex))
            hard.Add(new ValidationIssue
            {
                Code = PhysicalRuleCodes.ShiftDomain,
                Message = "Нельзя поставить в это время (вне сетки/доступности).",
                OccurrenceId = node.Id, ClassId = node.ClassId, TeacherId = node.TeacherId
            });
        if (_problem.AllowedSlots.TryGetValue(node.Id, out var slots) && !slots.Contains(move.SlotIndex))
            hard.Add(new ValidationIssue
            {
                Code = PhysicalRuleCodes.ShiftDomain,
                Message = "Нельзя поставить в этот номер урока.",
                OccurrenceId = node.Id, ClassId = node.ClassId, TeacherId = node.TeacherId
            });
        if (move.RoomId.HasValue &&
            _problem.RoomCaps.TryGetValue((move.RoomId.Value, node.SubjectId), out var cap) &&
            cap == RoomCapabilityKind.Forbidden)
            hard.Add(new ValidationIssue
            {
                Code = "forbidden-room",
                Message = "Кабинет запрещён для этого предмета.",
                OccurrenceId = node.Id, RoomId = move.RoomId
            });

        // Lift: временно убираем N из индекса — дальше все счётчики про «остальных».
        Remove(node, old.Day, old.Slot, old.Room);

        if (hard.Count == 0)
            CheckScopes(node, move, hard);
        long delta = hard.Count == 0 ? SoftDelta(node, old, move) : 0;

        // Restore.
        Insert(node.Id, old.Day, old.Slot, old.Room);

        return (hard.Count == 0, delta, hard);
    }

    public void Commit(CandidateMove move)
    {
        var node = _occById[move.OccurrenceId];
        var old = _pos[node.Id];
        Remove(node, old.Day, old.Slot, old.Room);
        Insert(node.Id, move.DayIndex, move.SlotIndex, move.RoomId);
    }

    // EPIC-H H5.2 — атомарный обмен ВРЕМЕНЕМ двух занятий (комнаты свои).
    // Чистый: lift обоих → проверки/дельта → restore. Одинаковые клетки — no-op.
    public (bool Allowed, long DeltaTotal, IReadOnlyList<ValidationIssue> Hard) TrySwap(Guid aId, Guid bId)
    {
        if (aId == bId) return (false, 0, []);
        if (!_occById.TryGetValue(aId, out var a) || !_occById.TryGetValue(bId, out var b))
            return (false, 0, [new ValidationIssue { Code = "unknown-occurrence", Message = "Занятие не найдено." }]);
        if (!_pos.TryGetValue(aId, out var pa) || !_pos.TryGetValue(bId, out var pb))
            return (false, 0, [new ValidationIssue { Code = "unknown-occurrence", Message = "Занятие не размещено." }]);
        if (pa.Day == pb.Day && pa.Slot == pb.Slot) return (false, 0, []); // обмен времени — no-op

        var hard = new List<ValidationIssue>();
        // Домены: каждому — чужая клетка.
        CheckDomain(a, pb.Day, pb.Slot, hard);
        CheckDomain(b, pa.Day, pa.Slot, hard);
        CheckRoomCap(a, pa.Room, pb.Day, pb.Slot, hard);
        CheckRoomCap(b, pb.Room, pa.Day, pa.Slot, hard);

        Remove(a, pa.Day, pa.Slot, pa.Room);
        Remove(b, pb.Day, pb.Slot, pb.Room);

        if (hard.Count == 0)
        {
            CheckScopes(a, new CandidateMove(aId, pb.Day, pb.Slot, pa.Room), hard);
            if (hard.Count == 0)
                CheckScopes(b, new CandidateMove(bId, pa.Day, pa.Slot, pb.Room), hard);
        }
        long delta = 0;
        if (hard.Count == 0)
            delta = SoftDelta(a, pa, new CandidateMove(aId, pb.Day, pb.Slot, pa.Room))
                + SoftDelta(b, pb, new CandidateMove(bId, pa.Day, pa.Slot, pb.Room));

        Insert(aId, pa.Day, pa.Slot, pa.Room);
        Insert(bId, pb.Day, pb.Slot, pb.Room);
        return (hard.Count == 0, delta, hard);
    }

    public void CommitSwap(Guid aId, Guid bId)
    {
        var pa = _pos[aId];
        var pb = _pos[bId];
        var a = _occById[aId];
        var b = _occById[bId];
        Remove(a, pa.Day, pa.Slot, pa.Room);
        Remove(b, pb.Day, pb.Slot, pb.Room);
        Insert(aId, pb.Day, pb.Slot, pa.Room);
        Insert(bId, pa.Day, pa.Slot, pb.Room);
    }

    private void CheckDomain(LessonOccurrence node, int day, int slot, List<ValidationIssue> hard)
    {
        if (_problem.AllowedDays.TryGetValue(node.Id, out var days) && !days.Contains(day))
            hard.Add(new ValidationIssue
            {
                Code = PhysicalRuleCodes.ShiftDomain,
                Message = "Нельзя поставить в это время (вне сетки/доступности).",
                OccurrenceId = node.Id, ClassId = node.ClassId, TeacherId = node.TeacherId
            });
        if (_problem.AllowedSlots.TryGetValue(node.Id, out var slots) && !slots.Contains(slot))
            hard.Add(new ValidationIssue
            {
                Code = PhysicalRuleCodes.ShiftDomain,
                Message = "Нельзя поставить в этот номер урока.",
                OccurrenceId = node.Id, ClassId = node.ClassId, TeacherId = node.TeacherId
            });
    }

    private void CheckRoomCap(LessonOccurrence node, Guid? room, int day, int slot, List<ValidationIssue> hard)
    {
        if (room.HasValue &&
            _problem.RoomCaps.TryGetValue((room.Value, node.SubjectId), out var cap) &&
            cap == RoomCapabilityKind.Forbidden)
            hard.Add(new ValidationIssue
            {
                Code = "forbidden-room",
                Message = "Кабинет запрещён для этого предмета.",
                OccurrenceId = node.Id, RoomId = room
            });
        // P2/R1: ONLY-кабинет — чужой предмет запрещён и вручную (hard).
        // IsManualOnly здесь НЕ проверяем: ручное назначение разрешено.
        if (room.HasValue &&
            _problem.Rooms.TryGetValue(room.Value, out var rm) &&
            rm.OnlySubjectId.HasValue && rm.OnlySubjectId.Value != node.SubjectId)
            hard.Add(new ValidationIssue
            {
                Code = "forbidden-room",
                Message = $"Кабинет «{rm.Name}» — только для своего предмета.",
                OccurrenceId = node.Id, RoomId = room
            });
    }

    private void CheckScopes(LessonOccurrence node, CandidateMove move, List<ValidationIssue> hard)
    {
        if (_teacherCell.ContainsKey((node.TeacherId, move.DayIndex, move.SlotIndex)))
            hard.Add(new ValidationIssue
            {
                Code = PhysicalRuleCodes.TeacherCollision,
                Message = "Учитель уже ведёт урок в это время.",
                TeacherId = node.TeacherId, OccurrenceId = node.Id
            });

        bool blocked = node.GroupId.HasValue
            ? _wholeCell.ContainsKey((node.ClassId, move.DayIndex, move.SlotIndex)) ||
              _groupCell.ContainsKey((node.GroupId.Value, move.DayIndex, move.SlotIndex))
            : _wholeCell.ContainsKey((node.ClassId, move.DayIndex, move.SlotIndex)) ||
              _subCell.ContainsKey((node.ClassId, move.DayIndex, move.SlotIndex));
        if (blocked)
            hard.Add(new ValidationIssue
            {
                Code = PhysicalRuleCodes.GroupCollision,
                Message = "Класс/группа уже заняты в это время.",
                ClassId = node.ClassId, OccurrenceId = node.Id
            });

        if (move.RoomId.HasValue && _problem.Rooms.TryGetValue(move.RoomId.Value, out var room))
        {
            // P2/R5: единицы key-aware (сплит одним классом = 1 при флаге false).
            // Индекс — БЕЗ N (lift): +1 за двигаемый урок.
            int units = CellUnitsWith(room,
                (move.RoomId.Value, move.DayIndex, move.SlotIndex), node.ClassId);
            if (units > room.MaxSimultaneousGroups)
                hard.Add(new ValidationIssue
                {
                    Code = PhysicalRuleCodes.RoomOverflow,
                    Message = $"Кабинет занят ({units} при лимите {room.MaxSimultaneousGroups}).",
                    RoomId = room.Id, OccurrenceId = node.Id
                });
        }

        // P2/R7: один учитель на (класс,предмет) / (параллель,предмет).
        // Ходы учителей не меняют (INV-02) → проверяем текущую группу с N.
        var mode = _problem.Flex.AssignMode;
        if (!node.GroupId.HasValue &&
            mode is TeacherAssignMode.HardClass or TeacherAssignMode.HardParallel)
        {
            if (SplitDistinctWith(node) > 1)
                hard.Add(new ValidationIssue
                {
                    Code = "teacher-assign",
                    Message = "Предмет в классе/параллели ведут несколько учителей.",
                    ClassId = node.ClassId, OccurrenceId = node.Id, TeacherId = node.TeacherId
                });
        }

        if (node.SyncGroupId.HasValue)
        {
            foreach (var mate in _problem.Occurrences.Where(o => o.SyncGroupId == node.SyncGroupId && o.Id != node.Id))
            {
                if (!_pos.TryGetValue(mate.Id, out var mp)) continue;
                if (mp.Day != move.DayIndex || mp.Slot != move.SlotIndex)
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

        if (_problem.Teachers.TryGetValue(node.TeacherId, out var teacher))
        {
            int count = _teacherDay.GetValueOrDefault((node.TeacherId, move.DayIndex)) + 1;
            if (count > teacher.MaxLessonsPerDay)
                hard.Add(new ValidationIssue
                {
                    Code = "teacher-maxperday",
                    Message = $"Превышен дневной лимит учителя ({count} > {teacher.MaxLessonsPerDay}).",
                    TeacherId = node.TeacherId
                });
        }

        // СанПиН-кэп класса (D-28): distinct-слоты дня (сплит-час — 1 слот).
        // Индекс БЕЗ N (lift) → считаем distinct БЕЗ N плюс целевой слот.
        if (_problem.Classes.TryGetValue(node.ClassId, out var cls))
        {
            var list = _classSlots.GetValueOrDefault((node.ClassId, move.DayIndex), []);
            int count = list.Contains(move.SlotIndex) ? new HashSet<int>(list).Count : new HashSet<int>(list).Count + 1;
            if (count > cls.MaxLessonsPerDay)
                hard.Add(new ValidationIssue
                {
                    Code = "class-maxperday",
                    Message = $"Превышена дневная норма класса ({count} > {cls.MaxLessonsPerDay}).",
                    ClassId = node.ClassId
                });
        }
    }

    private long SoftDelta(LessonOccurrence node, (int Day, int Slot, Guid? Room) old, CandidateMove move)
    {
        long delta = 0;
        // P2/R8: вес параллели класса (student-сторона; учителя/кабинеты — без веса).
        int gw = _problem.Flex.WeightForGrade(
            _problem.Classes.TryGetValue(node.ClassId, out var clsg) ? clsg.Grade : 0);
        // Индекс сейчас — БЕЗ N (lift): old-множества уже без старого слота.
        int anchor = StudentCompactness.AnchorFor(_problem, node.ClassId);
        var csOld = _classSlots.GetValueOrDefault((node.ClassId, old.Day), []);
        var csNew = _classSlots.GetValueOrDefault((node.ClassId, move.DayIndex), []);
        delta += (GapAfter(csNew, move.SlotIndex) - GapOf(csNew)) * _wStudentGap * gw;
        delta += (GapOf(csOld) - GapBefore(csOld, old.Slot)) * _wStudentGap * gw;
        delta += (LateAfter(csNew, move.SlotIndex, anchor) - LateOf(csNew, anchor)) * _wLate * gw;
        delta += (LateOf(csOld, anchor) - LateBefore(csOld, old.Slot, anchor)) * _wLate * gw;
        // P2/R6 heavy-edge дня (× вес параллели).
        bool nodeHeavy = IsHeavy(node);
        delta += (HeavyWith(csNew, node.ClassId, move.DayIndex, move.SlotIndex, nodeHeavy) -
                  HeavyWithout(csNew, node.ClassId, move.DayIndex)) * _wHeavy * gw;
        delta += (HeavyWithout(csOld, node.ClassId, old.Day) -
                  HeavyWith(csOld, node.ClassId, old.Day, old.Slot, nodeHeavy)) * _wHeavy * gw;

        var tsOld = _teacherSlots.GetValueOrDefault((node.TeacherId, old.Day), []);
        var tsNew = _teacherSlots.GetValueOrDefault((node.TeacherId, move.DayIndex), []);
        delta += TeacherCostAfter(tsNew, move.SlotIndex) - TeacherCostOf(tsNew);
        delta += TeacherCostOf(tsOld) - TeacherCostBefore(tsOld, old.Slot);

        if (_problem.Subjects.TryGetValue(node.SubjectId, out var subj))
        {
            int newC = _subjCount.GetValueOrDefault((node.ClassId, node.SubjectId, move.DayIndex));
            int oldC = _subjCount.GetValueOrDefault((node.ClassId, node.SubjectId, old.Day));
            // Lift: счётчики БЕЗ N. Было (с N): oldC+1 / newC+1; стало: oldC / newC+1.
            delta += (Excess(newC + 1, subj.MaxPerDay) - Excess(newC, subj.MaxPerDay)) * _wSubj * gw;
            delta += (Excess(oldC, subj.MaxPerDay) - Excess(oldC + 1, subj.MaxPerDay)) * _wSubj * gw;
        }

        // P2/R5 room-crowding клеток (без веса параллели; снятие −, установка +).
        if (old.Room.HasValue && _problem.Rooms.TryGetValue(old.Room.Value, out var oldRoom))
            delta -= CrowdStep(oldRoom, (old.Room.Value, old.Day, old.Slot), node.ClassId) * _wCrowd;
        if (move.RoomId.HasValue && _problem.Rooms.TryGetValue(move.RoomId.Value, out var newRoom))
            delta += CrowdStep(newRoom, (move.RoomId.Value, move.DayIndex, move.SlotIndex), node.ClassId) * _wCrowd;
        // P2/R7 teacher-split: состав учителей ходами не меняется (INV-02) → дельта 0.
        return delta;
    }

    // P2/R6: единицы heavy-edge дня БЕЗ N / С N (зеркало SoftUnits.HeavyEdge).
    private int HeavyWithout(List<int> slots, Guid classId, int day) =>
        SoftUnits.HeavyEdge(slots, s => _heavyCount.GetValueOrDefault((classId, day, s)) > 0);

    private int HeavyWith(List<int> slots, Guid classId, int day, int slot, bool nodeHeavy) =>
        SoftUnits.HeavyEdge([.. slots, slot], s =>
            s == slot
                ? nodeHeavy || _heavyCount.GetValueOrDefault((classId, day, s)) > 0
                : _heavyCount.GetValueOrDefault((classId, day, s)) > 0);

    // P2/R5: шаг тесноты клетки С N минус БЕЗ N (индекс БЕЗ N).
    private int CellUnitsWithout(Room room, (Guid Room, int Day, int Slot) cell)
    {
        if (room.CountSubgroupAsGroup) return _roomCell.GetValueOrDefault(cell);
        if (!_roomKeys.TryGetValue(cell, out var ks)) return 0;
        int n = 0;
        foreach (int cnt in ks.Values) if (cnt > 0) n++;
        return n;
    }

    private long CrowdStep(Room room, (Guid Room, int Day, int Slot) cell, Guid classId)
    {
        int desired = RoomPolicy.EffectiveDesired(room);
        return SoftUnits.Crowding(CellUnitsWith(room, cell, classId), desired) -
               SoftUnits.Crowding(CellUnitsWithout(room, cell), desired);
    }

    // Взвешенная стоимость teacher-day через GapUtils (ordinary + cross, S5).
    private long TeacherCostOf(List<int> without)
    {
        var (ord, cross, _) = GapUtils.SplitTeacherDay(without, _problem.ShiftBands);
        return ord * _wOrdinary + cross * _wCross;
    }

    private long TeacherCostBefore(List<int> without, int slot)
    {
        var l = new List<int>(without) { slot };
        return TeacherCostOf(l);
    }

    private long TeacherCostAfter(List<int> without, int slot)
    {
        var l = new List<int>(without) { slot };
        return TeacherCostOf(l);
    }

    // Зеркало арифметики SoftEvaluator (включая дубликаты слотов и guard gap>0):
    // GapOf(list) == вклад группы БЕЗ N; GapBefore(list, s) == вклад С N (s добавлен).
    // Списки содержат дубликаты сплит-пар — GapOf работает по DISTINCT (D-28d).
    private static int GapOf(List<int> slots)
    {
        if (slots.Count <= 1) return 0;
        var d = slots.Distinct().OrderBy(s => s).ToList();
        if (d.Count <= 1) return 0;
        int gap = (d[^1] - d[0] + 1) - d.Count;
        return gap > 0 ? gap : 0;
    }

    private static int GapBefore(List<int> without, int slot)
    {
        if (without.Count == 0) return 0; // было только N → вклада не было (count<=1)
        var l = new List<int>(without) { slot };
        l.Sort();
        return GapOf(l);
    }

    private static int GapAfter(List<int> without, int slot)
    {
        var l = new List<int>(without) { slot };
        l.Sort();
        return GapOf(l);
    }

    // Зеркало LatePenalty SoftEvaluator: LateOf(list) == вклад группы БЕЗ N.
    private static int LateOf(List<int> slots, int anchor) =>
        StudentCompactness.LateExcess(slots, anchor);

    private static int LateBefore(List<int> without, int slot, int anchor)
    {
        // В отличие от окон, одинокий урок поздний старт сохраняет → guard запрещён.
        var l = new List<int>(without) { slot };
        l.Sort();
        return StudentCompactness.LateExcess(l, anchor);
    }

    private static int LateAfter(List<int> without, int slot, int anchor)
    {
        var l = new List<int>(without) { slot };
        l.Sort();
        return StudentCompactness.LateExcess(l, anchor);
    }

    private static int Excess(int count, int maxPerDay) =>
        count > maxPerDay ? count - maxPerDay : 0;

    private void Insert(Guid occId, int day, int slot, Guid? room)
    {
        var node = _occById[occId];
        _pos[occId] = (day, slot, room);
        SortedInsert(_classSlots.GetOrAdd((node.ClassId, day)), slot);
        SortedInsert(_teacherSlots.GetOrAdd((node.TeacherId, day)), slot);
        var sk = (node.ClassId, node.SubjectId, day);
        _subjCount[sk] = _subjCount.GetValueOrDefault(sk) + 1;
        Bump(_teacherCell, (node.TeacherId, day, slot));
        if (node.GroupId.HasValue)
        {
            Bump(_subCell, (node.ClassId, day, slot));
            Bump(_groupCell, (node.GroupId.Value, day, slot));
        }
        else Bump(_wholeCell, (node.ClassId, day, slot));
        if (room.HasValue) Bump(_roomCell, (room.Value, day, slot));
        var tk = (node.TeacherId, day);
        _teacherDay[tk] = _teacherDay.GetValueOrDefault(tk) + 1;
        // P2: тяжёлые клетки, key-aware ключи комнат, split-индекс целых.
        if (IsHeavy(node))
            Bump(_heavyCount, (node.ClassId, day, slot));
        if (room.HasValue && _problem.Rooms.TryGetValue(room.Value, out var rm) && !rm.CountSubgroupAsGroup)
        {
            var cell = (room.Value, day, slot);
            if (!_roomKeys.TryGetValue(cell, out var ks)) _roomKeys[cell] = ks = [];
            ks[node.ClassId] = ks.GetValueOrDefault(node.ClassId) + 1;
        }
        if (!node.GroupId.HasValue)
        {
            var ck = (node.ClassId, node.SubjectId);
            if (!_splitC.TryGetValue(ck, out var cd)) _splitC[ck] = cd = [];
            cd[node.TeacherId] = cd.GetValueOrDefault(node.TeacherId) + 1;
            if (_problem.Classes.TryGetValue(node.ClassId, out var cls))
            {
                var pk = (cls.Grade, node.SubjectId);
                if (!_splitP.TryGetValue(pk, out var pd)) _splitP[pk] = pd = [];
                pd[node.TeacherId] = pd.GetValueOrDefault(node.TeacherId) + 1;
            }
        }
    }

    private void Remove(LessonOccurrence node, int day, int slot, Guid? room)
    {
        _pos.Remove(node.Id);
        SortedRemove(_classSlots[(node.ClassId, day)], slot);
        SortedRemove(_teacherSlots[(node.TeacherId, day)], slot);
        var sk = (node.ClassId, node.SubjectId, day);
        _subjCount[sk] = _subjCount[sk] - 1;
        Drop(_teacherCell, (node.TeacherId, day, slot));
        if (node.GroupId.HasValue)
        {
            Drop(_subCell, (node.ClassId, day, slot));
            Drop(_groupCell, (node.GroupId.Value, day, slot));
        }
        else Drop(_wholeCell, (node.ClassId, day, slot));
        if (room.HasValue) Drop(_roomCell, (room.Value, day, slot));
        var tk = (node.TeacherId, day);
        _teacherDay[tk] = _teacherDay[tk] - 1;
        // P2: откат структур выше.
        if (IsHeavy(node))
            Drop(_heavyCount, (node.ClassId, day, slot));
        if (room.HasValue && _problem.Rooms.TryGetValue(room.Value, out var rm) && !rm.CountSubgroupAsGroup)
        {
            var cell = (room.Value, day, slot);
            if (_roomKeys.TryGetValue(cell, out var ks))
            {
                int v = ks.GetValueOrDefault(node.ClassId) - 1;
                if (v <= 0) ks.Remove(node.ClassId);
                else ks[node.ClassId] = v;
                if (ks.Count == 0) _roomKeys.Remove(cell);
            }
        }
        if (!node.GroupId.HasValue)
        {
            var ck = (node.ClassId, node.SubjectId);
            if (_splitC.TryGetValue(ck, out var cd))
            {
                int v = cd.GetValueOrDefault(node.TeacherId) - 1;
                if (v <= 0) cd.Remove(node.TeacherId);
                else cd[node.TeacherId] = v;
            }
            if (_problem.Classes.TryGetValue(node.ClassId, out var cls))
            {
                var pk = (cls.Grade, node.SubjectId);
                if (_splitP.TryGetValue(pk, out var pd))
                {
                    int v = pd.GetValueOrDefault(node.TeacherId) - 1;
                    if (v <= 0) pd.Remove(node.TeacherId);
                    else pd[node.TeacherId] = v;
                }
            }
        }
    }

    // P2: тяжёлый ли occurrence (R6: Difficulty >= порога).
    private bool IsHeavy(LessonOccurrence node) =>
        _problem.Subjects.TryGetValue(node.SubjectId, out var s) &&
        s.Difficulty >= _problem.Flex.IsHeavyThreshold;

    // P2/R5: единицы клетки с двигаемым уроком (индекс БЕЗ N → +N вручную).
    private int CellUnitsWith(Domain.Room room, (Guid Room, int Day, int Slot) cell, Guid classId)
    {
        if (room.CountSubgroupAsGroup)
            return _roomCell.GetValueOrDefault(cell) + 1;
        int n = 0;
        bool has = false;
        if (_roomKeys.TryGetValue(cell, out var ks))
            foreach (var (c, cnt) in ks)
                if (cnt > 0)
                {
                    n++;
                    if (c == classId) has = true;
                }
        return n + (has ? 0 : 1);
    }

    // P2/R7: distinct учителей целой группы узла (индекс БЕЗ N → +N вручную).
    private int SplitDistinctWith(LessonOccurrence node)
    {
        var mode = _problem.Flex.AssignMode;
        if (mode == Domain.TeacherAssignMode.HardParallel &&
            _problem.Classes.TryGetValue(node.ClassId, out var cls) &&
            _splitP.TryGetValue((cls.Grade, node.SubjectId), out var pd))
            return pd.Count + (pd.ContainsKey(node.TeacherId) ? 0 : 1);
        if (mode == Domain.TeacherAssignMode.HardClass &&
            _splitC.TryGetValue((node.ClassId, node.SubjectId), out var cd))
            return cd.Count + (cd.ContainsKey(node.TeacherId) ? 0 : 1);
        return 1;
    }

    private static void SortedInsert(List<int> list, int slot)
    {
        int i = list.BinarySearch(slot);
        list.Insert(i < 0 ? ~i : i, slot);
    }

    private static void SortedRemove(List<int> list, int slot)
    {
        int i = list.BinarySearch(slot);
        if (i >= 0) list.RemoveAt(i);
    }

    private static void Bump(Dictionary<(Guid, int, int), int> d, (Guid, int, int) k) =>
        d[k] = d.GetValueOrDefault(k) + 1;

    private static void Drop(Dictionary<(Guid, int, int), int> d, (Guid, int, int) k)
    {
        int v = d[k] - 1;
        if (v <= 0) d.Remove(k);
        else d[k] = v;
    }
}

file static class DictExt
{
    internal static List<int> GetOrAdd(this Dictionary<(Guid, int), List<int>> d, (Guid, int) k)
    {
        if (!d.TryGetValue(k, out var l)) d[k] = l = [];
        return l;
    }
}
