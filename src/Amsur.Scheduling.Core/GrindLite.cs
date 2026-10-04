namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// Grind-lite: «олимпийская» доводка одним вызовом (по просьбе, окт. 2026):
// сшивание тощих дней → bin-pack учительских дней (плотные дни вместо 1–2 уроков:
// отработал смену целиком — завтра выходной) → сплочённость смен → парные обмены
// → финальный ремонт компактности + физры.
// Каждая стадия принимается ТОЛЬКО если: 0 жёстких, 0 ученических окон/стартов
// и (меньше учительских окон ИЛИ столько же при нехудшем штрафе).
// Иначе остаётся предыдущее — never-worsens по построению. Детерминировано.
public static class GrindLite
{
    public sealed record PolishResult(
        List<PlacedLesson> Placements, long SoftTotal, int HardViolations,
        int TeacherWindows, IReadOnlyList<string> Log);

    public static PolishResult Polish(
        SchedulingProblem problem,
        IReadOnlyList<PlacedLesson> start,
        EffectiveRuleSet? rules = null,
        TimeSpan budget = default,
        int seed = 11,
        CancellationToken ct = default)
    {
        var rs = rules ?? EffectiveRuleSet.Default;
        if (budget <= TimeSpan.Zero) budget = TimeSpan.FromSeconds(30);
        var log = new List<string>();
        var occById = problem.Occurrences.ToDictionary(o => o.Id);

        (List<PlacedLesson> Pl, long Soft, int Hard, int Pupil, int Win) Measure(
            List<PlacedLesson> pl)
        {
            var vr = PlacementValidator.Validate(problem, pl);
            int pupil = vr.HardViolations.Count(v => v.Code is "student-gap" or "student-late-start");
            return (pl, SoftEvaluator.Evaluate(problem, pl, rs).Total,
                vr.HardViolations.Count, pupil,
                TeacherDayLns.TeacherGridGaps(occById, pl));
        }

        // Двухтировая приёмка: current дрейфует без регресса (pupil, hard),
        // bestClean — только полностью чистое и лучше по (окна, штраф).
        var curM = Measure(start.ToList());
        var current = curM.Pl;
        (List<PlacedLesson> Pl, long Soft, int Hard, int Pupil, int Win)? bestClean = null;
        if (curM is { Hard: 0, Pupil: 0 }) bestClean = curM;
        log.Add($"start: soft={curM.Soft} hard={curM.Hard} tw={curM.Win}");

        void Stage(string name, Func<List<PlacedLesson>, List<PlacedLesson>> run)
        {
            List<PlacedLesson> got;
            try { got = run(current); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log.Add($"{name}: ошибка стадии ({ex.GetType().Name}) — пропущена.");
                return;
            }
            var m = Measure(got);
            if ((m.Pupil, m.Hard).CompareTo((curM.Pupil, curM.Hard)) <= 0)
            {
                current = got;
                curM = m;
            }
            if (m is { Hard: 0, Pupil: 0 } &&
                (bestClean is null || (m.Win, m.Soft).CompareTo((bestClean.Value.Win, bestClean.Value.Soft)) < 0))
            {
                bestClean = m;
                log.Add($"{name}: чистое принято (soft={m.Soft} tw={m.Win}).");
            }
            else log.Add($"{name}: (pupil={m.Pupil} hard={m.Hard} tw={m.Win}).");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan Slice(double frac, TimeSpan min, TimeSpan max)
        {
            var t = TimeSpan.FromTicks((long)(budget.Ticks * frac));
            if (t < min) t = min;
            if (t > max) t = max;
            return t;
        }

        Stage("ThinDay", cur => ThinDayLns.Improve(problem, cur, Slice(0.15, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20)), seed, ct: ct).Placements);
        Stage("TeacherDay", cur => TeacherDayLns.Improve(problem, cur, Slice(0.30, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(40)), seed + 1, ct: ct).Placements);
        Stage("CrossShift", cur => CrossShiftLns.Improve(problem, cur, Slice(0.15, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20)), seed + 2, ct: ct).Placements);
        Stage("Swap", cur => SwapLns.Improve(problem, cur, Slice(0.15, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20)), seed + 3, ct: ct).Placements);
        Stage("CompactRepair", cur => CompactRepair.Repair(problem, cur).Placements);
        Stage("PeRepair", cur => PeSpacingRepair.Repair(problem, cur));
        sw.Stop();
        var final = bestClean ?? curM;
        log.Add($"итог: soft={final.Soft} hard={final.Hard} tw={final.Win} за {sw.Elapsed.TotalSeconds:F1}с.");
        return new PolishResult(final.Pl, final.Soft, final.Hard, final.Win, log);
    }
}
