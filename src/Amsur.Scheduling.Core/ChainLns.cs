namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// Grind-цепочка до сходимости (24.09): конвейер фиксированной
// последовательности оставляет слабину — повтор раундов DC→TD→Sync→Thin
// со свежими сидами до раунда без принятий. Never-worsens по построению
// (каждая стадия принимает только улучшения). Детерминирована сидом;
// time-boxed (бюджет делится на стадии; минимум 1с на стадию).
public static class ChainLns
{
    public sealed record ChainResult(
        List<PlacedLesson> Placements, int TeacherGaps, int FailedDays,
        long SoftTotal, int Rounds, int Accepted);

    public static ChainResult Improve(
        SchedulingProblem problem,
        IReadOnlyList<PlacedLesson> start,
        TimeSpan budget,
        int seed,
        EffectiveRuleSet? rules = null,
        CancellationToken ct = default)
    {
        var occById = problem.Occurrences.ToDictionary(o => o.Id);
        var rs = rules ?? EffectiveRuleSet.Default;
        var best = start.ToList();
        int bestGaps = TeacherDayLns.TeacherGridGaps(occById, best);
        int bestFailed = RuinRecreate.FailedDays(problem, occById, best);
        long bestSoft = SoftEvaluator.Evaluate(problem, best, rs).Total;
        const int maxRounds = 4;
        var stageBudget = TimeSpan.FromTicks(Math.Max(
            TimeSpan.FromSeconds(1).Ticks, budget.Ticks / (4 * maxRounds)));
        int rounds = 0, accepted = 0;

        for (int round = 0; round < maxRounds && !ct.IsCancellationRequested; round++)
        {
            int roundAccepted = 0;
            var dc = DayCloseLns.Improve(problem, best, stageBudget,
                seed + round * 100003 + 1, rules: rs, ct: ct);
            best = dc.Placements; roundAccepted += dc.Accepted;
            var td = TeacherDayLns.Improve(problem, best, stageBudget,
                seed + round * 100003 + 2, rules: rs, ct: ct);
            best = td.Placements; roundAccepted += td.Accepted;
            var sy = SyncLns.Improve(problem, best, stageBudget,
                seed + round * 100003 + 3, rules: rs, ct: ct);
            best = sy.Placements; roundAccepted += sy.Accepted;
            var th = ThinDayLns.Improve(problem, best, stageBudget,
                seed + round * 100003 + 4, rules: rs, ct: ct);
            best = th.Placements; roundAccepted += th.Accepted;
            rounds++;
            accepted += roundAccepted;
            bestGaps = TeacherDayLns.TeacherGridGaps(occById, best);
            bestFailed = RuinRecreate.FailedDays(problem, occById, best);
            bestSoft = SoftEvaluator.Evaluate(problem, best, rs).Total;
            if (roundAccepted == 0) break; // сходимость: раунд без принятий
            if (bestGaps == 0 && bestFailed == 0) break;
        }
        return new ChainResult(best, bestGaps, bestFailed, bestSoft, rounds, accepted);
    }
}
