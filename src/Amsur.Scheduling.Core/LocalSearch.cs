namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// E12/D-24 — локальное улучшение feasible-базлайна (first-improvement, time-boxed).
// Ходы — только время (комната фиксирована); каждый кандидат — через SearchIndex
// (EPIC-H H5.1: та же семантика, что IncrementalEvaluator, но O(k) вместо O(n));
// финал всегда идёт через FullValidator-gate вызывателя (Accept).
// Детерминирован seed (порядок обхода).
// Seed-различие → разные локальные оптимумы → diversity Top-5 на multi-seed (D-18).
public static class LocalSearch
{
    public sealed record ImproveResult(
        List<PlacedLesson> Placements, long SoftTotal, int AcceptedMoves, long ElapsedMs);

    /// <summary>
    /// EPIC-H аудит-контейнер: только счётчики/тайминги, на поведение поиска не влияет.
    /// Trajectory — (elapsedMs, soft) после каждого accepted-хода + финальная точка.
    /// </summary>
    public sealed class Audit
    {
        public long Attempts;
        public long Forbidden;
        public long NonImproving;
        public int Sweeps;
        public List<int> AcceptedPerSweep { get; } = [];
        public List<(long ElapsedMs, long Soft)> Trajectory { get; } = [];
        public long EvalMs;
        // H5.2 swap-фаза:
        public long SwapAttempts;
        public long SwapAccepted;
        public List<int> SwapAcceptedPerSweep { get; } = [];
        public long SwapEvalMs;
    }

