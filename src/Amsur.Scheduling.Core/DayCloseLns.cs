namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// Фаза 1 (план «победа <140»): цепной LNS по закрытию учителе-дней.
// Зеркало структуры TeacherDayLns (ruin/topK/restarts/лексикография/
// детерминизм — те же), отличия:
// - RuinTeacherDayClose: ВСЕ уроки (учитель, день) + sync-замыкание,
//   БЕЗ class-contention расширения (день должен опустеть; contention
//   чинит фаза TeacherDayLns до/после).
// - Replant с GreedyPlacer.DayReuseHint(teacher): ordering дней — сначала
//   ОТКРЫТЫЕ дни учителя, потом остальные; внутри дня обычный порядок.
// - Приём строго лексикографически (gaps, failedDays, soft).
// Детерминирован (сиды), time-boxed, never-worsens по построению.
public static class DayCloseLns
{
    public sealed record DayCloseResult(
        List<PlacedLesson> Placements, int TeacherGaps, int FailedDays,
        long SoftTotal, int Iterations, int Accepted);

    public static DayCloseResult Improve(
        SchedulingProblem problem,
        IReadOnlyList<PlacedLesson> start,
        TimeSpan budget,
        int seed,
        int topK = 32,
        int restarts = 6,
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
            var worst = TeacherDayLns.WorstTeacherDays(occById, best, topK);
            if (worst.Count == 0) break;
            int rot = (pass - 1) % worst.Count;
            var ordered = worst.Skip(rot).Concat(worst.Take(rot)).ToList();
            bool passImproved = false;
            foreach (var (teacher, day) in ordered)
            {
                if (sw.Elapsed >= budget || ct.IsCancellationRequested) break;
                iters++;
                var ruined = RuinTeacherDayClose(problem, occById, best, teacher, day);
                if (ruined.Count == 0) continue;
                var frozen = best.Where(p => !ruined.Contains(p.OccurrenceId))
                    .ToDictionary(p => p.OccurrenceId, p => (p.DayIndex, p.SlotIndex, p.RoomId));
                var units = BuildUnits(problem, occById, ruined);
                for (int r = 0; r < restarts; r++)
                {
                    if (sw.Elapsed >= budget) break;
                    var replant = GreedyPlacer.Replant(
                        problem, frozen, units, seed + pass * 1009 + iters * 131 + r * 17,
                        reuse: new GreedyPlacer.DayReuseHint(teacher));
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
        return new DayCloseResult(best, bestGaps, bestFailed, bestSoft, iters, accepted);
    }

    private static HashSet<Guid> RuinTeacherDayClose(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        List<PlacedLesson> current, Guid teacher, int day)
    {
        var ruin = new HashSet<Guid>(current
            .Where(p => occById[p.OccurrenceId].TeacherId == teacher && p.DayIndex == day)
            .Select(p => p.OccurrenceId));
        // Только sync-замыкание (пары целиком); class-contention НЕТ —
        // цель выселить день целиком.
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

    // Юниты для пересева: sync-пары целиком, остальные по одному
    // (зеркало TeacherDayLns.BuildUnits: порядок — StableKey).
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
