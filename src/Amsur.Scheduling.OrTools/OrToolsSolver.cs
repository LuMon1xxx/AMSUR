namespace Amsur.Scheduling.OrTools;

using System.Diagnostics;
using Amsur.Domain;
using Amsur.Scheduling.Core;
using Google.OrTools.Sat;

// CP-SAT runner: IntVar-модель (D-02), two-phase A/B, FullValidator-gate (INV-01).
// Кабинеты — lane-модель (D-24): комната cap C = C линий + NoOverlap вместо
// O(n·R·T) реификации (OOM на большой школе). Семантика Hard та же (D-05/P0.5 факты).
// Phase A: hard-only, первое feasible. Phase B: hard + proxy-objective (сумма t),
// best-so-far — по SoftEvaluator среди incumbents через callback (честно: solver
// предлагает, validator распоряжается; точная линеаризация gaps — P1 staged-бенч).
public sealed class OrToolsSolver : IScheduleSolver
{
    // EPIC-H H6: CP-SAT Phase B мёртв на масштабе (0 инкумбентов за 34–180с на
    // ≥388 occ — D-24; жив на малых: Small90 — 8 инкумбентов). Порог 300 — между
    // измеренными 90 (B нужен) и 388 (B бесполезен); зафиксирован замером H6.
    public const int LargeSchoolPhaseBThreshold = 300;
    /// <summary>Выбор solver: время + кабинет (null — кабинетов нет в задаче).</summary>
    private sealed record SlotPick(int Day, int Slot, Guid? RoomId);

    /// <summary>
    /// Кандидаты-кабинеты occurrence (P0.5 + P2/R1): исключены Forbidden
    /// + IsManualOnly + ONLY-чужой. Места не проверяются (D-68). Пусто → ModelInvalid.
    /// </summary>
    private static List<int> RoomCandidates(
        SchedulingProblem problem, IList<Room> roomList,
        Dictionary<Guid, int> roomIndex, LessonOccurrence occ)
    {
        var res = new List<int>();
        if (roomList.Count == 0) return res;
        for (int r = 0; r < roomList.Count; r++)
        {
            if (!RoomPolicy.IsCandidate(problem, roomList[r], occ)) continue;
            res.Add(r);
        }
        return res;
    }

    public Task<SolverResult> SolveAsync(SchedulingProblem problem, CancellationToken ct = default) =>
        SolveAsync(problem, ct, sink: null, rules: null);

    public Task<SolverResult> SolveAsync(
        SchedulingProblem problem, CancellationToken ct, EffectiveRuleSet? rules) =>
        SolveAsync(problem, ct, sink: null, rules: rules);

