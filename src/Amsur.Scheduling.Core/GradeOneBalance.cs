namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// Балансировка 1-х классов (SANPIN_RB.md §3, D-28): 21 ч / 5 дней с кэпом 5
// вынуждает ровно один 5-урочный день (5,4,4,4,4). Greedy/LS его не держат
// (нет soft-градиента), поэтому доводим пост-проходом: переносим КРАЙНИЙ урок
// избыточного 5-дня в append-слот дня-приёмника (<4 уроков).
// Компактность сохраняется по построению (убираем с конца/начала у якоря,
// дописываем в конец блока). Детерминировано. Честно частична (Notes).
public static class GradeOneBalance
{
    public sealed record BalanceResult(
        List<PlacedLesson> Placements, int Moved, IReadOnlyList<string> Notes);

    public static BalanceResult Balance(
        SchedulingProblem problem, IReadOnlyList<PlacedLesson> placements)
    {
        var occById = problem.Occurrences.ToDictionary(o => o.Id);
        var pos = placements.ToDictionary(p => p.OccurrenceId);
        var notes = new List<string>();
        int moved = 0;

        foreach (var cls in problem.Classes.Values
                     .Where(c => c.Grade == 1)
                     .OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            int guard = 0;
            var reasons = new List<string>();
            while (FiveDays(occById, pos, cls.Id) > 1 && guard++ < 8)
            {
                if (!TryRelieve(problem, occById, pos, cls.Id, reasons))
                {
                    notes.Add($"Класс {cls.Name}: не удалось свести 5-урочные дни к одному " +
                        $"(осталось {FiveDays(occById, pos, cls.Id)}): " +
                        string.Join(" | ", reasons.Take(3)));
                    break;
                }
                moved++;
            }
        }
        return new BalanceResult(pos.Values.ToList(), moved, notes);
    }

    private static int FiveDays(
        Dictionary<Guid, LessonOccurrence> occById,
        Dictionary<Guid, PlacedLesson> pos, Guid classId)
    {
        int n = 0;
        foreach (var day in pos.Values
                     .Where(p => occById[p.OccurrenceId].ClassId == classId)
                     .GroupBy(p => p.DayIndex))
            if (day.Select(p => p.SlotIndex).Distinct().Count() == 5) n++;
        return n;
    }

    private static bool TryRelieve(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        Dictionary<Guid, PlacedLesson> pos, Guid classId, List<string> reasons)
    {
        int anchor = StudentCompactness.AnchorFor(problem, classId);
        var days = pos.Values
            .Where(p => occById[p.OccurrenceId].ClassId == classId)
            .GroupBy(p => p.DayIndex)
            .OrderBy(g => g.Key)
            .ToList();
        // Источники/приёмники: все 5-дни × все дни с <4 distinct-слотами.
        var sources = days.Where(g => g.Select(p => p.SlotIndex).Distinct().Count() == 5).ToList();
        var receivers = days.Where(g => g.Select(p => p.SlotIndex).Distinct().Count() < 4).ToList();
        if (sources.Count == 0 || receivers.Count == 0)
        {
            reasons.Add(receivers.Count == 0 ? "нет дня-приёмника (<4 уроков)" : "нет 5-дня");
            return false;
        }

        foreach (var src in sources)
        {
            var srcSlots = src.Select(p => p.SlotIndex).OrderBy(s => s).ToList();
            // Кандидаты на перенос: последний урок блока, затем первый (если блок от якоря).
            var ordered = src.OrderByDescending(p => p.SlotIndex).ToList();
            if (srcSlots[0] == anchor)
                ordered = ordered.Concat(src.OrderBy(p => p.SlotIndex).Take(1)).ToList();
            foreach (var dst in receivers)
            {
                var dstSlots = dst.Select(p => p.SlotIndex).OrderBy(s => s).ToList();
                int append = dstSlots.Count == 0 ? anchor : dstSlots[^1] + 1;
                foreach (var cand in ordered)
                {
                    var o = occById[cand.OccurrenceId];
                    // Источник остаётся компактным: убираем край (конец — всегда; начало —
                    // только если новый первый ≤ anchor+1).
                    var rest = srcSlots.Where(s => s != cand.SlotIndex).OrderBy(s => s).ToList();
                    if (rest.Count > 0)
                    {
                        if (StudentCompactness.GapOf(rest) != 0) continue;
                        if (StudentCompactness.LateExcess(rest, anchor) != 0) continue;
                    }
                    string why;
                    if (TryMoveTo(problem, occById, pos, o, cand, dst.Key, append, out why))
                        return true;
                    if (reasons.Count < 12)
                        reasons.Add($"{Subj(problem, o)}→день{dst.Key + 1}ур{append}: {why}");
                }
            }
        }
        return false;
    }

