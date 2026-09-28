namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// Фаза D (план v2): точечный SanPin-ремонт перегруженных классо-дней.
// Spill юнита (sync — целиком) с перегруженного классо-дня с ЗАПРЕТОМ дня
// через клон AllowedDays (только внутри стадии); Replant тянет в дни со
// слабиной (least-loaded class-day ordering; дни с нагрузкой 4–5 первыми
// среди непустых); приём: перегрузов строго меньше + pupilHard==0 +
// failedDays≤ + дыры≤+2/ход. Детерминирован (сиды), time-boxed.
public static class OverloadLns
{
    public sealed record OverloadResult(
        List<PlacedLesson> Placements, int Overloads, int TeacherGaps,
        int FailedDays, long SoftTotal, int PupilHard, int Iterations, int Accepted);

    public static OverloadResult Improve(
        SchedulingProblem problem,
        IReadOnlyList<PlacedLesson> start,
        TimeSpan budget,
        int seed,
        int cap = 7,
        CancellationToken ct = default,
        EffectiveRuleSet? rules = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var occById = problem.Occurrences.ToDictionary(o => o.Id);
        var rs = rules ?? EffectiveRuleSet.Default;
        var best = start.ToList();
        int bestOver = CountOverloads(problem, occById, best, cap);
        int bestGaps = TeacherDayLns.TeacherGridGaps(occById, best);
        int bestFailed = RuinRecreate.FailedDays(problem, occById, best);
        long bestSoft = SoftEvaluator.Evaluate(problem, best, rs).Total;
        int bestPupil = PupilHard(problem, best);
        int iters = 0, accepted = 0;

        while (sw.Elapsed < budget && !ct.IsCancellationRequested && bestOver > 0)
        {
            var worst = WorstOverloadDays(problem, occById, best, cap, topK: 8);
            if (worst.Count == 0) break;
            bool improved = false;
            foreach (var (cls, day) in worst)
            {
                if (sw.Elapsed >= budget || ct.IsCancellationRequested) break;
                var cellUnits = best
                    .Where(p => occById[p.OccurrenceId].ClassId == cls && p.DayIndex == day)
                    .GroupBy(p => occById[p.OccurrenceId].SyncGroupId ?? p.OccurrenceId)
                    .Select(g => g.Select(p => p.OccurrenceId).ToList())
                    .OrderBy(u => occById[u[0]].StableKey, StringComparer.Ordinal)
                    .ToList();
                foreach (var unit in cellUnits)
                {
                    if (sw.Elapsed >= budget || ct.IsCancellationRequested) break;
                    iters++;
                    // Ruin: юнит (sync целиком через замыкание) + запрет дня.
                    var ruin = new HashSet<Guid>(unit);
                    bool grew;
                    do
                    {
                        grew = false;
                        foreach (var id in ruin.ToList())
                        {
                            var o = occById[id];
                            if (!o.SyncGroupId.HasValue) continue;
                            foreach (var m in problem.Occurrences.Where(x => x.SyncGroupId == o.SyncGroupId))
                                if (ruin.Add(m.Id)) grew = true;
                        }
                    } while (grew);
                    var staged = CloneWithBannedDay(problem, ruin, day);
                    var frozen = best.Where(p => !ruin.Contains(p.OccurrenceId))
                        .ToDictionary(p => p.OccurrenceId, p => (p.DayIndex, p.SlotIndex, p.RoomId));
                    var units = BuildUnits(problem, occById, ruin);
                    var replant = GreedyPlacer.Replant(
                        staged, frozen, units, seed + iters * 131 + accepted * 17);
                    if (replant.Unplaced.Count > 0) continue;
                    var trial = CompactRepair.Repair(staged, replant.Placed
                        .Select(kv => new PlacedLesson
                        {
                            OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
                            SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
                        }).ToList()).Placements;
                    int over = CountOverloads(problem, occById, trial, cap);
                    int pupil = PupilHard(problem, trial);
                    int fd = RuinRecreate.FailedDays(problem, occById, trial);
                    int tg = TeacherDayLns.TeacherGridGaps(occById, trial);
                    long soft = SoftEvaluator.Evaluate(problem, trial, rs).Total;
                    if (over < bestOver && pupil == 0 && fd <= bestFailed &&
                        tg <= bestGaps + 2)
                    {
                        best = trial; bestOver = over; bestGaps = tg;
                        bestFailed = fd; bestSoft = soft; bestPupil = pupil;
                        accepted++; improved = true;
                        break;
                    }
                }
                if (improved) break;
            }
            if (!improved) break;
        }
        sw.Stop();
        return new OverloadResult(best, bestOver, bestGaps, bestFailed, bestSoft, bestPupil, iters, accepted);
    }