    public async Task<SolverResult> SolveAsync(
        SchedulingProblem problem,
        CancellationToken ct,
        Action<SolverIncumbent>? sink,
        EffectiveRuleSet? rules = null)
    {
        var sw = Stopwatch.StartNew();
        var phaseMs = new Dictionary<string, long>();
        var diagnostics = new List<string>();
        var rs = rules ?? EffectiveRuleSet.Default;
        if (rs.ProfileName != "STANDARD" || rs.CatalogVersion != RuleCatalog.Version)
            diagnostics.Add($"Профиль качества: {rs.ProfileName} (каталог v{rs.CatalogVersion}).");

        // ModelInvalid: пустой домен или нет подходящего кабинета.
        foreach (var occ in problem.Occurrences)
        {
            if (!problem.AllowedDays.TryGetValue(occ.Id, out var d) || d.Count == 0 ||
                !problem.AllowedSlots.TryGetValue(occ.Id, out var s) || s.Count == 0)
            {
                return new SolverResult(SolverStatus.ModelInvalid, [], 0, 0,
                    sw.ElapsedMilliseconds, 0, 0, new Dictionary<string, long>(),
                    [$"Occurrence {occ.Id}: empty domain."], phaseMs);
            }
        }
        var roomList = problem.Rooms.Values.OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
        var roomIndex = roomList.Select((r, i) => (r.Id, i)).ToDictionary(p => p.Id, p => p.i);
        if (roomList.Count > 0)
        {
            foreach (var occ in problem.Occurrences)
            {
                if (RoomCandidates(problem, roomList, roomIndex, occ).Count == 0)
                {
                    return new SolverResult(SolverStatus.ModelInvalid, [], 0, 0,
                        sw.ElapsedMilliseconds, 0, 0, new Dictionary<string, long>(),
                        [$"Occurrence {occ.Id}: no room fits (Forbidden or room rules)."], phaseMs);
                }
            }
        }

        var options = problem.Options;
        double totalBudget = options.MaxTimeSeconds;
        double budgetA = options.TwoPhaseSolve ? Math.Min(30, totalBudget * 0.25) : totalBudget;
        double budgetB = totalBudget - budgetA;
        // Общие allowed-пары (day,slot) -> t.
        var allowedPairs = problem.Occurrences.ToDictionary(
            o => o.Id,
            o => problem.AllowedDays[o.Id]
                .SelectMany(d => problem.AllowedSlots[o.Id].Select(s => (Day: d, Slot: s)))
                .OrderBy(p => p.Day).ThenBy(p => p.Slot).ToList());

        // E12/D-24: жадный старт для Phase A (только hints; семантика та же).
        // D-61 портфель стартов: до 6 greedy (3 плотных compact+dayReuse —
        // плотная упаковка учителе-дней со старта, 3 обычных), каждый с
        // completion-досевом неразмещённых (GreedyCompletion). Выбор — минимум
        // Unplaced, при равенстве минимум (FailedDays, TeacherGridGaps, Soft)
        // после CompactRepair. Детерминировано; общий кап ~4с.
        var swGreedy = Stopwatch.StartNew();
        var evalOccById = problem.Occurrences.ToDictionary(o => o.Id);
        GreedyPlacement? greedy = null;
        (int FailedDays, int Gaps, long Soft)? greedyKey = null;
        int greedyStarts = 0;
        for (int gi = 0; gi < 6; gi++)
        {
            if (greedy is not null && swGreedy.Elapsed >= TimeSpan.FromSeconds(4)) break;
            bool dense = gi < 3;
            int seed = (problem.Options.RandomSeed ?? 0) + gi * 7919;
            var cand = GreedyPlacer.Place(problem, seed, compact: dense, dayReuse: dense);
            greedyStarts++;
            if (cand.Unplaced.Count > 0)
                cand = GreedyCompletion.Complete(problem, cand, seed);
            if (greedy is null || cand.Unplaced.Count < greedy.Unplaced.Count)
            {
                greedy = cand;
                greedyKey = null; // ленивый пересчёт при первом тайбрейке
                continue;
            }
            if (cand.Unplaced.Count == greedy.Unplaced.Count)
            {
                greedyKey ??= GreedyMetricsKey(problem, greedy, rs, evalOccById);
                var candKey = GreedyMetricsKey(problem, cand, rs, evalOccById);
                if (CmpKey(candKey, greedyKey.Value) < 0)
                {
                    greedy = cand;
                    greedyKey = candKey;
                }
            }
        }
        swGreedy.Stop();
        diagnostics.Add($"Greedy: placed {greedy!.Placed.Count}/{problem.Occurrences.Count} " +
            $"in {swGreedy.ElapsedMilliseconds}ms, starts {greedyStarts} (portfolio + completion, hints for phase A).");
        if (greedy.Unplaced.Count > 0)
        {
            var occById = problem.Occurrences.ToDictionary(o => o.Id);
            diagnostics.Add("Greedy unplaced: " + string.Join(";",
                greedy.Unplaced.Take(5).Select(id =>
                    occById.TryGetValue(id, out var o) ? o.StableKey : id.ToString())));
        }

        // E12/D-24: жадный baseline — если конструктив полон и validator-clean,
        // это уже feasible-first за миллисекунды (проверено, не предположено):
        // Phase A пропускается, CP-SAT работает только улучшение в Phase B.
        // D-28: greedy чиним CompactRepair (окна/поздний старт ученика — HARD).
        // D-28b: greedy ПОЛНЫЙ, но не clean → CP-SAT A всё равно пропускаем:
        // A компактность не моделирует и на масштабе 5×14 за cap не находит,
        // а LS+repair идут по soft-градиенту (student 100) и доводят до HARD.
        Dictionary<Guid, SlotPick>? greedyFeasible = null;
        List<PlacedLesson>? greedyFullDirty = null;
        if (greedy.Unplaced.Count == 0)
        {
            var gpl = greedy.Placed.Select(kv => new PlacedLesson
            {
                OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
                SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
            }).ToList();
            var rep = CompactRepair.Repair(problem, gpl);
            diagnostics.Add($"CompactRepair(greedy): дней починено {rep.RepairedDays}, " +
                $"не удалось {rep.FailedDays}, soft {SoftEvaluator.Evaluate(problem, rep.Placements, rs).Total}.");
            foreach (var n in rep.Notes) diagnostics.Add("CompactRepair: " + n);
            gpl = rep.Placements;
            if (PlacementValidator.Validate(problem, gpl).IsValid)
                greedyFeasible = ToSlotPicks(gpl.ToDictionary(
                    p => p.OccurrenceId, p => (p.DayIndex, p.SlotIndex, p.RoomId)));
            else
                greedyFullDirty = gpl;
        }

        // Фаза A.
        var swA = Stopwatch.StartNew();
        PhaseResult resA;
        if (greedyFeasible is not null)
        {
            swA.Stop();
            phaseMs["phaseA"] = swGreedy.ElapsedMilliseconds; // время получения baseline
            phaseMs["modelBuildA"] = 0; // модель не строилась
            resA = new PhaseResult(greedyFeasible, false, ct.IsCancellationRequested, 0, false);
        }
        else if (greedyFullDirty is not null)
        {
            // Полный, но compact-dirty baseline: сразу в LS (D-28b).
            swA.Stop();
            phaseMs["phaseA"] = swGreedy.ElapsedMilliseconds;
            phaseMs["modelBuildA"] = 0;
            diagnostics.Add("Greedy full but not compact-clean: CP-SAT A skipped, LS+repair will polish.");
            resA = new PhaseResult(
                greedyFullDirty.ToDictionary(p => p.OccurrenceId,
                    p => new SlotPick(p.DayIndex, p.SlotIndex, p.RoomId)),
                false, ct.IsCancellationRequested, 0, false);
        }
        else
        {
            resA = SolvePhase(problem, allowedPairs, roomList, roomIndex, options, useObjective: false,
                hints: null, greedy: ToSlotPicks(greedy.Placed, includeRooms: false), timeLimit: budgetA, ct: ct);
            swA.Stop();
            phaseMs["phaseA"] = swA.ElapsedMilliseconds;
            phaseMs["modelBuildA"] = resA.ModelBuildMs;
        }

        if (resA.Cancelled && resA.Values is null)
            return Cancelled(problem, sw, phaseMs, diagnostics, firstFeasibleMs: 0);
        if (resA.Values is null)
        {
            var status = resA.ProvenInfeasible ? SolverStatus.Infeasible : SolverStatus.Unknown;
            if (status == SolverStatus.Unknown)
                diagnostics.Add("No solution within limit (timeout ≠ infeasible).");
            var early = new SolverResult(status, [], 0, 0, sw.ElapsedMilliseconds, 0, 0,
                new Dictionary<string, long>(), diagnostics, phaseMs, resA.Cancelled);
            return resA.Stats is null ? early : early with { ModelStats = resA.Stats };
        }

        long firstFeasibleMs = sw.ElapsedMilliseconds;
        var bestValues = resA.Values;
        var bestQuality = SoftQuality(problem, bestValues);
        int solutionsFound = 1;

        if (!options.TwoPhaseSolve || budgetB <= 0.01)
            return Accept(problem, bestValues, bestQuality, solutionsFound,
                sw, phaseMs, diagnostics, firstFeasibleMs,
                wasCancelled: resA.Cancelled, optimalProven: false, stats: resA.Stats, rules: rs);

        // Фаза B': локальный поиск + GrindLite от baseline (time-boxed).
        // На большой школе CP-SAT Phase B не даёт инкумбентов — резервировать под
        // него половину бюджета бессмысленно: 45% идёт в LocalSearch, 45% — в
        // GrindLite (полный LNS-набор), 10% — на финальный RuinRecreate/CompactRepair.
        // Малые школы оставляют Phase B ~50% (LS 20% + GrindLite 20% + финал 10%).
        bool largeSchool = problem.Occurrences.Count >= LargeSchoolPhaseBThreshold;
        var swB = Stopwatch.StartNew();
        double BudgetLeft() => Math.Max(0, budgetB - swB.Elapsed.TotalSeconds);
        double Slice(double share, double min, double max) =>
            Math.Min(BudgetLeft(), Math.Clamp(budgetB * share, min, max));
        int seedBase = problem.Options.RandomSeed ?? 0;

        // Любой incumbent от LS/GrindLite идёт в тот же sink, что и CP-SAT:
        // график прогресса и Top-5 оживают по ходу поиска на любой школе.
        // Троттлинг ~250 мс — на финал он не распространяется.
        int sinkSeq = 0;
        long lastSinkMs = long.MinValue / 2;
        void Emit(IReadOnlyList<PlacedLesson> pl, long soft, bool force = false)
        {
            if (sink is null || pl.Count == 0) return;
            long now = sw.ElapsedMilliseconds;
            if (!force && now - lastSinkMs < 250) return;
            lastSinkMs = now;
            sink(new SolverIncumbent(pl, soft, ++sinkSeq, now, "B", options.RandomSeed));
        }

        double lsBudgetSec = Slice(largeSchool ? 0.45 : 0.20, 0.5, 300);
        var ls = LocalSearch.Improve(problem, ToPlacements(bestValues),
            TimeSpan.FromSeconds(lsBudgetSec), seedBase, ct, rules: rs, sink: sink);
        diagnostics.Add($"LocalSearch: soft {bestQuality}->{ls.SoftTotal} " +
            $"in {ls.ElapsedMs}ms, moves {ls.AcceptedMoves}.");
        if (ls.SoftTotal < bestQuality)
        {
            bestQuality = ls.SoftTotal;
            bestValues = ls.Placements.ToDictionary(
                p => p.OccurrenceId, p => new SlotPick(p.DayIndex, p.SlotIndex, p.RoomId));
        }

        // GrindLite.Polish — все LNS-операторы в одном месте (не только режим ТОП):
        // DayClose → TeacherDay → Sync → ThinDay → CrossShift → Swap → Overload →
        // CompactRepair → GradeOneBalance → PeSpacing → RuinRecreate (остаток).
        double grindBudgetSec = Slice(largeSchool ? 0.45 : 0.20, 0.5, 300);
        if (grindBudgetSec > 0)
        {
            var grind = GrindLite.Polish(problem, ToPlacements(bestValues), rs,
                TimeSpan.FromSeconds(grindBudgetSec), seedBase + 31, ct, sink);
            diagnostics.Add($"GrindLite: soft {bestQuality}->{grind.SoftTotal}, " +
                $"hard={grind.HardViolations}, tw={grind.TeacherWindows}, budget {grindBudgetSec:F1}s.");
            foreach (var line in grind.Log.TakeLast(6)) diagnostics.Add("GrindLite: " + line);
            if (grind.SoftTotal < bestQuality)
            {
                bestQuality = grind.SoftTotal;
                bestValues = grind.Placements.ToDictionary(
                    p => p.OccurrenceId, p => new SlotPick(p.DayIndex, p.SlotIndex, p.RoomId));
            }
        }

        double finalReserveSec = Math.Min(BudgetLeft(),
            Math.Clamp(budgetB * 0.10, 0.5, 60));

        // H6: на большой школе CP-SAT B не даёт инкумбентов — пропускаем
        // (качество уже ведёт LS+VND настоящим soft; Accept-gate без изменений).
        int rejectedB = 0;
        int seqB = 0;
        PhaseResult? resB = null;
        if (!largeSchool)
        {
            double cpBudget = Math.Max(0.5, BudgetLeft() - finalReserveSec);
            // Фаза B: hints + proxy-objective + callback best-so-far по SoftEvaluator.
            // Каждый incumbent проходит FullValidator (счётчик rejected — данные для P0.5/P1;
            // hard-нарушающий incumbent никогда не становится best, защита в глубину).
            var swPhaseB = Stopwatch.StartNew();
            resB = SolvePhase(problem, allowedPairs, roomList, roomIndex, options, useObjective: true,
                hints: bestValues.ToDictionary(kv => kv.Key, kv => kv.Value with { RoomId = null }),
                timeLimit: cpBudget, ct: ct,
                onIncumbent: (values, proxy) =>
                {
                    solutionsFound++;
                    seqB++;
                    var pl = values.Select(kv => new PlacedLesson
                    {
                        OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
                        SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
                    }).ToList();
                    sink?.Invoke(new SolverIncumbent(pl, proxy, seqB, sw.ElapsedMilliseconds, "B", options.RandomSeed));
                    if (!PlacementValidator.Validate(problem, pl).IsValid) { rejectedB++; return; }
                    long q = SoftEvaluator.Evaluate(problem, pl, rs).Total;
                    if (q < bestQuality) { bestQuality = q; bestValues = values; }
                });
            swPhaseB.Stop();
            phaseMs["phaseB"] = swPhaseB.ElapsedMilliseconds;
            phaseMs["modelBuildB"] = resB.ModelBuildMs;
            diagnostics.Add($"Phase B incumbents: {solutionsFound - 1}, validator-rejected: {rejectedB}.");
            if (resB.Values is not null)
            {
                // Финальный incumbent тоже кандидат — только через validator-gate (D-28b:
                // компактность — HARD, мягкий отбор недостаточен).
                var pl = resB.Values.Select(kv => new PlacedLesson
                {
                    OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
                    SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
                }).ToList();
                if (!PlacementValidator.Validate(problem, pl).IsValid) { rejectedB++; }
                else
                {
                    solutionsFound++;
                    long q = SoftQuality(problem, resB.Values, rs);
                    if (q < bestQuality) { bestQuality = q; bestValues = resB.Values; }
                }
            }
        } // end if Phase B
        else
        {
            phaseMs["phaseB"] = 0;
            phaseMs["modelBuildB"] = 0;
            diagnostics.Add($"Phase B skipped (large school: {problem.Occurrences.Count} occ >= {LargeSchoolPhaseBThreshold}); " +
                $"budget -> LocalSearch + GrindLite + final LNS/repair.");
        }

        bool cancelled = resB is null ? resA.Cancelled : resA.Cancelled || resB.Cancelled;
        // 10% бюджета B (или всё, что осталось): финальный ruin&recreate по
        // остаточным failedDays, затем дефрагментация перед Accept-гейтом
        // (solver-фазы компактность не моделируют; LS идёт по soft-градиенту,
        // ремонт доводит до HARD) + балансировка 1-х классов (междневное).
        {
            var occById = problem.Occurrences.ToDictionary(o => o.Id);
            var cur = ToPlacements(bestValues);
            int failed = RuinRecreate.FailedDays(problem, occById, cur);
            double finalBudgetSec = Math.Min(BudgetLeft(), Math.Max(finalReserveSec, 0.5));
            if (failed > 0 && finalBudgetSec >= 0.5 && !ct.IsCancellationRequested)
            {
                var lns = RuinRecreate.Improve(problem, cur, TimeSpan.FromSeconds(finalBudgetSec),
                    seedBase + 777, ct, rules: rs);
                diagnostics.Add($"RuinRecreate(final): failed {failed}->{lns.FailedDays}, " +
                    $"soft {bestQuality}->{lns.SoftTotal}, iters {lns.Iterations} accepted {lns.Accepted}.");
                if (lns.FailedDays < failed || (lns.FailedDays == failed && lns.SoftTotal < bestQuality))
                {
                    bestValues = lns.Placements.ToDictionary(
                        p => p.OccurrenceId, p => new SlotPick(p.DayIndex, p.SlotIndex, p.RoomId));
                    bestQuality = lns.SoftTotal;
                    Emit(lns.Placements, bestQuality, force: true);
                }
            }
            var rep = CompactRepair.Repair(problem, ToPlacements(bestValues));
            diagnostics.Add($"CompactRepair(final): дней починено {rep.RepairedDays}, " +
                $"не удалось {rep.FailedDays}.");
            foreach (var n in rep.Notes) diagnostics.Add("CompactRepair: " + n);
            var bal = GradeOneBalance.Balance(problem, rep.Placements);
            if (bal.Moved > 0 || bal.Notes.Count > 0)
                diagnostics.Add($"GradeOneBalance: перенесено {bal.Moved}.");
            foreach (var n in bal.Notes) diagnostics.Add("GradeOneBalance: " + n);
            bestValues = bal.Placements.ToDictionary(
                p => p.OccurrenceId, p => new SlotPick(p.DayIndex, p.SlotIndex, p.RoomId));
            bestQuality = SoftQuality(problem, bestValues, rs);
        }
        var accepted = Accept(problem, bestValues, bestQuality, solutionsFound,
            sw, phaseMs, diagnostics, firstFeasibleMs,
            wasCancelled: cancelled, optimalProven: (resB?.Optimal ?? false) && !cancelled,
            stats: resB?.Stats ?? resA.Stats, rules: rs);
        // Финал в sink: даже если промежуточные incumbents не прошли validator-gate,
        // график получает точку итогового расписания.
        Emit(accepted.Placements, accepted.ObjectiveValue, force: true);
        return accepted;
    }

