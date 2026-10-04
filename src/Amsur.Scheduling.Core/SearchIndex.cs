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
    // D-51: eligible-слоты дубля по K=(класс,предмет,день) (для doubles-adjacency дельты).
    private readonly Dictionary<(Guid Class, Guid Subject, int Day), List<int>> _dblSlots = [];
    // НДТП-7: edge-once структуры (только counted edge-предметы):
    // слоты ключа по дням + ключи классо-дня (для кросс-ключевого пересчёта).
    private readonly Dictionary<(Guid Class, Guid Subject, int Day), List<int>> _keyDaySlots = [];
    private readonly Dictionary<(Guid Class, int Day), HashSet<(Guid Class, Guid Subject)>> _classDayKeys = [];
    // PE-видимость: счётчик физры класса по дням (только counted).
    private readonly Dictionary<(Guid Class, int Day), int> _peDays = [];
    // НДТП-7: члены ключа (класс,предмет) — статический состав occurrences.
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
    private long _wActiveDay = RuleCatalog.TeacherActiveDay;
    private long _wDoubles = RuleCatalog.DoublesAdjacency;
    private long _wPeak = RuleCatalog.PeakDays;
    private long _wEdgeOnce = RuleCatalog.EdgeOnce;
    private long _wAlternation = RuleCatalog.Alternation;
    private long _wPe = RuleCatalog.PeConsecutive;

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
            idx._wActiveDay = rules.Weight("teacher-active-day");
            idx._wDoubles = rules.Weight("doubles-adjacency");
            idx._wPeak = rules.Weight("peak-days");
            idx._wEdgeOnce = rules.Weight("edge-once");
            idx._wAlternation = rules.Weight("alternation");
            idx._wPe = rules.Weight("pe-consecutive");
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

        // НДТП-7: слепок не нужен — своп-дельта считается по truth (_pos),
        // слепки инкрементальных структур для week-Эксцесса хрупки.
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
            // НДТП-7: edge-once идёт точным своп-пересчётом (week-Эксцесс не
            // раскладывается в сумму двух синглов) — из сингл-дельт исключён.
            delta = SoftDelta(a, pa, new CandidateMove(aId, pb.Day, pb.Slot, pa.Room), includeEdge: false, includePe: false)
                + SoftDelta(b, pb, new CandidateMove(bId, pa.Day, pa.Slot, pb.Room), includeEdge: false, includePe: false)
                + EdgeSwapDelta(a, pa, b, pb)
                + PeSwapDelta(a, pa, b, pb);

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
        // НДТП-7: внеурочка не проверяется (классный руководитель ≠ предметник).
        var mode = _problem.Flex.AssignMode;
        if (IsCounted(node) && !node.GroupId.HasValue &&
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

        // НДТП-7: дневной лимит учителя — только уроки (внеурочка не в счёт).
        if (IsCounted(node) && _problem.Teachers.TryGetValue(node.TeacherId, out var teacher))
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
        // НДТП-7: внеурочка не в счёт.
        if (IsCounted(node) && _problem.Classes.TryGetValue(node.ClassId, out var cls))
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

    private long SoftDelta(LessonOccurrence node, (int Day, int Slot, Guid? Room) old, CandidateMove move, bool includeEdge = true, bool includePe = true)
    {
        long delta = 0;
        // P2/R8: вес параллели класса (student-сторона; учителя/кабинеты — без веса).
        int gw = _problem.Flex.WeightForGrade(
            _problem.Classes.TryGetValue(node.ClassId, out var clsg) ? clsg.Grade : 0);
        // НДТП-7: внеурочка в мягких термах не участвует — только физика кабинетов.
        if (!IsCounted(node))
        {
            if (old.Room.HasValue && _problem.Rooms.TryGetValue(old.Room.Value, out var or0))
                delta -= CrowdStep(or0, (old.Room.Value, old.Day, old.Slot), node.ClassId) * _wCrowd;
            if (move.RoomId.HasValue && _problem.Rooms.TryGetValue(move.RoomId.Value, out var nr0))
                delta += CrowdStep(nr0, (move.RoomId.Value, move.DayIndex, move.SlotIndex), node.ClassId) * _wCrowd;
            return delta;
        }
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
        // D-50 teacher-active-day: день активен, пока в нём есть хоть 1 занятие.
        // Индекс БЕЗ N: old-день был активен (там стояло N); new-день станет активен.
        // Same-day ход: обе поправки схлопываются в 0 (день остаётся занят).
        if (_wActiveDay != 0)
        {
            if (tsOld.Count == 0) delta -= _wActiveDay; // старый день освобождается
            if (tsNew.Count == 0) delta += _wActiveDay; // новый день занимается
        }

        if (_problem.Subjects.TryGetValue(node.SubjectId, out var subj))
        {
            int newC = _subjCount.GetValueOrDefault((node.ClassId, node.SubjectId, move.DayIndex));
            int oldC = _subjCount.GetValueOrDefault((node.ClassId, node.SubjectId, old.Day));
            // Lift: счётчики БЕЗ N. Было (с N): oldC+1 / newC+1; стало: oldC / newC+1.
            delta += (Excess(newC + 1, subj.MaxPerDay) - Excess(newC, subj.MaxPerDay)) * _wSubj * gw;
            delta += (Excess(oldC, subj.MaxPerDay) - Excess(oldC + 1, subj.MaxPerDay)) * _wSubj * gw;
        }

        // D-51 doubles-adjacency дня (× вес параллели, INV-D8).
        // Lift-паттерн как heavy-edge: списки БЕЗ N; With = список + слот N.
        // INV-D4: guards запрещены — считаем всегда, независимо от subject-maxperday.
        // INV-D6: room-only ход (тот же день+слот) схлопывается в 0.
        if (_wDoubles != 0 && SoftUnits.IsDoubleEligible(node))
        {
            var oldKey = (node.ClassId, node.SubjectId, old.Day);
            var newKey = (node.ClassId, node.SubjectId, move.DayIndex);
            var lNew = _dblSlots.GetValueOrDefault(newKey, []);
            var lOld = _dblSlots.GetValueOrDefault(oldKey, []);
            delta += (DblWith(lNew, move.SlotIndex) - DblWithout(lNew)) * _wDoubles * gw;
            delta += (DblWithout(lOld) - DblWith(lOld, old.Slot)) * _wDoubles * gw;
        }

        // НДТП-7: пик Вт/Ср/Пт — тяжёлый урок вне пика (день-гранулярность).
        if (_wPeak != 0 && IsHeavy(node))
            delta += (SoftUnits.PeakOutside(move.DayIndex, true) -
                      SoftUnits.PeakOutside(old.Day, true)) * _wPeak * gw;

        // НДТП-7: чередование дня (× вес параллели; lift-паттерн как heavy-edge).
        if (_wAlternation != 0)
        {
            delta += (AltWith(csNew, node.ClassId, move.DayIndex, move.SlotIndex, nodeHeavy) -
                      AltWithout(csNew, node.ClassId, move.DayIndex)) * _wAlternation * gw;
            delta += (AltWithout(csOld, node.ClassId, old.Day) -
                      AltWith(csOld, node.ClassId, old.Day, old.Slot, nodeHeavy)) * _wAlternation * gw;
        }

        // НДТП-7: край 1 раз/нед (× вес параллели). Кросс-ключевой эффект:
        // ход меняет края дня и для других предметов дня → пересчитываем все
        // edge-ключи старого и нового классо-дня (зеркало SoftEvaluator).
        // В свопах выключается (includeEdge:false) — там точный EdgeSwapDelta.
        if (includeEdge && _wEdgeOnce != 0)
        {
            var movedKey = (node.ClassId, node.SubjectId);
            var affected = new HashSet<(Guid Class, Guid Subject)>();
            if (_classDayKeys.TryGetValue((node.ClassId, old.Day), out var so))
                foreach (var k in so) affected.Add(k);
            if (_classDayKeys.TryGetValue((node.ClassId, move.DayIndex), out var sn))
                foreach (var k in sn) affected.Add(k);
            if (IsEdgeSubject(node)) affected.Add(movedKey);
            foreach (var k in affected)
            {
                bool isMoved = IsEdgeSubject(node) && k == movedKey;
                delta += (EdgeKeyUnits(k, move.DayIndex, move.SlotIndex, isMoved, node.ClassId) -
                          EdgeKeyUnits(k, old.Day, old.Slot, isMoved, node.ClassId)) * _wEdgeOnce * gw;
            }
        }

        // PE-видимость (× вес параллели; lift-паттерн: индекс БЕЗ N).
        if (includePe && _wPe != 0 && IsPeCounted(node))
            delta += (PeUnits(node.ClassId, move.DayIndex) -
                      PeUnits(node.ClassId, old.Day)) * _wPe * gw;

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

    // D-51: единицы doubles-adjacency ключа БЕЗ N / С N (зеркало SoftUnits.DoublesUnits).
    // Списки хранят только eligible-слоты (INV-D1 — по построению Insert/Remove).
    private static int DblWithout(List<int> slots) => SoftUnits.DoublesUnits(slots);

    private static int DblWith(List<int> slots, int slot) => SoftUnits.DoublesUnits([.. slots, slot]);

    // НДТП-7: единицы чередования дня БЕЗ N / С N (зеркало SoftUnits.AlternationBreaks).
    private int AltWithout(List<int> slots, Guid classId, int day)
    {
        var d = slots.Distinct().OrderBy(s => s).ToList();
        return SoftUnits.AlternationBreaks(d.Select(s =>
            _heavyCount.GetValueOrDefault((classId, day, s)) > 0).ToList());
    }

    private int AltWith(List<int> slots, Guid classId, int day, int slot, bool nodeHeavy)
    {
        var d = slots.Append(slot).Distinct().OrderBy(s => s).ToList();
        return SoftUnits.AlternationBreaks(d.Select(s =>
            s == slot
                ? nodeHeavy || _heavyCount.GetValueOrDefault((classId, day, s)) > 0
                : _heavyCount.GetValueOrDefault((classId, day, s)) > 0).ToList());
    }

    // НДТП-7: единицы edge-once ключа K при гипотетическом N в (nDay,nSlot).
    // Индекс БЕЗ N: слоты классо-дня из _classSlots + nSlot; слоты ключа из
    // _keyDaySlots + nSlot (если N — член ключа). Та же группировка, что в
    // SoftEvaluator (край = первый/последний distinct-слот дня).
    private int EdgeKeyUnits((Guid Class, Guid Subject) key, int nDay, int nSlot, bool nIsMember, Guid classId)
    {
        var classDay = new Dictionary<int, List<int>>();
        var keyDay = new Dictionary<int, List<int>>();
        for (int day = 0; day < _problem.DaysCount; day++)
        {
            if (_classSlots.TryGetValue((classId, day), out var sl) && sl.Count > 0)
                classDay[day] = sl;
            if (_keyDaySlots.TryGetValue((key.Class, key.Subject, day), out var kl) && kl.Count > 0)
                keyDay[day] = kl;
        }
        if (nIsMember)
        {
            if (!keyDay.TryGetValue(nDay, out var kn) || !kn.Contains(nSlot))
                keyDay[nDay] = kn is null ? [nSlot] : [.. kn, nSlot];
        }
        if (!classDay.TryGetValue(nDay, out var cn) || !cn.Contains(nSlot))
            classDay[nDay] = cn is null ? [nSlot] : [.. cn, nSlot];
        return EdgeWeekUnits(classDay, keyDay, _problem.DaysCount);
    }

    // НДТП-7: чистое ядро подсчёта edge-единиц ключа по готовым словарям
    // (день → слоты). Единый источник для синглов и свопов.
    private static int EdgeWeekUnits(
        IReadOnlyDictionary<int, List<int>> classDaySlots,
        IReadOnlyDictionary<int, List<int>> keyDaySlots,
        int daysCount)
    {
        int count = 0;
        for (int day = 0; day < daysCount; day++)
        {
            if (!keyDaySlots.TryGetValue(day, out var kl) || kl.Count == 0) continue;
            if (!classDaySlots.TryGetValue(day, out var sl) || sl.Count == 0) continue;
            var d = sl.Distinct().OrderBy(x => x).ToList();
            if (d.Count == 0) continue;
            var ks = new HashSet<int>(kl);
            if (ks.Contains(d[0])) count++;
            if (d[^1] != d[0] && ks.Contains(d[^1])) count++;
        }
        return SoftUnits.EdgeOnceExcess(count);
    }

    // НДТП-7: точная edge-дельта свопа по truth (_pos). Вызывается при ОБОИХ
    // снятых (lift) — «до» восстанавливается виртуально (a@pa + b@pb),
    // «после» — виртуально (a@pb + b@pa). O(n) сканирование _pos + O(ключи).
    private long EdgeSwapDelta(
        LessonOccurrence a, (int Day, int Slot, Guid? Room) pa,
        LessonOccurrence b, (int Day, int Slot, Guid? Room) pb)
    {
        if (_wEdgeOnce == 0) return 0;
        bool ca = IsCounted(a), cb = IsCounted(b);
        if (!ca && !cb) return 0;
        var cls = new Dictionary<(Guid Class, int Day), HashSet<int>>();
        var key = new Dictionary<(Guid Class, Guid Subject, int Day), HashSet<int>>();
        void Add(Guid cc, Guid ss, bool edge, int day, int slot)
        {
            if (!cls.TryGetValue((cc, day), out var s)) cls[(cc, day)] = s = [];
            s.Add(slot);
            if (!edge) return;
            if (!key.TryGetValue((cc, ss, day), out var k)) key[(cc, ss, day)] = k = [];
            k.Add(slot);
        }
        foreach (var (id, pos) in _pos)
        {
            var o = _occById[id];
            if (!IsCounted(o)) continue;
            Add(o.ClassId, o.SubjectId, IsEdgeSubject(o), pos.Day, pos.Slot);
        }
        // Затронутые ключи: edge-ключи 4 классо-дней + ключи a/b.
        var affDays = new HashSet<(Guid Class, int Day)>
        {
            (a.ClassId, pa.Day), (a.ClassId, pb.Day), (b.ClassId, pb.Day), (b.ClassId, pa.Day)
        };
        var affKeys = new HashSet<(Guid Class, Guid Subject)>();
        foreach (var t in key.Keys)
            if (affDays.Contains((t.Class, t.Day)))
                affKeys.Add((t.Class, t.Subject));
        if (ca && IsEdgeSubject(a)) affKeys.Add((a.ClassId, a.SubjectId));
        if (cb && IsEdgeSubject(b)) affKeys.Add((b.ClassId, b.SubjectId));
        long UnitsWith(
            (int Day, int Slot) posA, (int Day, int Slot) posB)
        {
            // Локальные копии затронутых дней/ключей + виртуалы.
            var c2 = new Dictionary<int, HashSet<int>>();
            var k2 = new Dictionary<int, HashSet<int>>();
            long sum = 0;
            foreach (var k in affKeys)
            {
                c2.Clear(); k2.Clear();
                for (int day = 0; day < _problem.DaysCount; day++)
                {
                    if (cls.TryGetValue((k.Class, day), out var s)) c2[day] = [.. s];
                    if (key.TryGetValue((k.Class, k.Subject, day), out var ks)) k2[day] = [.. ks];
                }
                if (ca && a.ClassId == k.Class)
                {
                    if (!c2.TryGetValue(posA.Day, out var s)) c2[posA.Day] = s = [];
                    s.Add(posA.Slot);
                    if (a.SubjectId == k.Subject && IsEdgeSubject(a))
                    {
                        if (!k2.TryGetValue(posA.Day, out var t)) k2[posA.Day] = t = [];
                        t.Add(posA.Slot);
                    }
                }
                if (cb && b.ClassId == k.Class)
                {
                    if (!c2.TryGetValue(posB.Day, out var s)) c2[posB.Day] = s = [];
                    s.Add(posB.Slot);
                    if (b.SubjectId == k.Subject && IsEdgeSubject(b))
                    {
                        if (!k2.TryGetValue(posB.Day, out var t)) k2[posB.Day] = t = [];
                        t.Add(posB.Slot);
                    }
                }
                sum += EdgeWeekUnitsSets(c2, k2, _problem.DaysCount) * _wEdgeOnce *
                    _problem.Flex.WeightForGrade(
                        _problem.Classes.TryGetValue(k.Class, out var c) ? c.Grade : 0);
            }
            return sum;
        }
        // «До»: оба на старых местах; «после»: обмен.
        long before = UnitsWith((pa.Day, pa.Slot), (pb.Day, pb.Slot));
        // Виртуалы a/b уже добавлены выше как posA/posB — для «после»
        // вызываем с обменянными позициями:
        long after = UnitsWith((pb.Day, pb.Slot), (pa.Day, pa.Slot));
        return after - before;
    }

    // НДТП-7: ядро подсчёта по множествам (день → слоты).
    private static int EdgeWeekUnitsSets(
        IReadOnlyDictionary<int, HashSet<int>> classDaySlots,
        IReadOnlyDictionary<int, HashSet<int>> keyDaySlots,
        int daysCount)
    {
        int count = 0;
        for (int day = 0; day < daysCount; day++)
        {
            if (!keyDaySlots.TryGetValue(day, out var ks) || ks.Count == 0) continue;
            if (!classDaySlots.TryGetValue(day, out var ss) || ss.Count == 0) continue;
            int first = ss.Min(), last = ss.Max();
            if (ks.Contains(first)) count++;
            if (last != first && ks.Contains(last)) count++;
        }
        return SoftUnits.EdgeOnceExcess(count);
    }

    // НДТП-7: единицы pe-consecutive класса при N в atDay, индекс БЕЗ N.
    private int PeUnits(Guid classId, int atDay)
    {
        var days = new HashSet<int> { atDay };
        foreach (var ((c, d), cnt) in _peDays)
            if (c == classId && cnt > 0) days.Add(d);
        return SoftUnits.PeRuns(days.ToList());
    }

    // НДТП-7: точная pe-дельта свопа (оба сняты): классы a/b, виртуалы по _peDays.
    private long PeSwapDelta(
        LessonOccurrence a, (int Day, int Slot, Guid? Room) pa,
        LessonOccurrence b, (int Day, int Slot, Guid? Room) pb)
    {
        if (_wPe == 0) return 0;
        bool ca = IsPeCounted(a), cb = IsPeCounted(b);
        if (!ca && !cb) return 0;
        long delta = 0;
        foreach (var c in new[] { a.ClassId, b.ClassId }.Distinct())
        {
            bool aIn = ca && a.ClassId == c, bIn = cb && b.ClassId == c;
            if (!aIn && !bIn) continue;
            var days = new HashSet<int>();
            foreach (var ((cc, d), cnt) in _peDays)
                if (cc == c && cnt > 0) days.Add(d);
            var before = new HashSet<int>(days);
            if (aIn) before.Add(pa.Day);
            if (bIn) before.Add(pb.Day);
            var after = new HashSet<int>(days);
            if (aIn) after.Add(pb.Day);
            if (bIn) after.Add(pa.Day);
            int gw = _problem.Flex.WeightForGrade(
                _problem.Classes.TryGetValue(c, out var cl) ? cl.Grade : 0);
            delta += (SoftUnits.PeRuns(after.ToList()) -
                      SoftUnits.PeRuns(before.ToList())) * _wPe * gw;
        }
        return delta;
    }

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
        // НДТП-7: внеурочка занимает слот (occupancy ниже), но в счётчиках
        // нагрузки не участвует (зеркало SoftEvaluator.counted).
        bool counted = IsCounted(node);
        if (counted)
        {
        SortedInsert(_classSlots.GetOrAdd((node.ClassId, day)), slot);
        SortedInsert(_teacherSlots.GetOrAdd((node.TeacherId, day)), slot);
        var sk = (node.ClassId, node.SubjectId, day);
        _subjCount[sk] = _subjCount.GetValueOrDefault(sk) + 1;
        }
        Bump(_teacherCell, (node.TeacherId, day, slot));
        if (node.GroupId.HasValue)
        {
            Bump(_subCell, (node.ClassId, day, slot));
            Bump(_groupCell, (node.GroupId.Value, day, slot));
        }
        else Bump(_wholeCell, (node.ClassId, day, slot));
        if (room.HasValue) Bump(_roomCell, (room.Value, day, slot));
        if (counted)
        {
        var tk = (node.TeacherId, day);
        _teacherDay[tk] = _teacherDay.GetValueOrDefault(tk) + 1;
        // P2: тяжёлые клетки, key-aware ключи комнат, split-индекс целых.
        if (IsHeavy(node))
            Bump(_heavyCount, (node.ClassId, day, slot));
        // D-51: eligible-слоты дубля по K (только целые несинхронные).
        if (SoftUnits.IsDoubleEligible(node))
            SortedInsert(_dblSlots.GetOrAdd((node.ClassId, node.SubjectId, day)), slot);
        // НДТП-7: edge-структуры (только edge-предметы; класс-day-слот влияет и так).
        if (IsEdgeSubject(node))
        {
            SortedInsert(_keyDaySlots.GetOrAdd((node.ClassId, node.SubjectId, day)), slot);
            var dk = (node.ClassId, day);
            if (!_classDayKeys.TryGetValue(dk, out var dset)) _classDayKeys[dk] = dset = [];
            dset.Add((node.ClassId, node.SubjectId));
        }
        // PE-видимость: счётчик дней класса с физрой.
        if (IsPeCounted(node))
            _peDays[(node.ClassId, day)] = _peDays.GetValueOrDefault((node.ClassId, day)) + 1;
        }
        if (room.HasValue && _problem.Rooms.TryGetValue(room.Value, out var rm) && !rm.CountSubgroupAsGroup)
        {
            var cell = (room.Value, day, slot);
            if (!_roomKeys.TryGetValue(cell, out var ks)) _roomKeys[cell] = ks = [];
            ks[node.ClassId] = ks.GetValueOrDefault(node.ClassId) + 1;
        }
        if (counted && !node.GroupId.HasValue)
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
        bool counted = IsCounted(node);
        if (counted)
        {
        SortedRemove(_classSlots[(node.ClassId, day)], slot);
        SortedRemove(_teacherSlots[(node.TeacherId, day)], slot);
        var sk = (node.ClassId, node.SubjectId, day);
        _subjCount[sk] = _subjCount[sk] - 1;
        if (_subjCount[sk] == 0) _subjCount.Remove(sk);
        }
        Drop(_teacherCell, (node.TeacherId, day, slot));
        if (node.GroupId.HasValue)
        {
            Drop(_subCell, (node.ClassId, day, slot));
            Drop(_groupCell, (node.GroupId.Value, day, slot));
        }
        else Drop(_wholeCell, (node.ClassId, day, slot));
        if (room.HasValue) Drop(_roomCell, (room.Value, day, slot));
        if (counted)
        {
        var tk = (node.TeacherId, day);
        _teacherDay[tk] = _teacherDay[tk] - 1;
        // P2: откат структур выше.
        if (IsHeavy(node))
            Drop(_heavyCount, (node.ClassId, day, slot));
        // D-51: откат eligible-слота дубля.
        if (SoftUnits.IsDoubleEligible(node))
            SortedRemove(_dblSlots[(node.ClassId, node.SubjectId, day)], slot);
        // НДТП-7: откат edge-структур.
        if (IsEdgeSubject(node))
        {
            var kk = (node.ClassId, node.SubjectId, day);
            SortedRemove(_keyDaySlots[kk], slot);
            if (_keyDaySlots[kk].Count == 0)
            {
                _keyDaySlots.Remove(kk);
                var dk = (node.ClassId, day);
                if (_classDayKeys.TryGetValue(dk, out var dset))
                {
                    dset.Remove((node.ClassId, node.SubjectId));
                    if (dset.Count == 0) _classDayKeys.Remove(dk);
                }
            }
        }
        // PE-видимость: откат счётчика.
        if (IsPeCounted(node))
        {
            var pk = (node.ClassId, day);
            int v = _peDays[pk] - 1;
            if (v <= 0) _peDays.Remove(pk);
            else _peDays[pk] = v;
        }
        }
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
        if (counted && !node.GroupId.HasValue)
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

    // НДТП-7: считается ли уроком (внеурочка — нет). Зеркало SoftEvaluator/Validator.
    private bool IsCounted(LessonOccurrence node) =>
        !node.IsExtra &&
        !(_problem.Subjects.TryGetValue(node.SubjectId, out var s) && s.IsNonLesson);

    private bool IsEdgeSubject(LessonOccurrence node) =>
        _problem.Subjects.TryGetValue(node.SubjectId, out var s) &&
        SoftUnits.IsEdgeOnceSubject(s);

    // PE-видимость: физра ли (counted; внеурочка физрой не бывает, но guard дешёвый).
    private bool IsPeCounted(LessonOccurrence node) =>
        IsCounted(node) &&
        _problem.Subjects.TryGetValue(node.SubjectId, out var s) && s.IsPhysicalEducation;

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

    // D-51: тот же helper для ключей K=(класс,предмет,день).
    internal static List<int> GetOrAdd<TKey>(this Dictionary<TKey, List<int>> d, TKey k)
        where TKey : notnull
    {
        if (!d.TryGetValue(k, out var l)) d[k] = l = [];
        return l;
    }
}
