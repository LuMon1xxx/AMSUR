namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// LNS ruin & recreate для остаточных окон (D-28d): одиночные ходы и свопы
// застревают в локальных оптимумах contention-карманов (доказано: 2Г-д5,
// цепочка через 4В/9Г/1А одним ходом не разбирается). Разрушаем окрестность
// проваленных классо-дней + конфликтующие уроки тех же учителей в те же дни,
// пересеваем GreedyPlacer.Replant поверх замороженного, чиним, принимаем
// лексикографически лучшее (failedDays, soft). Детерминировано (сиды),
// time-boxed, честно частично.
public static class RuinRecreate
{
    public sealed record LnsResult(
        List<PlacedLesson> Placements, int FailedDays, long SoftTotal,
        int Iterations, int Accepted);

    public static LnsResult Improve(
        SchedulingProblem problem,
        IReadOnlyList<PlacedLesson> start,
        TimeSpan budget,
        int seed,
        CancellationToken ct = default,
        EffectiveRuleSet? rules = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var occById = problem.Occurrences.ToDictionary(o => o.Id);
        var rs = rules ?? EffectiveRuleSet.Default;
        var best = start.ToList();
        int bestFailed = FailedDays(problem, occById, best);
        long bestSoft = SoftEvaluator.Evaluate(problem, best, rs).Total;
        int iters = 0, accepted = 0;
        var rng = new Random(seed);

        while (sw.Elapsed < budget && !ct.IsCancellationRequested && iters < 12)
        {
            iters++;
            var ruined = SelectRuinSet(problem, occById, best, rng, iters);
            if (ruined.Count == 0) break;
            var frozen = best.Where(p => !ruined.Contains(p.OccurrenceId))
                .ToDictionary(p => p.OccurrenceId, p => (p.DayIndex, p.SlotIndex, p.RoomId));
            var units = BuildUnits(problem, occById, ruined);
            var replant = GreedyPlacer.Replant(problem, frozen, units, seed + iters * 131);
            if (replant.Unplaced.Count > 0) continue; // попытка не удалась — без коммита
            var trial = replant.Placed.Select(kv => new PlacedLesson
            {
                OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
                SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
            }).ToList();
            var rep = CompactRepair.Repair(problem, trial);
            var trialPlaced = rep.Placements;
            int failed = FailedDays(problem, occById, trialPlaced);
            long soft = SoftEvaluator.Evaluate(problem, trialPlaced, rs).Total;
            if (failed < bestFailed || (failed == bestFailed && soft < bestSoft))
            {
                best = trialPlaced;
                bestFailed = failed;
                bestSoft = soft;
                accepted++;
                if (bestFailed == 0) break;
            }
        }
        sw.Stop();
        return new LnsResult(best, bestFailed, bestSoft, iters, accepted);
    }

    // Проваленные дни: student-gap>0 или late>0 (validator-коды, без class-maxperday —
    // кэпы держит greedy скоупами; фокус LNS — компактность).
    public static int FailedDays(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        IReadOnlyList<PlacedLesson> placements)
    {
        int n = 0;
        foreach (var g in placements.GroupBy(p => (occById[p.OccurrenceId].ClassId, p.DayIndex)))
        {
            var slots = g.Select(p => p.SlotIndex).OrderBy(s => s).ToList();
            int anchor = StudentCompactness.AnchorFor(problem, g.Key.ClassId);
            if (StudentCompactness.GapOf(slots) > 0 || StudentCompactness.LateExcess(slots, anchor) > 0)
                n++;
        }
        return n;
    }

    // Ruin-множество: уроки проваленных дней + уроки тех же учителей в те же дни
    // (contention) + sync-пары целиком (замыкание). Дневная гранулярность
    // (пары не рвутся); остановка при ~120 для bounded-итераций.
    private static HashSet<Guid> SelectRuinSet(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        List<PlacedLesson> current,
        Random rng, int iter)
    {
        var failedDays = new HashSet<(Guid Class, int Day)>();
        foreach (var g in current.GroupBy(p => (occById[p.OccurrenceId].ClassId, p.DayIndex)))
        {
            var slots = g.Select(p => p.SlotIndex).OrderBy(s => s).ToList();
            int anchor = StudentCompactness.AnchorFor(problem, g.Key.ClassId);
            if (StudentCompactness.GapOf(slots) > 0 || StudentCompactness.LateExcess(slots, anchor) > 0)
                failedDays.Add(g.Key);
        }
        // Детерминированный порядок + лёгкая ротация по итерациям (разные окрестности).
        var orderedDays = failedDays.OrderBy(d => d.Class).ThenBy(d => d.Day).ToList();
        if (orderedDays.Count > 1)
        {
            int rot = iter % orderedDays.Count;
            orderedDays = orderedDays.Skip(rot).Concat(orderedDays.Take(rot)).ToList();
        }
        var ruin = new HashSet<Guid>();
        foreach (var (cls, day) in orderedDays)
        {
            foreach (var p in current.Where(p => occById[p.OccurrenceId].ClassId == cls && p.DayIndex == day))
                ruin.Add(p.OccurrenceId);
            // Contention: те же учителя в тот же день (другие классы).
            var teachers = ruin.Select(id => occById[id].TeacherId).ToHashSet();
            foreach (var p in current.Where(p => p.DayIndex == day && teachers.Contains(occById[p.OccurrenceId].TeacherId)))
                ruin.Add(p.OccurrenceId);
            if (ruin.Count >= 120) break;
        }
        // Sync-замыкание: пары целиком (на практике маты в том же дне — роста нет).
        bool grew;
        do
        {
            grew = false;
            foreach (var p in current.Where(p => ruin.Contains(p.OccurrenceId)))
            {
                var o = occById[p.OccurrenceId];
                if (!o.SyncGroupId.HasValue) continue;
                foreach (var m in problem.Occurrences.Where(x => x.SyncGroupId == o.SyncGroupId))
                    if (ruin.Add(m.Id)) grew = true;
            }
        } while (grew);
        return ruin;
    }

    // Юниты для пересева: sync-пары целиком, остальные по одному.
    private static List<List<Guid>> BuildUnits(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        HashSet<Guid> ruined)
    {
        var units = new List<List<Guid>>();
        var done = new HashSet<Guid>();
        foreach (var id in ruined.OrderBy(id => occById[id].StableKey, StringComparer.Ordinal))
        {
            if (!done.Add(id)) continue;
            var o = occById[id];
            if (o.SyncGroupId.HasValue)
            {
                var mates = problem.Occurrences
                    .Where(x => x.SyncGroupId == o.SyncGroupId && ruined.Contains(x.Id))
                    .Select(x => x.Id).ToList();
                foreach (var m in mates) done.Add(m);
                units.Add(mates);
            }
            else units.Add([id]);
        }
        return units;
    }
}