    private static SolverResult Cancelled(SchedulingProblem problem, Stopwatch sw,
        Dictionary<string, long> phaseMs, List<string> diagnostics, long firstFeasibleMs)
    {
        diagnostics.Add("Cancelled without feasible solution.");
        return new SolverResult(SolverStatus.Unknown, [], 0, 0,
            sw.ElapsedMilliseconds, firstFeasibleMs, 0,
            new Dictionary<string, long>(), diagnostics, phaseMs, WasCancelled: true);
    }

    // INV-01: gate FullValidator перед принятием.
    private static SolverResult Accept(
        SchedulingProblem problem,
        Dictionary<Guid, SlotPick> values,
        long bestQuality, int solutionsFound,
        Stopwatch sw, Dictionary<string, long> phaseMs, List<string> diagnostics,
        long firstFeasibleMs, bool wasCancelled, bool optimalProven = false,
        IReadOnlyDictionary<string, long>? stats = null,
        EffectiveRuleSet? rules = null)
    {
        var placements = values.Select(kv => new PlacedLesson
        {
            OccurrenceId = kv.Key, DayIndex = kv.Value.Day, SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
        }).ToList();
        var validation = PlacementValidator.Validate(problem, placements);
        if (!validation.IsValid)
        {
            diagnostics.Add($"VALIDATOR REJECTED: {validation.HardViolations.Count} hard violations.");
            foreach (var v in validation.HardViolations.Take(5)) diagnostics.Add(v.Message);
            return new SolverResult(SolverStatus.Unknown, [], 0, solutionsFound,
                sw.ElapsedMilliseconds, firstFeasibleMs, validation.HardViolations.Count,
                new Dictionary<string, long>(), diagnostics, phaseMs, wasCancelled);
        }
        var breakdown = SoftEvaluator.Evaluate(problem, placements, rules ?? EffectiveRuleSet.Default);
        var withPeak = stats is null ? null : new Dictionary<string, long>(stats)
        {
            ["peakMemoryMb"] = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576,
        };
        return new SolverResult(
            wasCancelled ? SolverStatus.Feasible : SolverStatus.Feasible,
            placements, breakdown.Total, solutionsFound,
            sw.ElapsedMilliseconds, firstFeasibleMs, 0,
            breakdown.Components.ToDictionary(c => c.Code, c => c.Value),
            diagnostics, phaseMs, wasCancelled, optimalProven, withPeak);
    }

