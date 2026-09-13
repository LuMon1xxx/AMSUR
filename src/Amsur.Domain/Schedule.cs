namespace Amsur.Domain;

// Расписание / версии / правила (порт V1: Schedule.cs, Stability.cs, Rules.cs).
public sealed class Schedule : Entity
{
    public Guid AcademicYearId { get; set; }
    public bool IsActive { get; set; }   // D-03: производное; truth = ScheduleVersion.IsActive
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class ScheduledPlacement : Entity
{
    public Guid ScheduleId { get; set; }
    public Guid OccurrenceId { get; set; }
    public int DayIndex { get; set; }
    public int SlotIndex { get; set; }
    public Guid? RoomId { get; set; }
}

// D-03 кандидат: Version.IsActive — single source of truth; rollback только новой версией.
public sealed class ScheduleVersion : Entity
{
    public Guid ScheduleId { get; set; }
    public int Number { get; set; }
    public bool IsActive { get; set; }
    public string Reason { get; set; } = "";
    public string SolverSettingsJson { get; set; } = "{}"; // time/workers/seed/algorithm
    public string RuleSetVersion { get; set; } = "1";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// Volatile-кандидат Top-K живёт в Scheduling.Core (ScheduleCandidate + Archive, E1);
// здесь оставлен только persist-контур (Schedule/ScheduledPlacement/ScheduleVersion).

public sealed class ConstraintDefinition : Entity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public RuleSeverity Severity { get; set; } = RuleSeverity.Soft;
    public bool HardCapable { get; set; }
    public long Weight { get; set; }
    public string ParametersJson { get; set; } = "{}";
}

public sealed class RuleSetVersion : Entity
{
    public int Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Note { get; set; } = "";
}

// Физические коды — барьер Hard (D-04): нельзя перевести в Soft из UI.
public static class PhysicalRuleCodes
{
    public const string TeacherCollision = "teacher-collision";
    public const string GroupCollision = "group-collision";
    public const string RoomOverflow = "room-overflow";
    public const string SubgroupSync = "subgroup-sync";
    public const string ShiftDomain = "shift-domain";

    public static bool IsPhysical(string code) => code is
        TeacherCollision or GroupCollision or RoomOverflow or SubgroupSync or ShiftDomain;

    public static void EnsureSeverity(string code, RuleSeverity severity)
    {
        if (IsPhysical(code) && severity != RuleSeverity.Hard)
            throw new InvalidOperationException($"Physical rule '{code}' must stay Hard (D-04).");
    }
}
