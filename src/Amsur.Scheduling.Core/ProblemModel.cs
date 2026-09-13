namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// Чистая постановка задачи для solver/validator (без OR-Tools).

/// <summary>Смена как диапазон слотов (1-based, включительно). Бэнды задаёт вход.</summary>
public sealed record ShiftBand(int FromSlot, int ToSlot);

public sealed class SchedulingProblem
{
    public List<LessonOccurrence> Occurrences { get; init; } = [];
    public Dictionary<Guid, SchoolClass> Classes { get; init; } = [];
    public Dictionary<Guid, Teacher> Teachers { get; init; } = [];
    public Dictionary<Guid, Room> Rooms { get; init; } = [];
    public Dictionary<Guid, Subject> Subjects { get; init; } = [];
    public Dictionary<Guid, List<int>> AllowedDays { get; init; } = [];   // occId -> дни
    public Dictionary<Guid, List<int>> AllowedSlots { get; init; } = [];  // occId -> слоты
    public Dictionary<Guid, Guid> GroupParents { get; init; } = [];       // groupId -> classId
    public Dictionary<(Guid RoomId, Guid SubjectId), Amsur.Domain.RoomCapabilityKind> RoomCaps { get; init; } = [];
    /// <summary>E3 perturbation: occId → запрещённые глобальные времена (day*SP+slot).</summary>
    public Dictionary<Guid, HashSet<int>> BannedTimes { get; init; } = [];
    public List<LessonRelation> Relations { get; init; } = [];
    public int DaysCount { get; init; } = 5;
    public int SlotsPerDay { get; init; } = 7;
    /// <summary>
    /// Смены для split teacher-gap (S5): пусто/одна полоса = односменка (старое поведение).
    /// Дефолт ставит ProblemBuilder (SP==14 → [(1,7),(8,14)]).
    /// </summary>
    public List<ShiftBand> ShiftBands { get; init; } = [];
    public SolverOptions Options { get; init; } = new();
}

public sealed class PlacedLesson
{
    public Guid OccurrenceId { get; init; }
    public int DayIndex { get; init; }
    public int SlotIndex { get; init; }
    public Guid? RoomId { get; init; }
}

public sealed class ValidationIssue
{
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
    public Guid? OccurrenceId { get; init; }
    public Guid? TeacherId { get; init; }
    public Guid? ClassId { get; init; }
    public Guid? RoomId { get; init; }
}

public sealed class ValidationResult
{
    public List<ValidationIssue> HardViolations { get; init; } = [];
    public List<ValidationIssue> Warnings { get; init; } = [];
    public bool IsValid => HardViolations.Count == 0;
}

public sealed class PenaltyComponent
{
    public string Code { get; init; } = "";
    public long Value { get; init; }
}

public sealed class PenaltyBreakdown
{
    public long Total { get; init; }
    public List<PenaltyComponent> Components { get; init; } = [];
}
