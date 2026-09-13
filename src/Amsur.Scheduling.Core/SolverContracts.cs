namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// Контракты solver P0 (без Application-проекта; D: Abstractions только при необходимости).
// SolverStatus — из Domain (единый enum, D-10: нового не вводим).
public sealed record SolverOptions(
    double MaxTimeSeconds = 30,
    int NumSearchWorkers = 8,
    int? RandomSeed = null,
    bool TwoPhaseSolve = true,
    // E12/D-24: presolve-probing съедает весь бюджет Phase A на большой школе
    // (branches: 0 за 7.5с) — для фазы feasibility его можно отключить: поиск идёт
    // по hints сразу. Phase B (оптимизация) presolve оставляет.
    bool PresolveInPhaseA = true);

public sealed record SolverResult(
    SolverStatus Status,
    IReadOnlyList<PlacedLesson> Placements,
    long ObjectiveValue,
    int SolutionsFound,
    long ElapsedMs,
    long FirstFeasibleMs,
    int HardViolations,
    IReadOnlyDictionary<string, long> Breakdown,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyDictionary<string, long> PhaseMs,
    bool WasCancelled = false,
    bool OptimalProven = false,
    IReadOnlyDictionary<string, long>? ModelStats = null);

public interface IScheduleSolver
{
    Task<SolverResult> SolveAsync(SchedulingProblem problem, CancellationToken ct = default);
}

/// <summary>
/// Один VISITED incumbent solver (E1): НЕ предполагается отсортированным по soft.
/// Архивный pipeline: Candidate → FullValidator → SoftEvaluator → Archive.
/// </summary>
public sealed record SolverIncumbent(
    IReadOnlyList<PlacedLesson> Placements,
    long Proxy,        // значение proxy-objective (сумма t) на момент incumbent
    int Sequence,      // порядковый номер среди incumbents запуска
    long ElapsedMs,    // время от старта SolveAsync
    string Phase,      // "A" | "B"
    int? Seed);