    private static long SoftQuality(
        SchedulingProblem problem, Dictionary<Guid, SlotPick> values, EffectiveRuleSet? rules = null)
    {
        var placements = values.Select(kv => new PlacedLesson
        {
            OccurrenceId = kv.Key, DayIndex = kv.Value.Day, SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
        }).ToList();
        return SoftEvaluator.Evaluate(problem, placements, rules ?? EffectiveRuleSet.Default).Total;
    }

    // D-61: ключ качества greedy-старта после CompactRepair — для тайбрейка
    // портфеля при равном покрытии (FailedDays → TeacherGridGaps → SoftTotal).
    private static (int FailedDays, int Gaps, long Soft) GreedyMetricsKey(
        SchedulingProblem problem, GreedyPlacement g, EffectiveRuleSet rs,
        Dictionary<Guid, LessonOccurrence> occById)
    {
        var pl = g.Placed.Select(kv => new PlacedLesson
        {
            OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
            SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
        }).ToList();
        var rep = CompactRepair.Repair(problem, pl);
        return (RuinRecreate.FailedDays(problem, occById, rep.Placements),
            TeacherDayLns.TeacherGridGaps(occById, rep.Placements),
            SoftEvaluator.Evaluate(problem, rep.Placements, rs).Total);
    }

    private static int CmpKey((int FailedDays, int Gaps, long Soft) a, (int FailedDays, int Gaps, long Soft) b) =>
        a.FailedDays != b.FailedDays ? a.FailedDays.CompareTo(b.FailedDays) :
        a.Gaps != b.Gaps ? a.Gaps.CompareTo(b.Gaps) :
        a.Soft.CompareTo(b.Soft);

