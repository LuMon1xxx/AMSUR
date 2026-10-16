namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// R1/R5: единая политика кабинетов (P2). До P2 фильтры Forbidden+seats были
// продублированы в 4 местах (OrTools/Greedy/CompactRepair/GradeOneBalance) с
// риском расхождения; теперь все идут сюда. Поведение без конфигов — как раньше.
public static class RoomPolicy
{
    /// <summary>
    /// Может ли solver сам ставить occurrence в кабинет (кандидат).
    /// Ручное назначение (ManualEdit/Preview) идёт мимо — IsManualOnly его не блокирует,
    /// ONLY-чужой блокируется и вручную (валидатор, hard).
    /// Вместимость по местам НЕ проверяется (решение пользователя 16.10.2026, D-68):
    /// класс может идти в любой кабинет, разрешённый предмету; поле «учеников в классе»
    /// остаётся справочным (выгрузки/интерфейс) и на выбор кабинета не влияет.
    /// </summary>
    public static bool IsCandidate(SchedulingProblem problem, Room room, LessonOccurrence occ)
    {
        if (room.IsManualOnly) return false;
        if (room.OnlySubjectId.HasValue && room.OnlySubjectId.Value != occ.SubjectId) return false;
        if (problem.RoomCaps.TryGetValue((room.Id, occ.SubjectId), out var cap) &&
            cap == RoomCapabilityKind.Forbidden)
            return false;
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
