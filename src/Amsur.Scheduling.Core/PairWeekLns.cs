namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// Арсенал S2 (24.09): двойной week-ruin сцепленной пары — два учителя,
// связанные sync-группами, перепаковываются вместе (ruin обеих недель +
// sync-замыкание; Replant с DayReuseHint ведущего по дырам). Приём строго
// лексикографически (gaps, failedDays, soft). Детерминирован (сиды),
// time-boxed, never-worsens. В конвейер НЕ вшит (замер — нуль); только класс + тесты.
public static class PairWeekLns
{
    public sealed record PairWeekResult(
        List<PlacedLesson> Placements, int TeacherGaps, int FailedDays,
        long SoftTotal, int Iterations, int Accepted);

    public static PairWeekResult Improve(
        SchedulingProblem problem,
        IReadOnlyList<PlacedLesson> start,
        TimeSpan budget,
        int seed,
        int topK = 8,
        int restarts = 4,
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
        int iters = 0, accepted = 0, pass = 0;

        while (sw.Elapsed < budget && !ct.IsCancellationRequested)
        {
            pass++;
            var worst = WorstLinkedPairs(problem, occById, best, topK);
            if (worst.Count == 0) break;
            int rot = (pass - 1) % worst.Count;
            var ordered = worst.Skip(rot).Concat(worst.Take(rot)).ToList();
            bool passImproved = false;
            foreach (var (first, second) in ordered)
            {
                if (sw.Elapsed >= budget || ct.IsCancellationRequested) break;
                iters++;
                var ruined = RuinPairWeeks(problem, occById, best, first, second);
                if (ruined.Count == 0) continue;
                var frozen = best.Where(p => !ruined.Contains(p.OccurrenceId))
                    .ToDictionary(p => p.OccurrenceId, p => (p.DayIndex, p.SlotIndex, p.RoomId));
                var units = BuildUnits(problem, occById, ruined);
                for (int r = 0; r < restarts; r++)
                {
                    if (sw.Elapsed >= budget) break;
                    var replant = GreedyPlacer.Replant(
                        problem, frozen, units, seed + pass * 1009 + iters * 131 + r * 17,
                        reuse: new GreedyPlacer.DayReuseHint(first));
                    if (replant.Unplaced.Count > 0) continue;
                    var trial = CompactRepair.Repair(problem, replant.Placed
                        .Select(kv => new PlacedLesson
                        {
                            OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
                            SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
                        }).ToList()).Placements;
                    int tg = TeacherDayLns.TeacherGridGaps(occById, trial);
                    int fd = RuinRecreate.FailedDays(problem, occById, trial);
                    long soft = SoftEvaluator.Evaluate(problem, trial, rs).Total;
                    if ((tg < bestGaps && fd <= bestFailed) ||
                        (tg == bestGaps && (fd < bestFailed ||
                            (fd == bestFailed && soft < bestSoft))))
                    {
                        best = trial; bestGaps = tg; bestFailed = fd; bestSoft = soft;
                        accepted++; passImproved = true;
                        break;
                    }
                }
                if (passImproved) break;
            }
            if (!passImproved) break;
            if (bestGaps == 0) break;
        }
        sw.Stop();
        return new PairWeekResult(best, bestGaps, bestFailed, bestSoft, iters, accepted);
    }

    /// <summary>Топ-K сцепленных sync-группами пар учителей по суммарным дырам
    /// недели (убыв.; first — более дырявый; детерминированный тайбрейк).</summary>
    public static List<(Guid First, Guid Second)> WorstLinkedPairs(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        IReadOnlyList<PlacedLesson> placements, int topK)
    {
        var weekGaps = new Dictionary<Guid, int>();
        foreach (var g in placements.GroupBy(p => (occById[p.OccurrenceId].TeacherId, p.DayIndex)))
        {
            var s = g.Select(p => p.SlotIndex).OrderBy(x => x).ToList();
            int gaps = s.Count > 1 ? (s[^1] - s[0] + 1) - s.Count : 0;
            weekGaps[g.Key.TeacherId] = weekGaps.GetValueOrDefault(g.Key.TeacherId) + gaps;
        }
        var linked = new HashSet<(Guid, Guid)>();
        foreach (var grp in problem.Occurrences
                     .Where(o => o.SyncGroupId.HasValue)
                     .GroupBy(o => o.SyncGroupId!.Value))
        {
            var teachers = grp.Select(o => o.TeacherId).Distinct().OrderBy(t => t).ToList();
            for (int i = 0; i < teachers.Count; i++)
                for (int j = i + 1; j < teachers.Count; j++)
                    linked.Add((teachers[i], teachers[j]));
        }
        return linked
            .Select(p =>
            {
                int ga = weekGaps.GetValueOrDefault(p.Item1);
                int gb = weekGaps.GetValueOrDefault(p.Item2);
                var first = ga >= gb ? p.Item1 : p.Item2;
                var second = ga >= gb ? p.Item2 : p.Item1;
                return (First: first, Second: second, Score: ga + gb);
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.First)
            .ThenBy(x => x.Second)
            .Take(topK)
            .Select(x => (x.First, x.Second))
            .ToList();
    }

    private static HashSet<Guid> RuinPairWeeks(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        List<PlacedLesson> current, Guid first, Guid second)
    {
        var ruin = new HashSet<Guid>(current
            .Where(p => occById[p.OccurrenceId].TeacherId == first ||
                        occById[p.OccurrenceId].TeacherId == second)
            .Select(p => p.OccurrenceId));
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