    private static Dictionary<Guid, SlotPick> ToSlotPicks(
        Dictionary<Guid, (int Day, int Slot, Guid? RoomId)> placed, bool includeRooms = true) =>
        placed.ToDictionary(kv => kv.Key,
            kv => new SlotPick(kv.Value.Day, kv.Value.Slot,
                includeRooms ? kv.Value.RoomId : null));

    private static List<PlacedLesson> ToPlacements(Dictionary<Guid, SlotPick> values) =>
        values.Select(kv => new PlacedLesson
        {
            OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
            SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
        }).ToList();

    private sealed record PhaseResult(
        Dictionary<Guid, SlotPick>? Values,
        bool ProvenInfeasible, bool Cancelled, long ModelBuildMs, bool Optimal,
        IReadOnlyDictionary<string, long>? Stats = null);

    private static PhaseResult SolvePhase(
        SchedulingProblem problem,
        Dictionary<Guid, List<(int Day, int Slot)>> allowedPairs,
        IList<Room> roomList,
        Dictionary<Guid, int> roomIndex,
        SolverOptions options,
        bool useObjective,
        Dictionary<Guid, SlotPick>? hints,
        double timeLimit,
        CancellationToken ct,
        Dictionary<Guid, SlotPick>? greedy = null,
        Action<Dictionary<Guid, SlotPick>, long>? onIncumbent = null)
    {
        var swBuild = Stopwatch.StartNew();
        var model = new CpModel();
        var occs = problem.Occurrences;
        int n = occs.Count;
        // Счётчики диагностики (§9 брифа): точные числа vars/constraints этой фазы.
        long nInt = 0, nBool = 0, nCt = 0;

        // x[i] — индекс в allowedPairs; t[i] — глобальный слот day*SP+slot; d[i] — день.
        var x = new IntVar[n];
        var t = new IntVar[n];
        var d = new IntVar[n];
        for (int i = 0; i < n; i++)
        {
            var pairs = allowedPairs[occs[i].Id];
            var tVals = pairs.Select(p => p.Day * problem.SlotsPerDay + p.Slot).ToList();
            var dVals = pairs.Select(p => p.Day).ToList();
            x[i] = model.NewIntVar(0, pairs.Count - 1, $"x{i}"); nInt++;
            t[i] = model.NewIntVar(tVals.Min(), tVals.Max(), $"t{i}"); nInt++;
            model.AddElement(x[i], tVals.ToArray(), t[i]); nCt++;
            d[i] = model.NewIntVar(0, problem.DaysCount - 1, $"d{i}"); nInt++;
            model.AddElement(x[i], dVals.ToArray(), d[i]); nCt++;
        }

        // Teacher collision (Hard).
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                if (occs[i].TeacherId == occs[j].TeacherId)
                { model.Add(t[i] != t[j]); nCt++; }

        // Class/subgroup collision (Hard): тот же ключ, что в validator + whole-блокировка.
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                var a = occs[i]; var b = occs[j];
                bool blocks = (!a.GroupId.HasValue || !b.GroupId.HasValue)
                    ? a.ClassId == b.ClassId
                    : (a.GroupId == b.GroupId);
                if (!a.GroupId.HasValue && a.ClassId == b.ClassId) blocks = true;
                if (!b.GroupId.HasValue && a.ClassId == b.ClassId) blocks = true;
                if (blocks) { model.Add(t[i] != t[j]); nCt++; }
            }

