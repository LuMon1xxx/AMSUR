namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// Арсенал S1 (24.09): парные обмены временем двух целых не-sync уроков
// (VND умеет только одиночки). Комнаты едут с уроками (каждый keeps RoomId).
// Кандидаты — уроки в клетках внутри спанов учителя (учителе-дни ≥2 уроков).
// Проверки точные локальные (кросс-годность/pupil/dыры через структуры дня)
// + полный валидатор и soft-кап на принятии; приём gaps-first,
// first-improvement, детерминизм. Time-boxed, never-worsens.
public static class SwapLns
{
    public sealed record SwapResult(
        List<PlacedLesson> Placements, int TeacherGaps, int FailedDays,
        long SoftTotal, int Iterations, int Accepted);

    public static SwapResult Improve(
        SchedulingProblem problem,
        IReadOnlyList<PlacedLesson> start,
        TimeSpan budget,
        int seed,
        long softCap = 0,
        CancellationToken ct = default,
        EffectiveRuleSet? rules = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var occById = problem.Occurrences.ToDictionary(o => o.Id);
        var rs = rules ?? EffectiveRuleSet.Default;
        var best = start.ToList();
        int bestGaps = TeacherDayLns.TeacherGridGaps(occById, best);
        int bestFailed = RuinRecreate.FailedDays(problem, occById, best);
        long bestSoft = SoftEvaluator.Evaluate(problem, best, rs).Total;
        int iters = 0, accepted = 0;
        var rng = new Random(seed);
        // Seed-тасовка пар внутри детерминированного порядка (разные окрестности).
        int salt = rng.Next();

        while (sw.Elapsed < budget && !ct.IsCancellationRequested)
        {
            var cands = Candidates(problem, occById, best, salt);
            if (cands.Count < 2) break;
            var state = new SwapState(problem, occById, best);
            bool improved = false;
            for (int i = 0; i < cands.Count; i++)
            {
                if (sw.Elapsed >= budget || ct.IsCancellationRequested) break;
                for (int j = i + 1; j < cands.Count; j++)
                {
                    if (sw.Elapsed >= budget || ct.IsCancellationRequested) break;
                    iters++;
                    var a = cands[i];
                    var b = cands[j];
                    if (!state.LocalFits(a, b)) continue;
                    var trial = state.Apply(problem, occById, best, a, b);
                    if (OverloadLns.BlockingHard(problem, trial) > 0) continue;
                    if (OverloadLns.PupilHard(problem, trial) > 0) continue;
                    int tg = TeacherDayLns.TeacherGridGaps(occById, trial);
                    if (tg >= bestGaps) continue; // gaps-first
                    int fd = RuinRecreate.FailedDays(problem, occById, trial);
                    if (fd > bestFailed) continue;
                    long soft = SoftEvaluator.Evaluate(problem, trial, rs).Total;
                    if (soft > bestSoft + softCap) continue;
                    best = trial; bestGaps = tg; bestFailed = fd; bestSoft = soft;
                    accepted++; improved = true;
                    break;
                }
                if (improved) break;
            }
            if (!improved) break;
            if (bestGaps == 0) break;
        }
        sw.Stop();
        return new SwapResult(best, bestGaps, bestFailed, bestSoft, iters, accepted);
    }

    // Кандидаты: целые (GroupId==null) не-sync уроки учителе-дней ≥2 уроков
    // (клетки внутри спанов). Порядок детерминирован + seed-соль.
    private static List<Guid> Candidates(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        List<PlacedLesson> current, int salt)
    {
        var pos = current.ToDictionary(p => p.OccurrenceId);
        var daySize = current
            .GroupBy(p => (occById[p.OccurrenceId].TeacherId, p.DayIndex))
            .ToDictionary(g => g.Key, g => g.Count());
        var rng = new Random(salt);
        return current
            .Where(p =>
            {
                var o = occById[p.OccurrenceId];
                return o.GroupId is null && o.SyncGroupId is null &&
                    daySize.GetValueOrDefault((o.TeacherId, p.DayIndex)) >= 2;
            })
            .OrderBy(p => occById[p.OccurrenceId].TeacherId)
            .ThenBy(p => p.DayIndex)
            .ThenBy(p => p.SlotIndex)
            .ThenBy(p => occById[p.OccurrenceId].StableKey, StringComparer.Ordinal)
            .Select(p => p.OccurrenceId)
            .OrderBy(_ => rng.Next())
            .ToList();
    }

