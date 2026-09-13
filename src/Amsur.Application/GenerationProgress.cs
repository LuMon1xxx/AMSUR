using Amsur.Scheduling.Core;

namespace Amsur.Application;

// E4 — Live Progress contract (§2 ТЗ).
// Стабильный application-level DTO: UI НЕ видит внутренние объекты CP-SAT
// (SolverIncumbent остаётся в Core/OrTools, сюда маппится адаптером).
// Ни proxy, ни seed, ни worker count в пользовательский слой не утекают
// (они доступны только через GenerationDiagnostics для Advanced-панели).

/// <summary>Фазы генерации для UI-стадий (без ложного процента).</summary>
public enum GenerationPhase
{
    Preparing,       // Подготовка
    SeekingFeasible, // Поиск рабочего решения
    Improving,       // Улучшение
    FormingVariants, // Формирование вариантов
    Done,            // Завершено
    Stopped,         // Остановлено пользователем / отменено
}

/// <summary>Стабильный DTO прогресса (application-level).</summary>
public sealed record GenerationProgressDto(
    GenerationPhase Phase,
    TimeSpan Elapsed,
    long? BestSoft,          // null — feasible ещё нет
    long? Proxy,             // только для diagnostics; карточки Top-5 его НЕ показывают
    int CandidateCount,      // всего incumbents принято в пул
    int ArchiveCount,        // членов архива (<=K)
    int ArchiveCapacity,     // K
    string SeedOrRun,        // "seed 11" / "run 2/3" — только для diagnostics
    string Status,           // человеческий статус (RU)
    bool HasFeasible,
    TimeSpan? FirstFeasibleAt);

/// <summary>Поток прогресса для WPF ViewModel (§11: adapter → IIncumbentStream → VM).</summary>
public interface IIncumbentStream
{
    event Action<GenerationProgressDto>? ProgressChanged;
    GenerationProgressDto Current { get; }
}

/// <summary>Адаптер OrTools-callback → DTO (§11). Чистая функция, без CP-SAT типов.</summary>
public static class IncumbentProgressAdapter
{
    public static GenerationProgressDto Adapt(
        SolverIncumbent incumbent,
        TimeSpan elapsed,
        long? bestSoft,
        int candidateCount,
        int archiveCount,
        int archiveCapacity,
        bool hasFeasible,
        TimeSpan? firstFeasibleAt,
        string status,
        GenerationPhase phase) =>
        new(phase, elapsed, bestSoft, incumbent.Proxy,
            candidateCount, archiveCount, archiveCapacity,
            $"seed {incumbent.Seed?.ToString() ?? "?"}",
            status, hasFeasible, firstFeasibleAt);
}