        // Sync equality (Hard INV-03).
        var syncGroups = occs.Where(o => o.SyncGroupId.HasValue).GroupBy(o => o.SyncGroupId!.Value);
        foreach (var g in syncGroups)
        {
            var idx = g.Select(o => occs.IndexOf(o)).ToList();
            for (int k = 1; k < idx.Count; k++)
            { model.Add(t[idx[0]] == t[idx[k]]); nCt++; }
        }

        // E3 perturbation bans (Hard): запрещённые глобальные времена.
        for (int i = 0; i < n; i++)
        {
            if (!problem.BannedTimes.TryGetValue(occs[i].Id, out var banned)) continue;
            foreach (int g in banned)
            { model.Add(t[i] != g); nCt++; }
        }

        // Teacher MaxPerDay (Hard FROZEN D-04): sum((d[i]==day)) <= max.
        foreach (var tg in occs.Select((o, i) => (o.TeacherId, i)).GroupBy(p => p.TeacherId))
        {
            if (!problem.Teachers.TryGetValue(tg.Key, out var teacher)) continue;
            for (int day = 0; day < problem.DaysCount; day++)
            {
                var bools = new List<BoolVar>();
                foreach (var (_, i) in tg)
                {
                    var b = model.NewBoolVar($"m{tg.Key}_{day}_{i}"); nBool++;
                    model.Add(d[i] == day).OnlyEnforceIf(b); nCt++;
                    model.Add(d[i] != day).OnlyEnforceIf(b.Not()); nCt++;
                    bools.Add(b);
                }
                model.Add(LinearExpr.Sum(bools) <= teacher.MaxLessonsPerDay); nCt++;
            }
        }

