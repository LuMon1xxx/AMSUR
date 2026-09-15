namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// R1/R5: единая политика кабинетов (P2). До P2 фильтры Forbidden+seats были
// продублированы в 4 местах (OrTools/Greedy/CompactRepair/GradeOneBalance) с
// риском расхождения; теперь все идут сюда. Поведение без конфигов — как раньше.
public static class RoomPolicy
{
    /// <summary>Сколько мест нужно occurrence: весь класс или половина (подгруппа).</summary>
    public static int NeedSeats(SchedulingProblem problem, LessonOccurrence occ)
    {
        int students = problem.Classes.TryGetValue(occ.ClassId, out var cls) ? cls.StudentCount : 0;
        return occ.GroupId.HasValue ? (students + 1) / 2 : students;
    }

    /// <summary>
    /// Может ли solver сам ставить occurrence в кабинет (кандидат).
    /// Ручное назначение (ManualEdit/Preview) идёт мимо — IsManualOnly его не блокирует,
    /// ONLY-чужой блокируется и вручную (валидатор, hard).
    /// </summary>
    public static bool IsCandidate(SchedulingProblem problem, Room room, LessonOccurrence occ)
    {
        if (room.IsManualOnly) return false;
        if (room.OnlySubjectId.HasValue && room.OnlySubjectId.Value != occ.SubjectId) return false;
        if (problem.RoomCaps.TryGetValue((room.Id, occ.SubjectId), out var cap) &&
            cap == RoomCapabilityKind.Forbidden)
            return false;
        if (room.PhysicalCapacity < NeedSeats(problem, occ)) return false;
        return true;
    }

    /// <summary>
    /// Действующее «желательно» (R5): 0/не задано → = MaxSimultaneousGroups
    /// (без претензий, как до R1–R9); иначе min(желательно, max).
    /// </summary>
    public static int EffectiveDesired(Room room) =>
        room.DesiredGroups < 1
            ? Math.Max(1, room.MaxSimultaneousGroups)
            : Math.Min(room.DesiredGroups, Math.Max(1, room.MaxSimultaneousGroups));

    /// <summary>
    /// Единицы вместимости клетки (R5): размещения — либо distinct-классы, если
    /// подгруппа НЕ считается отдельной (CountSubgroupAsGroup=false: сплит A/B
    /// одним классом занимает 1 единицу вместо 2).
    /// </summary>
    public static int CellUnits(Room room, int placementCount, int distinctClassCount) =>
        room.CountSubgroupAsGroup ? placementCount : distinctClassCount;

    /// <summary>Переполнение hard-капы в единицах (0 = влезает).</summary>
    public static int OverflowUnits(Room room, int placementCount, int distinctClassCount) =>
        Math.Max(0, CellUnits(room, placementCount, distinctClassCount) -
            Math.Max(1, room.MaxSimultaneousGroups));

    /// <summary>Теснота soft-капы (room-crowding): сверх «желательно».</summary>
    public static int CrowdingUnits(Room room, int placementCount, int distinctClassCount) =>
        Math.Max(0, CellUnits(room, placementCount, distinctClassCount) - EffectiveDesired(room));
}