    public static ImproveResult Improve(
        SchedulingProblem problem,
        IReadOnlyList<PlacedLesson> start,
        TimeSpan budget,
        int seed,
        CancellationToken ct = default,
        Audit? audit = null,
        EffectiveRuleSet? rules = null,
        Action<SolverIncumbent>? sink = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var deadline = sw.Elapsed + budget;
        var rs = rules ?? EffectiveRuleSet.Default;
        var index = SearchIndex.Build(problem, start, rs);
        long curSoft = SoftEvaluator.Evaluate(problem, start, rs).Total;

        var rng = new Random(seed);
        var order = problem.Occurrences.OrderBy(_ => rng.Next()).Select(o => o.Id).ToList();
        // P3 targeted-старт: первый single-проход — по teacher-ordinary desc,
        // ходы только внутри дня+смены (консолидация без блуждания через границу смен).
        var targeted = TargetedOrder(problem, start, seed);

        var occById = problem.Occurrences.ToDictionary(o => o.Id);
        // Синхронные группы: ходы всей группой (сплиты A/B, профильные пары).
        var syncMembers = problem.Occurrences
            .Where(o => o.SyncGroupId.HasValue)
            .GroupBy(o => o.SyncGroupId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Streaming прогресса: не чаще ~250 мс, иначе UI захлёбывается снапшотами.
        long nextSinkMs = 0;
        int sinkSeq = 0;
        Action<long>? emit = sink is null ? null : soft =>
        {
            long now = sw.ElapsedMilliseconds;
            if (now < nextSinkMs) return;
            nextSinkMs = now + 250;
            sink(new SolverIncumbent(index.Snapshot(), soft, ++sinkSeq, now, "B", seed));
        };

        int accepted = 0;
        bool improved = true;
        var evalSw = new System.Diagnostics.Stopwatch();
        long evalTicks = 0, swapTicks = 0;
        bool firstSingle = true;
        // H5.2 VND: single-moves до сходимости → swaps до сходимости → повтор,
        // пока полный цикл даёт улучшения или не вышел бюджет.
        while ((improved) && sw.Elapsed < deadline && !ct.IsCancellationRequested)
        {
            improved = false;
            if (SinglePhase(problem, index, firstSingle ? targeted : order, occById, syncMembers,
                    firstSingle, deadline, sw, ct, audit, evalSw,
                    ref evalTicks, ref curSoft, ref accepted, emit))
                improved = true;
            firstSingle = false;
            if (sw.Elapsed >= deadline || ct.IsCancellationRequested) break;
            if (SwapPhase(index, order, deadline, sw, ct, audit, evalSw,
                    ref swapTicks, ref curSoft, ref accepted, emit))
                improved = true;
        }
        sw.Stop();
        if (audit is not null)
        {
            audit.EvalMs = evalTicks * 1000 / System.Diagnostics.Stopwatch.Frequency;
            audit.SwapEvalMs = swapTicks * 1000 / System.Diagnostics.Stopwatch.Frequency;
            audit.Trajectory.Add((sw.ElapsedMilliseconds, curSoft));
        }
        // Финальная точка — всегда, мимо троттлинга: график не остаётся на предпоследнем.
        if (sink is not null)
            sink(new SolverIncumbent(index.Snapshot(), curSoft, ++sinkSeq, sw.ElapsedMilliseconds, "B", seed));
        return new ImproveResult(index.Snapshot(), curSoft, accepted, sw.ElapsedMilliseconds);
    }

    // P3: приоритет teacher-day по ordinary-окнам (desc) + seed-перемешивание
    // внутри равных. Детерминировано сидом (R3-митигация).
    internal static List<Guid> TargetedOrder(
        SchedulingProblem problem, IReadOnlyList<PlacedLesson> start, int seed)
    {
        var occById = problem.Occurrences.ToDictionary(o => o.Id);
        var ordByTeacherDay = new Dictionary<(Guid Teacher, int Day), int>();
        foreach (var g in start.GroupBy(p => (occById[p.OccurrenceId].TeacherId, p.DayIndex)))
        {
            var slots = g.Select(p => p.SlotIndex).ToList();
            var (ord, _, _) = GapUtils.SplitTeacherDay(slots, problem.ShiftBands);
            ordByTeacherDay[g.Key] = ord;
        }
        var rng = new Random(seed ^ 0x5eed);
        return problem.Occurrences
            .Select(o => o.Id)
            .OrderByDescending(id =>
            {
                var o = occById[id];
                var pos = start.FirstOrDefault(p => p.OccurrenceId == id);
                if (pos is null) return 0;
                return ordByTeacherDay.GetValueOrDefault((o.TeacherId, pos.DayIndex));
            })
            .ThenBy(_ => rng.Next())
            .ToList();
    }

    // Single-ходы до сходимости (один проход без улучшений). Возвращает, было ли хоть одно.
    private static bool SinglePhase(
        SchedulingProblem problem, SearchIndex index, List<Guid> order,
        Dictionary<Guid, LessonOccurrence> occById,
        Dictionary<Guid, List<LessonOccurrence>> syncMembers,
        bool restrictSameDayShift,
        TimeSpan deadline, System.Diagnostics.Stopwatch sw, CancellationToken ct,
        Audit? audit, System.Diagnostics.Stopwatch evalSw,
        ref long evalTicks, ref long curSoft, ref int accepted,
        Action<long>? emit = null)
    {
        bool any = false;
        bool improved = true;
        while (improved && sw.Elapsed < deadline && !ct.IsCancellationRequested)
        {
            improved = false;
            int sweepAccepted = 0;
            if (audit is not null) audit.Sweeps++;
            var syncDone = new HashSet<Guid>();
            foreach (var occId in order)
            {
                if (sw.Elapsed >= deadline || ct.IsCancellationRequested) break;
                if (!index.Contains(occId)) continue;
                var node = occById[occId];
                // Синхронная группа ходит целиком и ровно раз за проход:
                // иначе участнику A пришлось бы двигаться в одиночку (SubgroupSync).
                if (node.SyncGroupId is Guid syncId)
                {
                    if (!syncDone.Add(syncId)) continue;
                    if (TrySyncGroup(problem, index, syncId, syncMembers, node, restrictSameDayShift,
                            deadline, sw, ct, audit, evalSw,
                            ref evalTicks, ref curSoft, ref accepted, ref sweepAccepted, emit))
                    {
                        improved = true;
                        any = true;
                    }
                    continue;
                }
                var pos = index.Position(occId);
                int curDay = pos.Day, curSlot = pos.Slot;
                var days = problem.AllowedDays.GetValueOrDefault(occId, []);
                var slots = problem.AllowedSlots.GetValueOrDefault(occId, []);
                foreach (int day in days)
                {
                    // P3 targeted: только свой день и своя смена (без переноса через 7/8).
                    bool sameDayOnly = restrictSameDayShift && day != curDay;
                    int curBand = restrictSameDayShift
                        ? GapUtils.ShiftIndex(curSlot, problem.ShiftBands) : 0;
                    foreach (int slot in slots)
                    {
                        if (sw.Elapsed >= deadline) break;
                        if (day == curDay && slot == curSlot) continue;
                        if (sameDayOnly) continue;
                        if (restrictSameDayShift &&
                            GapUtils.ShiftIndex(slot, problem.ShiftBands) != curBand)
                            continue;
                        var move = new CandidateMove(occId, day, slot, pos.Room);
                        if (audit is not null) audit.Attempts++;
                        evalSw.Restart();
                        var (allowed, delta, _) = index.TryMove(move);
                        evalSw.Stop();
                        evalTicks += evalSw.ElapsedTicks;
                        if (!allowed)
                        {
                            if (audit is not null) audit.Forbidden++;
                            continue;
                        }
                        if (delta >= 0)
                        {
                            if (audit is not null) audit.NonImproving++;
                            continue;
                        }
                        index.Commit(move);
                        curDay = day; curSlot = slot;
                        curSoft += delta;
                        accepted++;
                        sweepAccepted++;
                        improved = true;
                        any = true;
                        if (audit is not null) audit.Trajectory.Add((sw.ElapsedMilliseconds, curSoft));
                        emit?.Invoke(curSoft);
                    }
                    if (sw.Elapsed >= deadline) break;
                }
            }
            if (audit is not null) audit.AcceptedPerSweep.Add(sweepAccepted);
        }
        return any;
    }

    // Первый улучшающий ход синхронной группы за проход. Домен цели — пересечение
    // доменов всех размещённых участников; приём/дельта — через TryMoveSync.
    private static bool TrySyncGroup(
        SchedulingProblem problem, SearchIndex index, Guid syncId,
        Dictionary<Guid, List<LessonOccurrence>> syncMembers,
        LessonOccurrence anchor, bool restrictSameDayShift,
        TimeSpan deadline, System.Diagnostics.Stopwatch sw, CancellationToken ct,
        Audit? audit, System.Diagnostics.Stopwatch evalSw,
        ref long evalTicks, ref long curSoft, ref int accepted, ref int sweepAccepted,
        Action<long>? emit)
    {
        if (!syncMembers.TryGetValue(syncId, out var group)) return false;
        var pos = index.Position(anchor.Id);
        int curDay = pos.Day, curSlot = pos.Slot;
        List<int>? days = null, slots = null;
        foreach (var m in group)
        {
            if (!index.Contains(m.Id)) continue;
            var md = problem.AllowedDays.GetValueOrDefault(m.Id, []);
            var ms = problem.AllowedSlots.GetValueOrDefault(m.Id, []);
            days = days is null ? [.. md] : days.Intersect(md).ToList();
            slots = slots is null ? [.. ms] : slots.Intersect(ms).ToList();
        }
        if (days is null || slots is null || days.Count == 0 || slots.Count == 0) return false;

        int curBand = restrictSameDayShift
            ? GapUtils.ShiftIndex(curSlot, problem.ShiftBands) : 0;
        foreach (int day in days)
        {
            if (sw.Elapsed >= deadline || ct.IsCancellationRequested) break;
            if (restrictSameDayShift && day != curDay) continue;
            foreach (int slot in slots)
            {
                if (sw.Elapsed >= deadline || ct.IsCancellationRequested) break;
                if (day == curDay && slot == curSlot) continue;
                if (restrictSameDayShift &&
                    GapUtils.ShiftIndex(slot, problem.ShiftBands) != curBand)
                    continue;
                if (audit is not null) audit.Attempts++;
                evalSw.Restart();
                var (allowed, delta, _) = index.TryMoveSync(syncId, day, slot, curSoft);
                evalSw.Stop();
                evalTicks += evalSw.ElapsedTicks;
                if (!allowed)
                {
                    if (audit is not null) audit.Forbidden++;
                    continue;
                }
                if (delta >= 0)
                {
                    if (audit is not null) audit.NonImproving++;
                    continue;
                }
                index.CommitSync(syncId, day, slot);
                curSoft += delta;
                accepted++;
                sweepAccepted++;
                if (audit is not null) audit.Trajectory.Add((sw.ElapsedMilliseconds, curSoft));
                emit?.Invoke(curSoft);
                return true; // 1 улучшающий ход на SyncGroupId за проход
            }
        }
        return false;
    }

    // Swap-фаза: попарные обмены временем (first-improvement) до прохода без улучшений.
    private static bool SwapPhase(
        SearchIndex index, List<Guid> order,
        TimeSpan deadline, System.Diagnostics.Stopwatch sw, CancellationToken ct,
        Audit? audit, System.Diagnostics.Stopwatch evalSw,
        ref long swapTicks, ref long curSoft, ref int accepted,
        Action<long>? emit = null)
    {
        bool any = false;
        bool improved = true;
        while (improved && sw.Elapsed < deadline && !ct.IsCancellationRequested)
        {
            improved = false;
            int sweepAccepted = 0;
            for (int i = 0; i < order.Count; i++)
            {
                if (sw.Elapsed >= deadline || ct.IsCancellationRequested) break;
                if (!index.Contains(order[i])) continue;
                for (int j = i + 1; j < order.Count; j++)
                {
                    if (sw.Elapsed >= deadline) break;
                    if (!index.Contains(order[j])) continue;
                    if (audit is not null) audit.SwapAttempts++;
                    evalSw.Restart();
                    var (allowed, delta, _) = index.TrySwap(order[i], order[j]);
                    evalSw.Stop();
                    swapTicks += evalSw.ElapsedTicks;
                    if (!allowed || delta >= 0) continue;
                    index.CommitSwap(order[i], order[j]);
                    curSoft += delta;
                    accepted++;
                    sweepAccepted++;
                    improved = true;
                    any = true;
                    if (audit is not null) audit.Trajectory.Add((sw.ElapsedMilliseconds, curSoft));
                    emit?.Invoke(curSoft);
                }
            }
            if (audit is not null) audit.SwapAcceptedPerSweep.Add(sweepAccepted);
            if (audit is not null) audit.SwapAccepted += sweepAccepted;
        }
        return any;
    }
}