        // Кабинеты (Hard, lane-модель D-24): комната с вместимостью C — это C
        // идентичных линий; occurrence выбирает линию через Element; присутствие на
        // линии — один BoolVar на (occurrence, линия); NoOverlap на линию.
        // Сложность O(n·L) вместо O(n·R·T) реификации (было OOM на 1200×40×42).
        // Эквивалентность: unit-интервалы размера 1 на целочисленном времени +
        // C линий ⟺ не более C одновременных на комнату; Forbidden/ONLY/ManualOnly — в кандидатах.
        IntVar?[] rVar = new IntVar?[n];
        IntVar?[] rIdxVar = new IntVar?[n];
        List<int>[] roomCands = new List<int>[n]; // ИНДЕКСЫ ЛИНИЙ-кандидатов
        List<int> laneRoom = []; // lane -> индекс комнаты в roomList
        if (roomList.Count > 0)
        {
            for (int r = 0; r < roomList.Count; r++)
                for (int c = 0; c < Math.Max(1, roomList[r].MaxSimultaneousGroups); c++)
                    laneRoom.Add(r);
            for (int i = 0; i < n; i++)
            {
                var roomSet = new HashSet<int>(RoomCandidates(
                    problem, (List<Room>)roomList, roomIndex, occs[i]));
                roomCands[i] = [];
                for (int l = 0; l < laneRoom.Count; l++)
                    if (roomSet.Contains(laneRoom[l])) roomCands[i].Add(l);
                // Домен через Element (без table constraint): rIdx -> глобальная линия.
                var rIdx = model.NewIntVar(0, roomCands[i].Count - 1, $"ri{i}"); nInt++;
                rIdxVar[i] = rIdx;
                rVar[i] = model.NewIntVar(0, laneRoom.Count - 1, $"r{i}"); nInt++;
                model.AddElement(rIdx, roomCands[i].ToArray(), rVar[i]!); nCt++;
            }
            var laneUsers = new List<List<IntervalVar>>(laneRoom.Count);
            for (int l = 0; l < laneRoom.Count; l++) laneUsers.Add([]);
            for (int i = 0; i < n; i++)
            {
                foreach (int l in roomCands[i])
                {
                    var b = model.NewBoolVar($"p{i}_{l}"); nBool++;
                    model.Add(rVar[i]! == l).OnlyEnforceIf(b); nCt++;
                    model.Add(rVar[i]! != l).OnlyEnforceIf(b.Not()); nCt++;
                    laneUsers[l].Add(model.NewOptionalFixedSizeIntervalVar(t[i], 1, b, $"iv{i}_{l}"));
                }
            }
            for (int l = 0; l < laneRoom.Count; l++)
                if (laneUsers[l].Count > 0)
                { model.AddNoOverlap(laneUsers[l]); nCt++; }
        }

        // Proxy objective фазы B: компактность (сумма t) — детерминированный tie-break;
        // реальное качество — SoftEvaluator в callback (см. шапку класса).
        if (useObjective)
        {
            var sum = LinearExpr.Sum(t);
            model.Minimize(sum);
        }

        // Hints: точное решение фазы A для B; жадный старт для A.
        // Частичность допустима: CP-SAT достраивает остальное.
        var hh = hints ?? greedy;
        int hintsApplied = 0;
        if (hh is not null)
        {
            for (int i = 0; i < n; i++)
            {
                if (!hh.TryGetValue(occs[i].Id, out var hv)) continue;
                int idx = allowedPairs[occs[i].Id].FindIndex(p => p.Day == hv.Day && p.Slot == hv.Slot);
                if (idx >= 0) model.AddHint(x[i], idx);
                if (hv.RoomId.HasValue && roomIndex.TryGetValue(hv.RoomId.Value, out int ri) && rVar[i] is not null)
                {
                    int pos = roomCands[i].FindIndex(l => laneRoom[l] == ri);
                    if (pos >= 0) model.AddHint(rIdxVar[i]!, pos);
                }
                if (idx >= 0) hintsApplied++;
            }
        }

