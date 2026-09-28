namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// OWN SYNTHESIS (24.09): обобщение DayClose с дня на неделю учителя —
// совместная перепаковка всех уроков учителя (динамический teacherDay
// в Replant сам bin-pack'ит через DayReuseHint). Приём строго
// лексикографически (gaps, failedDays, soft). Детерминирован (сиды),
// time-boxed, never-worsens. В конвейер НЕ вшит (замер на v3 — нуль-эффект);
// только класс + тесты, резерв с F-A/F-B/F-C.
public static class TeacherWeekLns
{
    public sealed record TeacherWeekResult(
        List<PlacedLesson> Placements, int TeacherGaps, int FailedDays,
        long SoftTotal, int Iterations, int Accepted);

    public static TeacherWeekResult Improve(
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
            var worst = WorstTeachers(occById, best, topK);
            if (worst.Count == 0) break;
            int rot = (pass - 1) % worst.Count;
            var ordered = worst.Skip(rot).Concat(worst.Take(rot)).ToList();
            bool passImproved = false;
            foreach (var teacher in ordered)
            {
                if (sw.Elapsed >= budget || ct.IsCancellationRequested) break;
                iters++;
                var ruined = RuinTeacherWeek(problem, occById, best, teacher);
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
        return new TeacherWeekResult(best, bestGaps, bestFailed, bestSoft, iters, accepted);
    }

    /// <summary>Топ-K учителей по суммарным сеточным дырам недели
    /// (убыв.; детерминированный тайбрейк).</summary>
    public static List<Guid> WorstTeachers(
        Dictionary<Guid, LessonOccurrence> occById,
        IReadOnlyList<PlacedLesson> placements, int topK)
    {
        return placements.GroupBy(p => (occById[p.OccurrenceId].TeacherId, p.DayIndex))
            .Select(g =>
            {
                var s = g.Select(p => p.SlotIndex).OrderBy(x => x).ToList();
                int gaps = s.Count > 1 ? (s[^1] - s[0] + 1) - s.Count : 0;
                return (Teacher: g.Key.TeacherId, Gaps: gaps);
            })
            .GroupBy(x => x.Teacher)
            .Select(g => (Teacher: g.Key, Gaps: g.Sum(x => x.Gaps)))
            .Where(x => x.Gaps > 0)
            .OrderByDescending(x => x.Gaps)
            .ThenBy(x => x.Teacher)
            .Take(topK)
            .Select(x => x.Teacher)
            .ToList();
    }

    private static HashSet<Guid> RuinTeacherWeek(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        List<PlacedLesson> current, Guid teacher)
    {
        var ruin = new HashSet<Guid>(current
            .Where(p => occById[p.OccurrenceId].TeacherId == teacher)
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
