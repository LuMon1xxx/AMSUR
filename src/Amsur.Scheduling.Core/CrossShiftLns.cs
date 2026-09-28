namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// Фаза E (добор 24.09): CrossShiftLns — держать учителя в одной смене где
// возможно. Ruin кросс-дней (уteacher через границу смен 7/8) + Replant с
// DayReuseHint + anchorSlots + ShiftCohesionHint (слоты мажоритарной смены дня
// первыми; ничья — пропуск цели; ordering без весов). Приём строго
// (gaps, failedDays, soft). Детерминирован (сиды), time-boxed, never-worsens.
// В конвейер НЕ вшит (замер растил перегруз — страж отклонил); только класс + тесты.
public static class CrossShiftLns
{
    public sealed record CrossShiftResult(
        List<PlacedLesson> Placements, int TeacherGaps, int FailedDays,
        long SoftTotal, int Iterations, int Accepted);

    public static CrossShiftResult Improve(
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
            var worst = WorstCrossDays(occById, best, problem.ShiftBands, topK);
            if (worst.Count == 0) break;
            int rot = (pass - 1) % worst.Count;
            var ordered = worst.Skip(rot).Concat(worst.Take(rot)).ToList();
            bool passImproved = false;
            foreach (var (teacher, day) in ordered)
            {
                if (sw.Elapsed >= budget || ct.IsCancellationRequested) break;
                iters++;
                var ruined = RuinTeacherDayCross(problem, occById, best, teacher, day);
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
                        anchorSlots: true,
                        shift: new GreedyPlacer.ShiftCohesionHint());
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
        return new CrossShiftResult(best, bestGaps, bestFailed, bestSoft, iters, accepted);
    }

    /// <summary>Топ-K кросс-дней учителя (работа в обеих сменах; по сумме
    /// ordinary+cross разрывов убыв.; детерминированный тайбрейк).</summary>
    public static List<(Guid Teacher, int Day)> WorstCrossDays(
        Dictionary<Guid, LessonOccurrence> occById,
        IReadOnlyList<PlacedLesson> placements,
        IReadOnlyList<ShiftBand> bands, int topK)
    {
        return placements.GroupBy(p => (occById[p.OccurrenceId].TeacherId, p.DayIndex))
            .Select(g =>
            {
                var s = g.Select(p => p.SlotIndex).ToList();
                var (ord, cross, isCross) = GapUtils.SplitTeacherDay(s, bands);
                return (Teacher: g.Key.TeacherId, Day: g.Key.DayIndex,
                    Gaps: ord + cross, IsCross: isCross);
            })
            .Where(x => x.IsCross && x.Gaps > 0)
            .OrderByDescending(x => x.Gaps)
            .ThenBy(x => x.Teacher)
            .ThenBy(x => x.Day)
            .Take(topK)
            .Select(x => (x.Teacher, x.Day))
            .ToList();
    }

    private static HashSet<Guid> RuinTeacherDayCross(
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