        // E12/D-24: стратегия ветвления под earliest-fit (как greedy): время по возрастанию.
        // Семантику не меняет; даёт поиску направление к первому решению и инкумбентам
        // вместо блуждания по тяжёлым NoOverlap-пропагациям (branches ~60/с без неё).
        model.AddDecisionStrategy(x,
            DecisionStrategyProto.Types.VariableSelectionStrategy.ChooseFirst,
            DecisionStrategyProto.Types.DomainReductionStrategy.SelectMinValue);
        nCt++;

        var solver = new CpSolver();
        // Presolve-off только для Phase A без objective (feasibility): probing иначе
        // съедает бюджет до первой ветки. Семантика модели не меняется.
        bool skipPresolve = !useObjective && !options.PresolveInPhaseA;
        solver.StringParameters = $"max_time_in_seconds:{timeLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
            $"num_search_workers:{options.NumSearchWorkers}" +
            (options.RandomSeed.HasValue ? $",random_seed:{options.RandomSeed.Value}" : "") +
            (skipPresolve ? ",cp_model_presolve:false" : "");
        swBuild.Stop();
        long modelBuildMs = swBuild.ElapsedMilliseconds;
        IReadOnlyDictionary<string, long> stats = new Dictionary<string, long>
        {
            ["occCount"] = n,
            ["slotCount"] = (long)problem.DaysCount * problem.SlotsPerDay,
            ["roomCount"] = roomList.Count,
            ["laneCount"] = laneRoom.Count,
            ["avgLanesPerOcc"] = roomList.Count == 0 || n == 0 ? 0
                : (long)roomCands.Where(c => c is not null).Average(c => c.Count),
            ["intVars"] = nInt,
            ["boolVars"] = nBool,
            ["constraints"] = nCt,
            ["hintsApplied"] = hintsApplied,
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.Token.Register(() => { try { solver.StopSearch(); } catch { } });

        Dictionary<Guid, SlotPick>? extract()
        {
            var dict = new Dictionary<Guid, SlotPick>();
            for (int i = 0; i < n; i++)
            {
                int xi = (int)solver.Value(x[i]);
                var p = allowedPairs[occs[i].Id][xi];
                Guid? roomId = rVar[i] is null ? null
                    : roomList[laneRoom[(int)solver.Value(rVar[i]!)]].Id;
                dict[occs[i].Id] = new SlotPick(p.Day, p.Slot, roomId);
            }
            return dict;
        }

        CpSolverStatus st;
        if (onIncumbent is not null && useObjective)
        {
            var cb = new IncumbentCallback(x, rVar, roomList, laneRoom, occs, allowedPairs, onIncumbent);
            st = solver.Solve(model, cb);
            if (cb.Best is not null) return new PhaseResult(cb.Best, false, cts.IsCancellationRequested, modelBuildMs, st == CpSolverStatus.Optimal, stats);
        }
        else
        {
            st = solver.Solve(model);
        }

        bool cancelled = cts.IsCancellationRequested;
        return st switch
        {
            CpSolverStatus.Optimal or CpSolverStatus.Feasible => new PhaseResult(extract(), false, cancelled, modelBuildMs, st == CpSolverStatus.Optimal, stats),
            CpSolverStatus.Infeasible => new PhaseResult(null, true, cancelled, modelBuildMs, false, stats),
            _ => new PhaseResult(null, false, cancelled, modelBuildMs, false, stats),
        };
    }

    private sealed class IncumbentCallback(
        IntVar[] x,
        IntVar?[] rVar,
        IList<Room> roomList,
        IList<int> laneRoom,
        IReadOnlyList<LessonOccurrence> occs,
        Dictionary<Guid, List<(int Day, int Slot)>> allowedPairs,
        Action<Dictionary<Guid, SlotPick>, long> onIncumbent) : CpSolverSolutionCallback
    {
        public Dictionary<Guid, SlotPick>? Best { get; private set; }

        public override void OnSolutionCallback()
        {
            var dict = new Dictionary<Guid, SlotPick>();
            for (int i = 0; i < x.Length; i++)
            {
                int xi = (int)Value(x[i]);
                var p = allowedPairs[occs[i].Id][xi];
                Guid? roomId = rVar[i] is null ? null
                    : roomList[laneRoom[(int)Value(rVar[i]!)]].Id;
                dict[occs[i].Id] = new SlotPick(p.Day, p.Slot, roomId);
            }
            Best = dict;
            onIncumbent(dict, (long)ObjectiveValue());
        }
    }
}