    /// <summary>Кэп классо-дня для ремонта: 5–6 кл → cap−1, 7–11 → cap
    /// (аудит-кэпы 6/7 при дефолте cap=7).</summary>
    public static int CapFor(Amsur.Domain.SchoolClass cls, int cap) =>
        cls.Grade <= 6 ? cap - 1 : cap;

    /// <summary>Число перегруженных классо-дней (distinct-слотов &gt; кэпа).</summary>
    public static int CountOverloads(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        IReadOnlyList<PlacedLesson> placements, int cap)
    {
        int n = 0;
        foreach (var g in placements.GroupBy(p => (occById[p.OccurrenceId].ClassId, p.DayIndex)))
        {
            if (!problem.Classes.TryGetValue(g.Key.ClassId, out var cls)) continue;
            int slots = g.Select(p => p.SlotIndex).Distinct().Count();
            if (slots > CapFor(cls, cap)) n++;
        }
        return n;
    }

    /// <summary>Худшие перегруженные классо-дни (по превышению убыв.;
    /// детерминированный тайбрейк).</summary>
    public static List<(Guid Class, int Day)> WorstOverloadDays(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        IReadOnlyList<PlacedLesson> placements, int cap, int topK)
    {
        return placements.GroupBy(p => (occById[p.OccurrenceId].ClassId, p.DayIndex))
            .Select(g =>
            {
                int slots = g.Select(p => p.SlotIndex).Distinct().Count();
                int excess = problem.Classes.TryGetValue(g.Key.ClassId, out var cls)
                    ? slots - CapFor(cls, cap) : int.MinValue;
                return (Class: g.Key.ClassId, Day: g.Key.DayIndex, Excess: excess);
            })
            .Where(x => x.Excess > 0)
            .OrderByDescending(x => x.Excess)
            .ThenBy(x => x.Class)
            .ThenBy(x => x.Day)
            .Take(topK)
            .Select(x => (x.Class, x.Day))
            .ToList();
    }

    /// <summary>Pupil-жёсткость: student-gap + student-late-start нарушения
    /// валидатора (инвариант pupilHard==0).</summary>
    public static int PupilHard(SchedulingProblem problem, IReadOnlyList<PlacedLesson> placements)
    {
        var vr = PlacementValidator.Validate(problem, placements);
        return vr.HardViolations.Count(v =>
            v.Code is "student-gap" or "student-late-start");
    }

    /// <summary>Блокирующие hard (без pupil-кодов и placement-count).</summary>
    public static int BlockingHard(SchedulingProblem problem, IReadOnlyList<PlacedLesson> placements)
    {
        var vr = PlacementValidator.Validate(problem, placements);
        return vr.HardViolations.Count(v =>
            v.Code is not ("student-gap" or "student-late-start" or "placement-count"));
    }

    // Клон задачи с запретом дня для руины (только внутри стадии; исходник цел).
    private static SchedulingProblem CloneWithBannedDay(
        SchedulingProblem problem, HashSet<Guid> ruin, int day)
    {
        var allowed = new Dictionary<Guid, List<int>>();
        foreach (var kv in problem.AllowedDays)
            allowed[kv.Key] = ruin.Contains(kv.Key)
                ? kv.Value.Where(d => d != day).ToList()
                : kv.Value;
        return new SchedulingProblem
        {
            Occurrences = problem.Occurrences,
            Classes = problem.Classes,
            Teachers = problem.Teachers,
            Rooms = problem.Rooms,
            Subjects = problem.Subjects,
            AllowedDays = allowed,
            AllowedSlots = problem.AllowedSlots,
            GroupParents = problem.GroupParents,
            RoomCaps = problem.RoomCaps,
            BannedTimes = problem.BannedTimes,
            Relations = problem.Relations,
            DaysCount = problem.DaysCount,
            SlotsPerDay = problem.SlotsPerDay,
            ShiftBands = problem.ShiftBands,
            Options = problem.Options,
            Flex = problem.Flex,
            Assignments = problem.Assignments,
        };
    }

    private static List<List<Guid>> BuildUnits(
        SchedulingProblem problem,
        Dictionary<Guid, LessonOccurrence> occById,
        HashSet<Guid> ruined)
    {
        var units = new List<List<Guid>>();
        var done = new HashSet<Guid>();
        foreach (var id in ruined.OrderBy(id => occById[id].StableKey, StringComparer.Ordinal))
        {
            if (!done.Add(id)) continue;
            var o = occById[id];
            if (o.SyncGroupId.HasValue)
            {
                var mates = problem.Occurrences
                    .Where(x => x.SyncGroupId == o.SyncGroupId && ruined.Contains(x.Id))
                    .Select(x => x.Id).ToList();
                foreach (var m in mates) done.Add(m);
                units.Add(mates);
            }
            else units.Add([id]);
        }
        return units;
    }
}
