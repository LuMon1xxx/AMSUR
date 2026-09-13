using System.Diagnostics;
using Amsur.Scheduling.Core;

namespace Amsur.Application;

// E4 §1/§8/§11 — оркестратор генерации.
// Multi-Seed short-budget (D-18) + общий архив + throttled progress.
// Application НЕ ссылается на OrTools: запуск инжектится делегатом
// (продакшн wire — в композиции/WPF, тесты — фейк). Архив — Core, без WPF.
public delegate Task<SolverResult> StreamingRun(
    SchedulingProblem problem, CancellationToken ct, Action<SolverIncumbent> sink);

public sealed class GenerationOrchestrator
{
    private readonly Func<int, SchedulingProblem> _problemFactory;
    private readonly StreamingRun _run;
    private readonly ThrottledIncumbentStream _stream;
    private readonly GenerateViewModel _vm;
    private readonly ScheduleCandidateArchive _archive;
    private readonly TrajectoryHistory _history = new(); // E6: только полученные события
    private readonly AcceptScheduleService? _acceptor; // E7: null — хранилище не подключено
    // E5: последняя построенная задача — имена для QualityLines (вход один,
    // имена совпадают между seeds; Guid различаются, StableKey — нет).
    private SchedulingProblem? _lastProblem;

    public GenerationOrchestrator(
        Func<int, SchedulingProblem> problemFactory,
        StreamingRun run,
        ThrottledIncumbentStream? stream = null,
        GenerateViewModel? vm = null,
        int archiveCapacity = 5,
        AcceptScheduleService? acceptor = null)
    {
        _problemFactory = problemFactory;
        _run = run;
        _stream = stream ?? new ThrottledIncumbentStream();
        _vm = vm ?? new GenerateViewModel();
        _acceptor = acceptor;
        _archive = new ScheduleCandidateArchive(archiveCapacity, 100);
        _stream.ProgressChanged += dto =>
        {
            _history.Append(dto);
            _vm.ApplyProgress(dto);
            _vm.RefreshTrajectory(_history.Snapshot());
        };
        _vm.StopRequested += () => _stopCts?.Cancel();
        _vm.AcceptRequested += card => _ = AcceptAsync(card);
    }

    private CancellationTokenSource? _stopCts;

    public IIncumbentStream Stream => _stream;
    public GenerateViewModel ViewModel => _vm;
    public ScheduleCandidateArchive Archive => _archive;
    public TrajectoryHistory History => _history;

    /// <summary>Активный набор весов (S5): задаётся до RunAsync; по умолчанию STANDARD.</summary>
    public EffectiveRuleSet Rules { get; set; } = EffectiveRuleSet.Default;

