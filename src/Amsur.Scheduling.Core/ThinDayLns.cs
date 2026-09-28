namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// Фаза A (план v2): ThinDayLns для тощих учителе-дней (≤2 уроков, 0–2 дыры —
// DayClose их не видит). Зеркало DayCloseLns: WorstThinDays (самые тонкие
// первыми) + ruin дня целиком + sync-замыкание (БЕЗ contention) + Replant
// с DayReuseHint (+ anchorSlots: слоты вплотную к занятиям reuse-учителя) +
// приём с ценой дня: лучше лексикографически (gaps, failedDays, soft) ИЛИ
// меньше дней при (gaps≤+gapsCap, failedDays≤, soft≤+softCap).
// Дефолт (0,0) = never-worsens. Детерминирован (сиды), time-boxed.
public static class ThinDayLns
{
    public sealed record ThinDayResult(
        List<PlacedLesson> Placements, int TeacherGaps, int FailedDays,
        long SoftTotal, int TeacherDays, int Iterations, int Accepted);

    public static ThinDayResult Improve(
        SchedulingProblem problem,
        IReadOnlyList<PlacedLesson> start,
        TimeSpan budget,
        int seed,
        int topK = 32,
        int restarts = 6,
        int gapsCap = 0,
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
        int bestDays = TeacherDayCount(occById, best);
        int iters = 0, accepted = 0, pass = 0;

        while (sw.Elapsed < budget && !ct.IsCancellationRequested)
        {
            pass++;
            var worst = WorstThinDays(occById, best, topK);
            if (worst.Count == 0) break;
            int rot = (pass - 1) % worst.Count;
            var ordered = worst.Skip(rot).Concat(worst.Take(rot)).ToList();
            bool passImproved = false;
            foreach (var (teacher, day) in ordered)
            {
                if (sw.Elapsed >= budget || ct.IsCancellationRequested) break;
                iters++;
                var ruined = RuinTeacherDayThin(problem, occById, best, teacher, day);
                if (ruined.Count == 0) continue;
                var frozen = best.Where(p => !ruined.Contains(p.OccurrenceId))
                    .ToDictionary(p => p.OccurrenceId, p => (p.DayIndex, p.SlotIndex, p.RoomId));
                var units = BuildUnits(problem, occById, ruined);
                for (int r = 0; r < restarts; r++)
                {
                    if (sw.Elapsed >= budget) break;
                    var replant = GreedyPlacer.Replant(
                        problem, frozen, units, seed + pass * 1009 + iters * 131 + r * 17,
                        reuse: new GreedyPlacer.DayReuseHint(teacher),
                        anchorSlots: true);
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
                    int days = TeacherDayCount(occById, trial);
                    bool lexBetter = (tg < bestGaps && fd <= bestFailed) ||
                        (tg == bestGaps && (fd < bestFailed ||
                            (fd == bestFailed && soft < bestSoft)));
                    bool dayCheaper = days < bestDays &&
                        tg <= bestGaps + gapsCap && fd <= bestFailed &&
                        soft <= bestSoft + softCap;
                    if (lexBetter || dayCheaper)
                    {
                        best = trial; bestGaps = tg; bestFailed = fd;
                        bestSoft = soft; bestDays = days;
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
        return new ThinDayResult(best, bestGaps, bestFailed, bestSoft, bestDays, iters, accepted);
    }

    /// <summary>Число занятых учителе-дней (цена дня для приёма).</summary>
    public static int TeacherDayCount(
        Dictionary<Guid, LessonOccurrence> occById,
        IReadOnlyList<PlacedLesson> placements) =>
        placements.Select(p => (occById[p.OccurrenceId].TeacherId, p.DayIndex))
            .Distinct().Count();

    /// <summary>Топ-K тощих учителе-дней (≤2 уроков; самые тонкие первыми,
    /// затем по дырам убыв.; детерминированный тайбрейк).</summary>
    public static List<(Guid Teacher, int Day)> WorstThinDays(
        Dictionary<Guid, LessonOccurrence> occById,
        IReadOnlyList<PlacedLesson> placements, int topK)
    {
        return placements.GroupBy(p => (occById[p.OccurrenceId].TeacherId, p.DayIndex))
            .Select(g =>
            {
                var s = g.Select(p => p.SlotIndex).OrderBy(x => x).ToList();
                int gaps = s.Count > 1 ? (s[^1] - s[0] + 1) - s.Count : 0;
                return (Teacher: g.Key.TeacherId, Day: g.Key.DayIndex,
                    Count: g.Count(), Gaps: gaps);
            })
            .Where(x => x.Count <= 2)
            .OrderBy(x => x.Count)
            .ThenByDescending(x => x.Gaps)
            .ThenBy(x => x.Teacher)
            .ThenBy(x => x.Day)
            .Take(topK)
            .Select(x => (x.Teacher, x.Day))
            .ToList();
    }

    private static HashSet<Guid> RuinTeacherDayThin(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        List<PlacedLesson> current, Guid teacher, int day)
    {
        var ruin = new HashSet<Guid>(current
            .Where(p => occById[p.OccurrenceId].TeacherId == teacher && p.DayIndex == day)
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
