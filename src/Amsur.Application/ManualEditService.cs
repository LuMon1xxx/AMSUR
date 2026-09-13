using Amsur.Scheduling.Core;

namespace Amsur.Application;

// E8 — ручная правка активного расписания (превью без solver + коммит новой версией).
// Preview — скоуповый IncrementalEvaluator; Commit — только CanCommit + полный
// FullValidator-gate (INV-01) через AcceptScheduleService. Без solver, без DnD-UI
// (экран редактора — следующий этап; здесь use-case + честность причин).

public sealed record EditPreview(
    bool CanCommit,
    string VerdictText,
    long SoftDelta,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<PlacedLesson> ResultingPlacements);

public sealed record EditCommitOutcome(
    bool Committed, string Message, AcceptedVersion? Accepted);

public sealed class ManualEditService(AcceptScheduleService acceptor)
{
    public EditPreview Preview(
        SchedulingProblem problem,
        IReadOnlyList<PlacedLesson> current,
        CandidateMove move)
    {
        var ev = IncrementalEvaluator.Evaluate(problem, current, move);
        var resulting = current
            .Where(p => p.OccurrenceId != move.OccurrenceId)
            .Concat([new PlacedLesson
            {
                OccurrenceId = move.OccurrenceId, DayIndex = move.DayIndex,
                SlotIndex = move.SlotIndex, RoomId = move.RoomId
            }])
            .ToList();

        if (ev.Severity == EvaluationSeverity.Forbidden)
        {
            string why = ev.Reasons.Count > 0 ? ev.Reasons[0] : "Действие запрещено.";
            return new EditPreview(false, $"Нельзя: {why}", 0, ev.Reasons, resulting);
        }

        string verdict = ev.DeltaTotal switch
        {
            > 0 => $"Можно, но оценка вырастет на +{ev.DeltaTotal}.",
            < 0 => $"Можно, оценка улучшится на {ev.DeltaTotal}.",
            _ => "Можно без потери качества.",
        };
        return new EditPreview(true, verdict, ev.DeltaTotal, ev.Reasons, resulting);
    }

    public async Task<EditCommitOutcome> CommitAsync(
        Guid academicYearId,
        SchedulingProblem problem,
        EditPreview preview,
        CancellationToken ct = default)
    {
        if (!preview.CanCommit)
            return new EditCommitOutcome(false,
                "Не сохранено: правка запрещена (" +
                (preview.Reasons.Count > 0 ? preview.Reasons[0] : "см. причину") + ").", null);
        var validation = PlacementValidator.Validate(problem, preview.ResultingPlacements);
        if (!validation.IsValid)
            return new EditCommitOutcome(false,
                $"Не сохранено: {validation.HardViolations.Count} жёстких нарушений.", null);
        var (accepted, breakdown) = await acceptor.AcceptAsync(
            academicYearId, problem, preview.ResultingPlacements,
            "Ручная правка", $"edit delta={preview.SoftDelta}", ct);
        return new EditCommitOutcome(true,
            $"Сохранено: версия {accepted.Number}, оценка Soft {breakdown.Total}.", accepted);
    }
}