    // Локальное состояние для точных проверок без полного валидатора на пару.
    private sealed class SwapState
    {
        private readonly SchedulingProblem _problem;
        private readonly Dictionary<Guid, (int Day, int Slot)> _pos;
        private readonly Dictionary<(Guid Teacher, int Day, int Slot), Guid> _teacherAt;
        private readonly Dictionary<(Guid Key, int Day, int Slot), Guid> _groupAt;
        private readonly HashSet<(Guid Class, int Day, int Slot)> _subAt;
        private readonly Dictionary<(Guid Teacher, int Day), int> _teacherDay;
        private readonly Dictionary<(Guid Class, int Day), HashSet<int>> _classDay;
        private readonly Dictionary<Guid, LessonOccurrence> _occ;

        public SwapState(
            SchedulingProblem problem,
            Dictionary<Guid, LessonOccurrence> occById,
            List<PlacedLesson> current)
        {
            _problem = problem;
            _occ = occById;
            _pos = current.ToDictionary(p => p.OccurrenceId, p => (p.DayIndex, p.SlotIndex));
            _teacherAt = new Dictionary<(Guid, int, int), Guid>();
            _groupAt = new Dictionary<(Guid, int, int), Guid>();
            _subAt = [];
            _teacherDay = new Dictionary<(Guid, int), int>();
            _classDay = new Dictionary<(Guid, int), HashSet<int>>();
            foreach (var p in current)
            {
                var o = occById[p.OccurrenceId];
                _teacherAt[(o.TeacherId, p.DayIndex, p.SlotIndex)] = p.OccurrenceId;
                _groupAt[((o.GroupId ?? o.ClassId), p.DayIndex, p.SlotIndex)] = p.OccurrenceId;
                if (o.GroupId.HasValue) _subAt.Add((o.ClassId, p.DayIndex, p.SlotIndex));
                _teacherDay[(o.TeacherId, p.DayIndex)] =
                    _teacherDay.GetValueOrDefault((o.TeacherId, p.DayIndex)) + 1;
                if (!_classDay.TryGetValue((o.ClassId, p.DayIndex), out var set))
                    _classDay[(o.ClassId, p.DayIndex)] = set = [];
                set.Add(p.SlotIndex);
            }
        }

