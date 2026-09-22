namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// D-50 (план «лучше в разы», шаг ②): цепной LNS по рваным учителе-дням.
// Мотивация измерена (22.09.2026): одиночные ходы в 99,9% запрещены
// (кабинет/класс/смена заняты), вес teacher-gap 10→20 даёт −3% —
// дыры «несущие», чинятся только пересборкой дня целиком.
// Метод: топ-K худших учителе-дней (сеточные дыры) → ruin (уроки дня +
// sync-замыкание) → Replant поверх замороженного ×restarts сидов →
// CompactRepair → принять строго лексикографически лучшее
// (teacherGaps, pupilFailedDays, soft). Хуже не бывает по построению.
// Детерминирован (сиды), time-boxed, честно частичен (Replant с хвостом — скип).
public static class TeacherDayLns
{
    public sealed record TeacherLnsResult(
        List<PlacedLesson> Placements, int TeacherGaps, int FailedDays,
        long SoftTotal, int Iterations, int Accepted);

    public static TeacherLnsResult Improve(
        SchedulingProblem problem,
        IReadOnlyList<PlacedLesson> start,
        TimeSpan budget,
        int seed,
        int topK = 16,
        int restarts = 4,
        CancellationToken ct = default,
        EffectiveRuleSet? rules = null,
        bool gapsFirst = false)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var occById = problem.Occurrences.ToDictionary(o => o.Id);
        var rs = rules ?? EffectiveRuleSet.Default;
        var best = start.ToList();
        int bestGaps = TeacherGridGaps(occById, best);
        int bestFailed = RuinRecreate.FailedDays(problem, occById, best);
        long bestSoft = SoftEvaluator.Evaluate(problem, best, rs).Total;
        int iters = 0, accepted = 0, pass = 0;

        while (sw.Elapsed < budget && !ct.IsCancellationRequested)
        {
            pass++;
            var worst = WorstTeacherDays(occById, best, topK);
            if (worst.Count == 0) break;
            // Ротация старта по проходам — разные окрестности при тех же топах.
            int rot = (pass - 1) % worst.Count;
            var ordered = worst.Skip(rot).Concat(worst.Take(rot)).ToList();
            bool passImproved = false;
            foreach (var (teacher, day) in ordered)
            {
                if (sw.Elapsed >= budget || ct.IsCancellationRequested) break;
                iters++;
                var ruined = RuinTeacherDay(problem, occById, best, teacher, day);
                if (ruined.Count == 0) continue;
                var frozen = best.Where(p => !ruined.Contains(p.OccurrenceId))
                    .ToDictionary(p => p.OccurrenceId, p => (p.DayIndex, p.SlotIndex, p.RoomId));
                var units = BuildUnits(problem, occById, ruined);
                for (int r = 0; r < restarts; r++)
                {
                    if (sw.Elapsed >= budget) break;
                    var replant = GreedyPlacer.Replant(
                        problem, frozen, units, seed + pass * 1009 + iters * 131 + r * 17,
                        new GreedyPlacer.CompactHint(teacher, day));
                    if (replant.Unplaced.Count > 0) continue;
                    var trial = CompactRepair.Repair(problem, replant.Placed
                        .Select(kv => new PlacedLesson
                        {
                            OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
                            SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
                        }).ToList()).Placements;
                    int tg = TeacherGridGaps(occById, trial);
                    int fd = RuinRecreate.FailedDays(problem, occById, trial);
                    long soft = SoftEvaluator.Evaluate(problem, trial, rs).Total;
                    // Строгая лексикография: дыры строго меньше БЕЗ роста
                    // pupil-провалов; при равных дырах — меньше провалов/soft.
                    // gapsFirst (только эксперименты): дыры любой ценой soft
                    // (провалы всё равно не растут) — карта границы «дыры vs soft».
                    bool accept = gapsFirst
                        ? (tg < bestGaps && fd <= bestFailed)
                        : ((tg < bestGaps && fd <= bestFailed) ||
                            (tg == bestGaps && (fd < bestFailed ||
                                (fd == bestFailed && soft < bestSoft))));
                    if (accept)
                    {
                        best = trial; bestGaps = tg; bestFailed = fd; bestSoft = soft;
                        accepted++; passImproved = true;
                        break;
                    }
                }
                if (passImproved) break; // топ пересчитываем после каждого принятия
            }
            if (!passImproved) break;
            if (bestGaps == 0) break;
        }
        sw.Stop();
        return new TeacherLnsResult(best, bestGaps, bestFailed, bestSoft, iters, accepted);
    }

    /// <summary>Сеточные дыры учителей (метрика пользователя; union слотов дня).</summary>
    public static int TeacherGridGaps(
        Dictionary<Guid, LessonOccurrence> occById, IReadOnlyList<PlacedLesson> placements)
    {
        int gaps = 0;
        foreach (var g in placements.GroupBy(p => (occById[p.OccurrenceId].TeacherId, p.DayIndex)))
        {
            var s = g.Select(p => p.SlotIndex).OrderBy(x => x).ToList();
            if (s.Count > 1) gaps += (s[^1] - s[0] + 1) - s.Count;
        }
        return gaps;
    }

    /// <summary>Топ-K (учитель, день) по дырам (убыв.; детерминированный тайбрейк).</summary>
    public static List<(Guid Teacher, int Day)> WorstTeacherDays(
        Dictionary<Guid, LessonOccurrence> occById,
        IReadOnlyList<PlacedLesson> placements, int topK)
    {
        return placements.GroupBy(p => (occById[p.OccurrenceId].TeacherId, p.DayIndex))
            .Select(g =>
            {
                var s = g.Select(p => p.SlotIndex).OrderBy(x => x).ToList();
                int gaps = s.Count > 1 ? (s[^1] - s[0] + 1) - s.Count : 0;
                return (Teacher: g.Key.TeacherId, Day: g.Key.DayIndex, Gaps: gaps);
            })
            .Where(x => x.Gaps > 0)
            .OrderByDescending(x => x.Gaps)
            .ThenBy(x => x.Teacher)
            .ThenBy(x => x.Day)
            .Take(topK)
            .Select(x => (x.Teacher, x.Day))
            .ToList();
    }

    private static HashSet<Guid> RuinTeacherDay(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        List<PlacedLesson> current, Guid teacher, int day)
    {
        var ruin = new HashSet<Guid>(current
            .Where(p => occById[p.OccurrenceId].TeacherId == teacher && p.DayIndex == day)
            .Select(p => p.OccurrenceId));
        // Contention: дыры учителя «держат» чужие уроки — весь классо-день каждого
        // затронутого класса (как в pupil-LNS) + синк-пары целиком. Без этого
        // пересадка воспроизводит ту же фрагментацию (измерено 22.09.2026).
        // Bounded: при переполнении — только учителе-день (итерации ограничены).
        var classes = ruin.Select(id => occById[id].ClassId).ToHashSet();
        foreach (var p in current.Where(p => p.DayIndex == day &&
                 classes.Contains(occById[p.OccurrenceId].ClassId)))
            ruin.Add(p.OccurrenceId);
        if (ruin.Count > 160)
            ruin = new HashSet<Guid>(current
                .Where(p => occById[p.OccurrenceId].TeacherId == teacher && p.DayIndex == day)
                .Select(p => p.OccurrenceId));
        // Sync-замыкание: пары целиком.
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
    // (зеркало RuinRecreate.BuildUnits: порядок — StableKey).
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