    private static string Subj(SchedulingProblem problem, LessonOccurrence o) =>
        problem.Subjects.TryGetValue(o.SubjectId, out var s) ? s.Name : "?";

    private static bool TryMoveTo(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        Dictionary<Guid, PlacedLesson> pos,
        LessonOccurrence occ, PlacedLesson cand, int dstDay, int append, out string why)
    {
        why = "";
        if (problem.AllowedDays.TryGetValue(occ.Id, out var days) && !days.Contains(dstDay))
        { why = "день вне домена"; return false; }
        if (problem.AllowedSlots.TryGetValue(occ.Id, out var slots) && !slots.Contains(append))
        { why = "слот вне домена"; return false; }
        // Коллизии приёмника (без двигаемого урока — он с другого дня).
        foreach (var p in pos.Values)
        {
            if (p.OccurrenceId == occ.Id) continue;
            if (p.DayIndex != dstDay || p.SlotIndex != append) continue;
            var q = occById[p.OccurrenceId];
            if (q.TeacherId == occ.TeacherId) { why = $"учитель занят ({TName(problem, q.TeacherId)})"; return false; }
            if ((q.GroupId ?? q.ClassId) == (occ.GroupId ?? occ.ClassId)) { why = "группа занята"; return false; }
            if (q.ClassId == occ.ClassId) { why = "класс занят"; return false; } // whole/sub одного класса
        }
        // Кабинет: свой если свободен, иначе любой свободный (по имени, детерминировано).
        Guid? room = PickFreeRoom(problem, occById, pos, occ, dstDay, append, cand.RoomId, out why);
        if (why is not null) return false;
        // Дневной лимит учителя.
        if (problem.Teachers.TryGetValue(occ.TeacherId, out var t))
        {
            int count = pos.Values.Count(p => p.OccurrenceId != occ.Id &&
                occById.TryGetValue(p.OccurrenceId, out var q) &&
                q.TeacherId == occ.TeacherId && p.DayIndex == dstDay) + 1;
            if (count > t.MaxLessonsPerDay) { why = $"лимит учителя {count}>{t.MaxLessonsPerDay}"; return false; }
        }
        // Sync-пары в 1-х классах отсутствуют (сплитов нет), одиночный перенос безопасен.
        if (occ.SyncGroupId.HasValue) { why = "sync-пара"; return false; }
        pos[occ.Id] = new PlacedLesson
        {
            OccurrenceId = occ.Id, DayIndex = dstDay, SlotIndex = append, RoomId = room
        };
        return true;
    }

    // why == null ⇔ комната найдена (null room допустим только без кабинетов в задаче).
    private static Guid? PickFreeRoom(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        Dictionary<Guid, PlacedLesson> pos,
        LessonOccurrence occ, int dstDay, int append, Guid? prefer, out string? why)
    {
        why = null;
        if (problem.Rooms.Count == 0) return null;
        int students = problem.Classes.TryGetValue(occ.ClassId, out var cls) ? cls.StudentCount : 0;
        var ordered = problem.Rooms.Values.OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
        if (prefer.HasValue)
            ordered = ordered.OrderByDescending(r => r.Id == prefer.Value).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();
        foreach (var room in ordered)
        {
            if (problem.RoomCaps.TryGetValue((room.Id, occ.SubjectId), out var cap) &&
                cap == RoomCapabilityKind.Forbidden)
                continue;
            if (room.PhysicalCapacity < students) continue;
            int concurrent = pos.Values.Count(x =>
                x.RoomId == room.Id && x.DayIndex == dstDay && x.SlotIndex == append);
            if (concurrent + 1 <= Math.Max(1, room.MaxSimultaneousGroups))
                return room.Id;
        }
        why = "все кабинеты заняты";
        return null;
    }

    private static string TName(SchedulingProblem problem, Guid teacherId) =>
        problem.Teachers.TryGetValue(teacherId, out var t) ? t.Name : "?";
}
