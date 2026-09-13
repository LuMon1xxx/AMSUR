namespace Amsur.Scheduling.Core;

// Граница persistence (D-03/D-13): интерфейс живёт в Core, чтобы зависимости шли
// Application→Core и Infrastructure→Core (запрещено Infrastructure→Application).
// Single source of truth активности — ScheduleVersion.IsActive.
public sealed record AcceptedVersion(Guid VersionId, Guid ScheduleId, int Number);

public sealed record ActiveSchedule(
    Guid ScheduleId,
    Guid VersionId,
    int Number,
    string Reason,
    string SolverSettingsJson,
    string RuleSetVersion,
    IReadOnlyList<PlacedLesson> Placements);

public interface IScheduleStore
{
    Task InitializeAsync(CancellationToken ct = default);

    /// <returns>Журнал починки (пусто — чинить было нечего).</returns>
    Task<IReadOnlyList<string>> RepairAsync(CancellationToken ct = default);

    /// <summary>
    /// Атомарная приёмка (INV-01): FullValidator-gate ДО записи; при Hard&gt;0 —
    /// InvalidOperationException БЕЗ записи. Внутри одной транзакции: деактивация
    /// старых + вставка schedule/placements/version + активация новой.
    /// </summary>
    Task<AcceptedVersion> AcceptAsync(
        Guid academicYearId,
        SchedulingProblem problem,
        IReadOnlyList<PlacedLesson> placements,
        string reason,
        string solverSettings,
        CancellationToken ct = default);

    Task<ActiveSchedule?> GetActiveAsync(Guid academicYearId, CancellationToken ct = default);

    Task<int> CountActiveVersionsAsync(Guid academicYearId, CancellationToken ct = default);
}