        public bool LocalFits(Guid aId, Guid bId)
        {
            var oa = _occ[aId];
            var ob = _occ[bId];
            var (dayA, slotA) = _pos[aId];
            var (dayB, slotB) = _pos[bId];
            if (dayA == dayB && slotA == slotB) return false;
            // Кросс-годность доменов.
            if (!_problem.AllowedDays.GetValueOrDefault(aId, []).Contains(dayB)) return false;
            if (!_problem.AllowedSlots.GetValueOrDefault(aId, []).Contains(slotB)) return false;
            if (!_problem.AllowedDays.GetValueOrDefault(bId, []).Contains(dayA)) return false;
            if (!_problem.AllowedSlots.GetValueOrDefault(bId, []).Contains(slotA)) return false;
            // Perturbation-запреты (глобальные t = day*SP+slot).
            if (_problem.BannedTimes.TryGetValue(aId, out var banA) &&
                banA.Contains(dayB * _problem.SlotsPerDay + slotB)) return false;
            if (_problem.BannedTimes.TryGetValue(bId, out var banB) &&
                banB.Contains(dayA * _problem.SlotsPerDay + slotA)) return false;
            // Учителя не двоятся в целевых клетках (кроме уходящего партнёра).
            if (_teacherAt.TryGetValue((oa.TeacherId, dayB, slotB), out var ta) && ta != bId)
                return false;
            if (_teacherAt.TryGetValue((ob.TeacherId, dayA, slotA), out var tb) && tb != aId)
                return false;
            // Pupil occupancy: group-ключ + whole/sub пересечение.
            Guid keyA = oa.GroupId ?? oa.ClassId;
            Guid keyB = ob.GroupId ?? ob.ClassId;
            if (_groupAt.TryGetValue((keyA, dayB, slotB), out var ga) && ga != bId) return false;
            if (_groupAt.TryGetValue((keyB, dayA, slotA), out var gb) && gb != aId) return false;
            // a,b целые: целевая клетка не встречает чужой sub того же класса
            // (sub-уроки никогда не бывают a/b — кандидаты только целые).
            // Whole-vs-whole того же класса уже покрыт groupAt (ключ ClassId).
            if (_subAt.Contains((oa.ClassId, dayB, slotB))) return false;
            if (_subAt.Contains((ob.ClassId, dayA, slotA))) return false;
            // Дневные кэпы учителей/классов после обмена.
            if (!TeacherCapFits(oa.TeacherId, dayA, dayB)) return false;
            if (!TeacherCapFits(ob.TeacherId, dayB, dayA)) return false;
            if (!ClassCapFits(oa.ClassId, dayA, slotA, dayB, slotB, aId, bId)) return false;
            if (!ClassCapFits(ob.ClassId, dayB, slotB, dayA, slotA, bId, aId)) return false;
            return true;
        }

        private bool TeacherCapFits(Guid teacher, int fromDay, int toDay)
        {
            if (fromDay == toDay) return true;
            if (!_problem.Teachers.TryGetValue(teacher, out var t)) return true;
            return _teacherDay.GetValueOrDefault((teacher, toDay)) + 1 <= t.MaxLessonsPerDay;
        }

        private bool ClassCapFits(
            Guid cls, int fromDay, int fromSlot, int toDay, int toSlot,
            Guid selfId, Guid partnerId)
        {
            if (!_problem.Classes.TryGetValue(cls, out var c)) return true;
            // Слоты класса в целевом дне без участников обмена + целевой слот.
            var after = new HashSet<int>();
            foreach (var kv in _pos)
            {
                if (kv.Key == selfId || kv.Key == partnerId) continue;
                if (_occ[kv.Key].ClassId == cls && kv.Value.Day == toDay)
                    after.Add(kv.Value.Slot);
            }
            after.Add(toSlot);
            if (fromDay == toDay && _occ[partnerId].ClassId == cls)
                after.Add(fromSlot); // партнёр (свой класс) встаёт в наш слот
            return after.Count <= c.MaxLessonsPerDay;
        }

        public List<PlacedLesson> Apply(
            SchedulingProblem problem,
            Dictionary<Guid, LessonOccurrence> occById,
            List<PlacedLesson> current, Guid aId, Guid bId)
        {
            var pa = _pos[aId];
            var pb = _pos[bId];
            var roomByOcc = current.ToDictionary(p => p.OccurrenceId, p => p.RoomId);
            var res = new List<PlacedLesson>(current.Count);
            foreach (var p in current)
            {
                if (p.OccurrenceId == aId)
                    res.Add(new PlacedLesson
                    {
                        OccurrenceId = aId, DayIndex = pb.Day,
                        SlotIndex = pb.Slot, RoomId = roomByOcc[aId]
                    });
                else if (p.OccurrenceId == bId)
                    res.Add(new PlacedLesson
                    {
                        OccurrenceId = bId, DayIndex = pa.Day,
                        SlotIndex = pa.Slot, RoomId = roomByOcc[bId]
                    });
                else res.Add(p);
            }
            return res;
        }
    }
}
