namespace Amsur.Domain;

public enum RuleSeverity { Hard, Soft, Disabled }
public enum RuleScope { Global, Class, Teacher, Subject, Room }
public enum LessonRelationKind
{
    SameDay, DifferentDay, Adjacent, NotAdjacent, Before, After,
    SameSlot, DifferentSlot, PreferSameDay, PreferDifferentDay, MaxPerDay
}
public enum RoomCapabilityKind { Universal, Preferred, Required, Forbidden }
public enum AvailabilityKind { Preferred, Neutral, Avoid, Forbidden }

// Статусы solver (D-10: новый enum НЕ вводим; маппинг — в Application through helper).
public enum SolverStatus { Unknown, Optimal, Feasible, Infeasible, ModelInvalid }

// Пользовательские статусы (хелпер ToUserStatus, не отдельный контракт solver).
public enum UserScheduleStatus
{
    Feasible,
    InfeasibleConfirmedByModel,
    NoSolutionFoundWithinLimit,
    CancelledAfterFeasible,
    CancelledWithoutFeasible,
    ValidatorRejected
}
