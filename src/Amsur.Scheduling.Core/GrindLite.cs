namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// Grind-lite: «олимпийская» доводка одним вызовом (по просьбе, окт. 2026):
// полная LNS-цепочка — закрытие учителе-дней → bin-pack учительских дней →
// sync-joint → тощие дни → сплочённость смен → парные обмены → SanPin-перегруз →
// финальный ремонт компактности → баланс 1-х классов → разнос физры →
// ruin&recreate по остатку бюджета, если остались failedDays.
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
        CancellationToken ct = default,
        Action<SolverIncumbent>? sink = null)
    {
        var rs = rules ?? EffectiveRuleSet.Default;
        if (budget <= TimeSpan.Zero) budget = TimeSpan.FromSeconds(30);
        var log = new List<string>();
        var occById = problem.Occurrences.ToDictionary(o => o.Id);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Stream в UI только чистых улучшений (грязные кандидаты архив всё равно
        // отклонит); троттлинг ~250 мс — график живой, снапшоты не давят.
        long nextEmitMs = 0;
        int emitSeq = 0;
        void Emit(List<PlacedLesson> pl, long soft, bool force = false)
        {
            if (sink is null) return;
            long now = sw.ElapsedMilliseconds;
            if (!force && now < nextEmitMs) return;
            nextEmitMs = now + 250;
            sink(new SolverIncumbent(pl, soft, ++emitSeq, now, "B", seed));
        }

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
                (bestClean is null ||
                 (m.Soft <= bestClean.Value.Soft &&
                  (m.Win < bestClean.Value.Win ||
                   (m.Win == bestClean.Value.Win && m.Soft < bestClean.Value.Soft)))))
            {
                bestClean = m;
                log.Add($"{name}: чистое принято (soft={m.Soft} tw={m.Win}).");
                Emit(m.Pl, m.Soft);
            }
            else log.Add($"{name}: (pupil={m.Pupil} hard={m.Hard} tw={m.Win}).");
        }

        // Срез бюджета: доля от общего, но никогда больше оставшегося времени
        // (иначе min-клампы стадий съедают бюджет следующих операторов).
        TimeSpan Slice(double frac, TimeSpan min, TimeSpan max)
        {
            var left = budget - sw.Elapsed;
            if (left <= TimeSpan.Zero) return TimeSpan.Zero;
            var t = TimeSpan.FromTicks((long)(budget.Ticks * frac));
            if (t < min) t = min;
            if (t > max) t = max;
            return t > left ? left : t;
        }

        // Полная LNS-цепочка (доля бюджета в скобках): закрытие учителе-дней →
        // bin-pack учителе-дней → sync-joint → тонкие дни → склейка смен →
        // парные обмены → SanPin-перегруз → ремонты → ruin&recreate по остатку.
        Stage("DayClose", cur => DayCloseLns.Improve(problem, cur,
            Slice(0.25, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(40)),
            seed, ct: ct, rules: rs).Placements);
        Stage("TeacherDay", cur => TeacherDayLns.Improve(problem, cur,
            Slice(0.25, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(40)),
            seed + 1, ct: ct, rules: rs).Placements);
        Stage("Sync", cur => SyncLns.Improve(problem, cur,
            Slice(0.15, TimeSpan.FromMilliseconds(750), TimeSpan.FromSeconds(25)),
            seed + 2, ct: ct, rules: rs).Placements);
        Stage("ThinDay", cur => ThinDayLns.Improve(problem, cur,
            Slice(0.10, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(20)),
            seed + 3, ct: ct, rules: rs).Placements);
        Stage("CrossShift", cur => CrossShiftLns.Improve(problem, cur,
            Slice(0.10, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(20)),
            seed + 4, ct: ct, rules: rs).Placements);
        Stage("Swap", cur => SwapLns.Improve(problem, cur,
            Slice(0.10, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(20)),
            seed + 5, ct: ct, rules: rs).Placements);
        Stage("Overload", cur => OverloadLns.Improve(problem, cur,
            Slice(0.05, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(10)),
            seed + 6, ct: ct, rules: rs).Placements);
        Stage("CompactRepair", cur => CompactRepair.Repair(problem, cur).Placements);
        Stage("GradeOneBalance", cur => GradeOneBalance.Balance(problem, cur).Placements);
        Stage("PeSpacing", cur => PeSpacingRepair.Repair(problem, cur));
        if (RuinRecreate.FailedDays(problem, occById, current) > 0)
            Stage("RuinRecreate", cur => RuinRecreate.Improve(problem, cur,
                Slice(0.10, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)),
                seed + 7, ct, rules: rs).Placements);

        sw.Stop();
        var final = bestClean ?? curM;
        if (bestClean is not null) Emit(bestClean.Value.Pl, bestClean.Value.Soft, force: true);
        log.Add($"итог: soft={final.Soft} hard={final.Hard} tw={final.Win} за {sw.Elapsed.TotalSeconds:F1}с.");
        return new PolishResult(final.Pl, final.Soft, final.Hard, final.Win, log);
    }
}
