namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// Точечный ремонт троек физкультуры (школьное правило №3): двигает по одному
// уроку физры с среднего дня тройки на день без физры так, чтобы валидатор
// не видел ничего, кроме (уменьшающихся) pe-spacing (ученические окна/старты
// и остальные hard — святы). Детерминирован (классы по имени, дни и слоты
// по возрастанию). Sync-половины не трогает. Возвращает размещения (in-place нет).
public static class PeSpacingRepair
{
    public static List<PlacedLesson> Repair(
        SchedulingProblem problem, IReadOnlyList<PlacedLesson> placements)
    {
        var occById = problem.Occurrences.ToDictionary(o => o.Id);
        var pos = placements.ToDictionary(p => p.OccurrenceId);
        bool IsPe(Guid occId) =>
            occById.TryGetValue(occId, out var o) && !o.IsExtra &&
            problem.Subjects.TryGetValue(o.SubjectId, out var s) && s.IsPhysicalEducation;

        int Units()
        {
            int u = 0;
            foreach (var g in pos.Values.Where(p => IsPe(p.OccurrenceId))
                             .GroupBy(p => occById[p.OccurrenceId].ClassId))
                u += SoftUnits.PeRuns(g.Select(p => p.DayIndex).ToList());
            return u;
        }

        bool HypoOk(Guid occId, int day, int slot, int unitsBefore, out Guid? roomUsed)
        {
            var old = pos[occId];
            // Сначала свой кабинет, затем без кабинета (зал забит — завуч назначит).
            foreach (var room in new Guid?[] { old.RoomId, null })
            {
                var hypo = new PlacedLesson
                {
                    OccurrenceId = occId, DayIndex = day, SlotIndex = slot, RoomId = room
                };
                var list = pos.Values.Where(p => p.OccurrenceId != occId).Concat([hypo]).ToList();
                var vr = PlacementValidator.Validate(problem, list);
                // Свято всё, кроме чинимого pe-spacing (и placement-count — счёт тот же).
                bool clean = true;
                foreach (var v in vr.HardViolations)
                    if (v.Code is not ("sanpin-pe-spacing" or "placement-count"))
                    { clean = false; break; }
                if (!clean) continue;
                int after = 0;
                foreach (var g in list.Where(p => IsPe(p.OccurrenceId))
                                 .GroupBy(p => occById[p.OccurrenceId].ClassId))
                    after += SoftUnits.PeRuns(g.Select(p => p.DayIndex).ToList());
                if (after < unitsBefore) { roomUsed = room; return true; }
            }
            roomUsed = null;
            return false;
        }

        for (int iter = 0; iter < pos.Count + 1; iter++)
        {
            int before = Units();
            if (before == 0) break;
            bool moved = false;
            var classes = pos.Values.Where(p => IsPe(p.OccurrenceId))
                .GroupBy(p => occById[p.OccurrenceId].ClassId)
                .Select(g => (ClassId: g.Key,
                    Name: problem.Classes.TryGetValue(g.Key, out var c) ? c.Name : "?",
                    Days: g.Select(p => p.DayIndex).Distinct().OrderBy(d => d).ToList()))
                .Where(t => SoftUnits.PeRuns(t.Days) > 0)
                .OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
            foreach (var (classId, _, days) in classes)
            {
                // Дни-кандидаты тройки: средний день каждой тройки первым.
                var midDays = new List<int>();
                for (int i = 2; i < days.Count; i++)
                    if (days[i] == days[i - 1] + 1 && days[i - 1] == days[i - 2] + 1)
                        midDays.Add(days[i - 1]);
                var peHere = pos.Values
                    .Where(p => IsPe(p.OccurrenceId) && occById[p.OccurrenceId].ClassId == classId)
                    .OrderBy(p => midDays.Contains(p.DayIndex) ? 0 : 1)
                    .ThenBy(p => p.DayIndex)
                    .ThenBy(p => occById[p.OccurrenceId].StableKey, StringComparer.Ordinal)
                    .ToList();
                foreach (var cand in peHere)
                {
                    var occ = occById[cand.OccurrenceId];
                    if (occ.SyncGroupId.HasValue) continue;
                    if (!problem.AllowedDays.TryGetValue(occ.Id, out var ad)) continue;
                    if (!problem.AllowedSlots.TryGetValue(occ.Id, out var asl)) continue;
                    // Все дни (не только без физры): глобальный пересчёт в HypoOk
                    // принимает только чистое уменьшение троек.
                    foreach (int day in ad.OrderBy(d => d))
                    {
                        if (day == cand.DayIndex) continue;
                        foreach (int slot in asl.OrderBy(s => s))
                        {
                            if (day == cand.DayIndex && slot == cand.SlotIndex) continue;
                            if (HypoOk(cand.OccurrenceId, day, slot, before, out var roomUsed))
                            {
                                pos[cand.OccurrenceId] = new PlacedLesson
                                {
                                    OccurrenceId = cand.OccurrenceId,
                                    DayIndex = day, SlotIndex = slot, RoomId = roomUsed
                                };
                                moved = true;
                                break;
                            }
                        }
                        if (moved) break;
                    }
                    if (moved) break;
                }
                if (moved) break;
            }
            if (!moved) break;
        }
        return pos.Values.ToList();
    }
}
