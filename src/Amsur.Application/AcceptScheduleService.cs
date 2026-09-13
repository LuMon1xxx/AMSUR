namespace Amsur.Application;

using Amsur.Scheduling.Core;

// Тонкий use-case приёмки (EPIC-C C1): validator-before-write на уровне приложения
// + атомарная запись через IScheduleStore (хранилище перепроверяет внутри транзакции).
// INV-01: при HardViolations > 0 — исключение, актив не заменяется.
public sealed class AcceptScheduleService(IScheduleStore store)
{
    public async Task<(AcceptedVersion Accepted, PenaltyBreakdown Breakdown)> AcceptAsync(
        Guid academicYearId,
        SchedulingProblem problem,
        IReadOnlyList<PlacedLesson> placements,
        string reason,
        string solverSettings,
        CancellationToken ct = default)
    {
        var validation = PlacementValidator.Validate(problem, placements);
        if (!validation.IsValid)
            throw new InvalidOperationException(
                $"Refusing to accept schedule: {validation.HardViolations.Count} hard violations " +
                $"({string.Join("; ", validation.HardViolations.Take(3).Select(v => v.Message))}).");
        var breakdown = SoftEvaluator.Evaluate(problem, placements);
        var accepted = await store.AcceptAsync(
            academicYearId, problem, placements, reason, solverSettings, ct);
        return (accepted, breakdown);
    }
}
