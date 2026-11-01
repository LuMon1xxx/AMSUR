namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// Точечный ремонт троек физкультуры (школьное правило №3): двигает по одному
// уроку физры с среднего дня тройки на день без физры так, чтобы валидатор
// не видел ничего, кроме (уменьшающихся) pe-spacing (ученические окна/старты
// и остальные hard — святы). Детерминирован (классы по имени, дни и слоты
// по возрастанию). Sync-половины не трогает. Возвращает размещения (in-place нет).
public static class PeSpacingRepair
{
    // Строгий вариант для финального расчёта: СанПиН РБ п. 94 запрещает
    // физкультуру два дня подряд. Метрика — число соседних пар дней с физрой.
    // Возвращает размещения; вызывающий обязан проверить валидатором.
    public static List<PlacedLesson> RepairNoAdjacent(
        SchedulingProblem problem, IReadOnlyList<PlacedLesson> placements)
    {
        var occById = problem.Occurrences.ToDictionary(o => o.Id);
        var pos = placements.ToDictionary(p => p.OccurrenceId);

        var baseline = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var v in PlacementValidator.Validate(problem, placements).HardViolations)
            baseline[v.Code] = baseline.GetValueOrDefault(v.Code) + 1;

        bool NoNewHards(IReadOnlyList<PlacedLesson> list)
        {
            var after = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var v in PlacementValidator.Validate(problem, list).HardViolations)
            {
                if (v.Code is "sanpin-pe-spacing" or "placement-count") continue;
                after[v.Code] = after.GetValueOrDefault(v.Code) + 1;
            }
            foreach (var (code, n) in after)
                if (n > baseline.GetValueOrDefault(code)) return false;
            return true;
        }
        bool IsPe(Guid occId) =>
            occById.TryGetValue(occId, out var o) && !o.IsExtra &&
            problem.Subjects.TryGetValue(o.SubjectId, out var s) && s.IsPhysicalEducation;

        int Adjacent(IEnumerable<PlacedLesson> list)
        {
            int u = 0;
            foreach (var g in list.Where(p => IsPe(p.OccurrenceId))
                         .GroupBy(p => occById[p.OccurrenceId].ClassId))
            {
                var counts = g.GroupBy(p => p.DayIndex)
                    .ToDictionary(d => d.Key, d => d.Count());
                // Два урока физкультуры в один день — тоже нарушение.
                u += counts.Values.Sum(n => Math.Max(0, n - 1));
                var days = counts.Keys.OrderBy(d => d).ToList();
                for (int i = 1; i < days.Count; i++)
                    if (days[i] == days[i - 1] + 1) u++;
            }
            return u;
        }

        bool HypoOk(Guid occId, int day, int slot, int before, out Guid? roomUsed)
        {
            var old = pos[occId];
            foreach (var room in new Guid?[] { old.RoomId, null })
            {
                var hypo = new PlacedLesson
                {
                    OccurrenceId = occId, DayIndex = day, SlotIndex = slot, RoomId = room
                };
                var list = pos.Values.Where(p => p.OccurrenceId != occId).Concat([hypo]).ToList();
                if (!NoNewHards(list)) continue;
                if (Adjacent(list) < before) { roomUsed = room; return true; }
            }
            roomUsed = null;
            return false;
        }

        for (int iter = 0; iter < pos.Count + 1; iter++)
        {
            int before = Adjacent(pos.Values);
            if (before == 0) break;
            bool moved = false;

            var classes = pos.Values.Where(p => IsPe(p.OccurrenceId))
                .GroupBy(p => occById[p.OccurrenceId].ClassId)
                .Select(g => (ClassId: g.Key,
                    Name: problem.Classes.TryGetValue(g.Key, out var c) ? c.Name : "?",
                    Days: g.Select(p => p.DayIndex).Distinct().OrderBy(d => d).ToList(),
                    Pe: g.ToList()))
                .Where(t =>
                {
                    for (int i = 1; i < t.Days.Count; i++)
                        if (t.Days[i] == t.Days[i - 1] + 1) return true;
                    return false;
                })
                .OrderBy(t => t.Name, StringComparer.Ordinal).ToList();

            foreach (var (classId, _, _, peHere) in classes)
            {
                foreach (var cand in peHere.OrderBy(p => p.DayIndex)
                             .ThenBy(p => occById[p.OccurrenceId].StableKey, StringComparer.Ordinal))
                {
                    var occ = occById[cand.OccurrenceId];
                    if (occ.SyncGroupId.HasValue) continue;
                    if (!problem.AllowedDays.TryGetValue(occ.Id, out var ad)) continue;
                    if (!problem.AllowedSlots.TryGetValue(occ.Id, out var asl)) continue;
                    foreach (int day in ad.OrderBy(d => d))
                    {
                        if (day == cand.DayIndex) continue;
                        foreach (int slot in asl.OrderBy(s => s))
                        {
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

                // Своп с другим уроком класса, если свободных клеток нет.
                foreach (var cand in peHere)
                {
                    var occ = occById[cand.OccurrenceId];
                    if (occ.SyncGroupId.HasValue) continue;
                    if (!problem.AllowedDays.TryGetValue(occ.Id, out var ad)) continue;
                    foreach (int day in ad.OrderBy(d => d))
                    {
                        if (day == cand.DayIndex) continue;
                        var others = pos.Values
                            .Where(p => p.OccurrenceId != cand.OccurrenceId
                                        && occById[p.OccurrenceId].ClassId == classId
                                        && p.DayIndex == day
                                        && !occById[p.OccurrenceId].SyncGroupId.HasValue
                                        && !occById[p.OccurrenceId].IsExtra)
                            .OrderBy(p => p.SlotIndex)
                            .Take(12)
                            .ToList();
                        foreach (var other in others)
                        {
                            var otherOcc = occById[other.OccurrenceId];
                            if (!problem.AllowedDays.TryGetValue(otherOcc.Id, out var od)
                                || !od.Contains(cand.DayIndex)) continue;
                            if (!problem.AllowedSlots.TryGetValue(otherOcc.Id, out var osl)
                                || !osl.Contains(cand.SlotIndex)) continue;
                            if (!problem.AllowedSlots.TryGetValue(occ.Id, out var psl)
                                || !psl.Contains(other.SlotIndex)) continue;

                            var newPe = new PlacedLesson
                            {
                                OccurrenceId = cand.OccurrenceId, DayIndex = day,
                                SlotIndex = other.SlotIndex, RoomId = other.RoomId,
                            };
                            var newOther = new PlacedLesson
                            {
                                OccurrenceId = other.OccurrenceId, DayIndex = cand.DayIndex,
                                SlotIndex = cand.SlotIndex, RoomId = cand.RoomId,
                            };
                            var list = pos.Values
                                .Where(p => p.OccurrenceId != cand.OccurrenceId
                                            && p.OccurrenceId != other.OccurrenceId)
                                .Concat([newPe, newOther]).ToList();
                            if (!NoNewHards(list)) continue;
                            if (Adjacent(list) >= before) continue;
                            pos[cand.OccurrenceId] = newPe;
                            pos[other.OccurrenceId] = newOther;
                            moved = true;
                            break;
                        }
                        if (moved) break;
                    }
                    if (moved) break;
                }
            }
            if (!moved) break;
        }
        return pos.Values.ToList();
    }

    public static List<PlacedLesson> Repair(
        SchedulingProblem problem, IReadOnlyList<PlacedLesson> placements)
    {
        var occById = problem.Occurrences.ToDictionary(o => o.Id);
        var pos = placements.ToDictionary(p => p.OccurrenceId);

        // Базовая линия нарушений: на черновике уже есть окна и перегрузки, поэтому
        // «чисто, кроме pe-spacing» недостижимо в принципе, и ремонт не двигал ничего.
        // Правильный критерий — не ДОБАВИТЬ новых нарушений (сверх исходных).
        var baseline = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var v in PlacementValidator.Validate(problem, placements).HardViolations)
            baseline[v.Code] = baseline.GetValueOrDefault(v.Code) + 1;

        bool NoNewHards(IReadOnlyList<PlacedLesson> list)
        {
            var after = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var v in PlacementValidator.Validate(problem, list).HardViolations)
            {
                if (v.Code is "sanpin-pe-spacing" or "placement-count") continue;
                after[v.Code] = after.GetValueOrDefault(v.Code) + 1;
            }
            foreach (var (code, n) in after)
                if (n > baseline.GetValueOrDefault(code)) return false;
            return true;
        }
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
                // Не добавляем нарушений сверх исходных (окна/перегрузки уже могут быть).
                if (!NoNewHards(list)) continue;
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

                // Своп (плотная сетка): свободных ячеек у класса нет, поэтому
                // «переезд» не проходит. Меняем физру местами с другим уроком
                // того же класса — сетка остаётся полной, коллизий класса нет.
                foreach (var cand in peHere)
                {
                    var occ = occById[cand.OccurrenceId];
                    if (occ.SyncGroupId.HasValue) continue;
                    if (!problem.AllowedDays.TryGetValue(occ.Id, out var ad)) continue;
                    foreach (int day in ad.OrderBy(d => d))
                    {
                        if (day == cand.DayIndex) continue;
                        var others = pos.Values
                            .Where(p => p.OccurrenceId != cand.OccurrenceId
                                        && occById[p.OccurrenceId].ClassId == classId
                                        && p.DayIndex == day
                                        && !occById[p.OccurrenceId].SyncGroupId.HasValue
                                        && !occById[p.OccurrenceId].IsExtra)
                            .OrderBy(p => p.SlotIndex)
                            .Take(12)
                            .ToList();
                        foreach (var other in others)
                        {
                            var otherOcc = occById[other.OccurrenceId];
                            if (!problem.AllowedDays.TryGetValue(otherOcc.Id, out var od)
                                || !od.Contains(cand.DayIndex)) continue;
                            if (!problem.AllowedSlots.TryGetValue(otherOcc.Id, out var osl)
                                || !osl.Contains(cand.SlotIndex)) continue;
                            if (!problem.AllowedSlots.TryGetValue(occ.Id, out var psl)
                                || !psl.Contains(other.SlotIndex)) continue;

                            var newPe = new PlacedLesson
                            {
                                OccurrenceId = cand.OccurrenceId, DayIndex = day,
                                SlotIndex = other.SlotIndex, RoomId = other.RoomId,
                            };
                            var newOther = new PlacedLesson
                            {
                                OccurrenceId = other.OccurrenceId, DayIndex = cand.DayIndex,
                                SlotIndex = cand.SlotIndex, RoomId = cand.RoomId,
                            };
                            var list = pos.Values
                                .Where(p => p.OccurrenceId != cand.OccurrenceId
                                            && p.OccurrenceId != other.OccurrenceId)
                                .Concat([newPe, newOther]).ToList();
                            if (!NoNewHards(list)) continue;
                            int after = 0;
                            foreach (var g in list.Where(p => IsPe(p.OccurrenceId))
                                         .GroupBy(p => occById[p.OccurrenceId].ClassId))
                                after += SoftUnits.PeRuns(g.Select(p => p.DayIndex).ToList());
                            if (after >= before) continue;
                            pos[cand.OccurrenceId] = newPe;
                            pos[other.OccurrenceId] = newOther;
                            moved = true;
                            break;
                        }
                        if (moved) break;
                    }
                    if (moved) break;
                }
            }
            if (!moved) break;
        }
        return pos.Values.ToList();
    }
}
