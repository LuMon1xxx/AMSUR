namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// Фаза 2 (план «победа <140»): sync-joint LNS. Ruin — sync-группа целиком +
// все уроки учителей-участников в тех же днях (teacher-day расширение по
// участникам); Replant — с DayReuseHint первого участника (sync-joint
// расширение GreedyPlacer: reuse при любом участнике, дни где все открыты,
// слоты смежные со всеми — якорь, не hard). Приём строго лексикографически
// (gaps, failedDays, soft). Детерминирован (сиды), time-boxed, never-worsens.
public static class SyncLns
{
    public sealed record SyncLnsResult(
        List<PlacedLesson> Placements, int TeacherGaps, int FailedDays,
        long SoftTotal, int Iterations, int Accepted);

    public static SyncLnsResult Improve(
        SchedulingProblem problem,
        IReadOnlyList<PlacedLesson> start,
        TimeSpan budget,
        int seed,
        int topK = 16,
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
            var worst = WorstSyncGroups(problem, occById, best, topK);
            if (worst.Count == 0) break;
            int rot = (pass - 1) % worst.Count;
            var ordered = worst.Skip(rot).Concat(worst.Take(rot)).ToList();
            bool passImproved = false;
            foreach (var grp in ordered)
            {
                if (sw.Elapsed >= budget || ct.IsCancellationRequested) break;
                iters++;
                var ruined = RuinSyncGroup(problem, occById, best, grp);
                if (ruined.Count == 0) continue;
                var anchor = problem.Occurrences
                    .Where(o => o.SyncGroupId == grp)
                    .OrderBy(o => o.StableKey, StringComparer.Ordinal)
                    .First().TeacherId;
                var frozen = best.Where(p => !ruined.Contains(p.OccurrenceId))
                    .ToDictionary(p => p.OccurrenceId, p => (p.DayIndex, p.SlotIndex, p.RoomId));
                var units = BuildUnits(problem, occById, ruined);
                for (int r = 0; r < restarts; r++)
                {
                    if (sw.Elapsed >= budget) break;
                    var replant = GreedyPlacer.Replant(
                        problem, frozen, units, seed + pass * 1009 + iters * 131 + r * 17,
                        reuse: new GreedyPlacer.DayReuseHint(anchor));
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
        return new SyncLnsResult(best, bestGaps, bestFailed, bestSoft, iters, accepted);
    }

    /// <summary>Топ-K sync-групп по сумме сеточных дыр учителе-дней участников
    /// (убыв.; детерминированный тайбрейк по StableKey).</summary>
    public static List<Guid> WorstSyncGroups(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        IReadOnlyList<PlacedLesson> placements, int topK)
    {
        var pos = placements.ToDictionary(p => p.OccurrenceId);
        var dayGaps = new Dictionary<(Guid Teacher, int Day), int>();
        foreach (var g in placements.GroupBy(p => (occById[p.OccurrenceId].TeacherId, p.DayIndex)))
        {
            var s = g.Select(p => p.SlotIndex).OrderBy(x => x).ToList();
            dayGaps[g.Key] = s.Count > 1 ? (s[^1] - s[0] + 1) - s.Count : 0;
        }
        return problem.Occurrences
            .Where(o => o.SyncGroupId.HasValue)
            .GroupBy(o => o.SyncGroupId!.Value)
            .Select(g =>
            {
                int score = 0;
                foreach (var o in g)
                    if (pos.TryGetValue(o.Id, out var p))
                        score += dayGaps.GetValueOrDefault((o.TeacherId, p.DayIndex));
                string key = g.Min(o => o.StableKey)!;
                return (Grp: g.Key, Score: score, Key: key);
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .Take(topK)
            .Select(x => x.Grp)
            .ToList();
    }

    private static HashSet<Guid> RuinSyncGroup(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        List<PlacedLesson> current, Guid syncGroup)
    {
        var ruin = new HashSet<Guid>(problem.Occurrences
            .Where(o => o.SyncGroupId == syncGroup)
            .Select(o => o.Id));
        // Teacher-day расширение по участникам: все уроки учителей-участников
        // в тех же днях, что члены группы.
        var pos = current.ToDictionary(p => p.OccurrenceId);
        var teachers = ruin.Select(id => occById[id].TeacherId).ToHashSet();
        var days = ruin
            .Where(id => pos.ContainsKey(id))
            .Select(id => pos[id].DayIndex)
            .ToHashSet();
        foreach (var p in current)
        {
            var o = occById[p.OccurrenceId];
            if (teachers.Contains(o.TeacherId) && days.Contains(p.DayIndex))
                ruin.Add(p.OccurrenceId);
        }
        // Sync-замыкание: пары целиком (участники могут тянуть чужие группы).
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