    public async Task<GenerationOutcome> RunAsync(
        IReadOnlyList<int> seeds, CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _stopCts = linked;
        var wall = Stopwatch.StartNew();
        int candidateCount = 0;
        long? bestSoft = null;
        TimeSpan? firstFeasibleAt = null;
        bool hasFeasible = false;
        var statuses = new List<string>();

        Report(GenerationPhase.Preparing, wall, bestSoft, candidateCount,
            "Подготовка…", hasFeasible, firstFeasibleAt, SeedOrRun(seeds));

        for (int r = 0; r < seeds.Count; r++)
        {
            int seed = seeds[r];
            if (linked.IsCancellationRequested) break;
            var problem = _problemFactory(seed);
            _lastProblem = problem;
            bool runHadFeasible = hasFeasible;
            var phase = hasFeasible ? GenerationPhase.Improving : GenerationPhase.SeekingFeasible;

            SolverResult result;
            try
            {
                result = await _run(problem, linked.Token, inc =>
                {
                    // Full-rate path: solver частота, UI — троттлинг (§3).
                    var c = ScheduleCandidate.Create(problem, inc.Placements, seed, inc.Proxy, inc.Phase,
                        rules: Rules);
                    if (c is null) return;
                    candidateCount++;
                    _archive.TryAdd(c);
                    if (!hasFeasible)
                    {
                        hasFeasible = true;
                        firstFeasibleAt = wall.Elapsed;
                        runHadFeasible = true;
                    }
                    if (bestSoft is null || c.SoftTotal < bestSoft) bestSoft = c.SoftTotal;
                    RefreshTop5();
                    Report(GenerationPhase.Improving, wall, bestSoft, candidateCount,
                        "Ищем подходящее расписание…", hasFeasible, firstFeasibleAt, SeedOrRun(seeds, r));
                });
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Финал запуска тоже кандидат (фаза A не идёт через sink).
            if (result.Placements.Count > 0)
            {
                var c = ScheduleCandidate.Create(problem, result.Placements, seed, result.ObjectiveValue, "B",
                    rules: Rules);
                if (c is not null)
                {
                    candidateCount++;
                    _archive.TryAdd(c);
                    if (!hasFeasible) { hasFeasible = true; firstFeasibleAt = wall.Elapsed; }
                    if (bestSoft is null || c.SoftTotal < bestSoft) bestSoft = c.SoftTotal;
                    RefreshTop5();
                }
            }
            statuses.Add(result.Status.ToString());
            _ = runHadFeasible;
            UpdateDiagnostics(seeds, problem, result, wall, bestSoft, candidateCount);
            Report(hasFeasible ? GenerationPhase.Improving : GenerationPhase.SeekingFeasible,
                wall, bestSoft, candidateCount, "Ищем подходящее расписание…",
                hasFeasible, firstFeasibleAt, SeedOrRun(seeds, r));
        }

        GenerationPhase endPhase;
        string endStatus;
        if (linked.IsCancellationRequested || ct.IsCancellationRequested)
        {
            // §8: best-so-far не теряется, архив в памяти, варианты доступны.
            endPhase = GenerationPhase.Stopped;
            endStatus = hasFeasible ? "Генерация остановлена" : "Рабочее расписание не найдено";
        }
        else if (!hasFeasible)
        {
            // §8: timeout ≠ infeasible — НИКОГДА не пишем «Расписание невозможно».
            endPhase = GenerationPhase.Stopped;
            endStatus = "Рабочее расписание не найдено";
        }
        else
        {
            Report(GenerationPhase.FormingVariants, wall, bestSoft, candidateCount,
                "Формируем варианты…", hasFeasible, firstFeasibleAt, SeedOrRun(seeds));
            endPhase = GenerationPhase.Done;
            endStatus = hasFeasible ? "Готово" : "Рабочее расписание не найдено";
        }

        var dto = new GenerationProgressDto(endPhase, wall.Elapsed, bestSoft, null,
            candidateCount, _archive.Members.Count, 5, SeedOrRun(seeds),
            endStatus, hasFeasible, firstFeasibleAt);
        _stream.Report(dto);
        _stream.Flush();
        RefreshTop5();
        _stopCts = null;
        return new GenerationOutcome(endPhase, endStatus, hasFeasible, bestSoft,
            candidateCount, _archive.Members.Count, firstFeasibleAt, wall.Elapsed);
    }

    // E7 — приёмка лучшего варианта в активное хранилище (CANDIDATE volatile → VERSION persist, D-06).
    // Тесты вызывают напрямую; окно — через событие AcceptRequested (только CanAccept-карточки).
    public async Task<AcceptOutcome> AcceptAsync(CandidateCardModel? card, CancellationToken ct = default)
    {
        if (card is not { CanAccept: true })
            return Fail("Принять можно только лучший вариант.");
        if (_lastProblem is null)
            return Fail("Нет данных генерации.");
        if (_acceptor is null)
            return Fail("Хранилище не подключено — принять нельзя.");
        var year = _lastProblem.Classes.Values.FirstOrDefault()?.AcademicYearId ?? Guid.Empty;
        if (year == Guid.Empty)
            return Fail("Нет учебного года — принять нельзя.");
        var validation = PlacementValidator.Validate(_lastProblem, card.Candidate.Placements);
        if (!validation.IsValid)
            return Fail($"Не принято: {validation.HardViolations.Count} жёстких нарушений — вариант не сохранён.");
        var c = card.Candidate;
        var settings =
            $"soft={c.SoftTotal} seed={c.Seed} proxy={c.Proxy} phase={c.Phase} fp={c.Fingerprint}";
        var (accepted, breakdown) = await _acceptor.AcceptAsync(
            year, _lastProblem, c.Placements, $"Принят вариант {card.Rank} из Top-5",
            settings, ct);
        _vm.ActiveVersionText = $"Активна версия {accepted.Number}";
        return Ok($"Принято: версия {accepted.Number}, оценка Soft {breakdown.Total}.", accepted);

        AcceptOutcome Fail(string message)
        {
            _vm.AcceptResultText = message;
            return new AcceptOutcome(false, message, null);
        }
        AcceptOutcome Ok(string message, AcceptedVersion accepted)
        {
            _vm.AcceptResultText = message;
            return new AcceptOutcome(true, message, accepted);
        }
    }

    public sealed record AcceptOutcome(bool Succeeded, string Message, AcceptedVersion? Accepted);

    private void RefreshTop5() => _vm.Top5 = Top5PanelModel.FromArchive(_archive, _lastProblem);

    private void Report(GenerationPhase phase, Stopwatch wall, long? bestSoft,
        int candidateCount, string status, bool hasFeasible, TimeSpan? firstAt, string run)
    {
        _stream.Report(new GenerationProgressDto(phase, wall.Elapsed, bestSoft, null,
            candidateCount, _archive.Members.Count, 5, run, status, hasFeasible, firstAt));
    }

    private void UpdateDiagnostics(IReadOnlyList<int> seeds, SchedulingProblem problem,
        SolverResult result, Stopwatch wall, long? bestSoft, int candidateCount)
    {
        string phaseAB = string.Join("; ", result.PhaseMs.Select(kv => $"{kv.Key}={kv.Value}мс"));
        _vm.Diagnostics = new GenerationDiagnostics(
            string.Join(",", seeds), problem.Options.NumSearchWorkers.ToString(),
            string.IsNullOrEmpty(phaseAB) ? "-" : phaseAB,
            result.Status.ToString(), $"{wall.Elapsed.TotalSeconds:F1} с",
            bestSoft?.ToString() ?? "—",
            result.HardViolations == 0 ? "Hard=0" : $"Hard={result.HardViolations}",
            $"pool={candidateCount} archive={_archive.Members.Count}/5");
    }

    private static string SeedOrRun(IReadOnlyList<int> seeds, int idx = -1) =>
        idx < 0 ? $"seeds {string.Join(",", seeds)}" : $"run {idx + 1}/{seeds.Count} (seed {seeds[idx]})";
}

public sealed record GenerationOutcome(
    GenerationPhase Phase, string Status, bool HasFeasible, long? BestSoft,
    int CandidateCount, int ArchiveCount, TimeSpan? FirstFeasibleAt, TimeSpan Elapsed);
