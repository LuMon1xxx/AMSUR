using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// Добивка остатков (04.10.2026): ни один из невмещённых не влезает
// ни в одну клетку без нарушения валидности (проверено полным перебором
// клеток за секунды). Остатки — структурные (профильные пары без синхрона,
// сплиты, капы), лечатся только перестановкой placed (подборщик/LNS) или
// ослаблением капов. Тест фиксирует факт; если движок начнёт впихивать сам —
// упадёт, и это будет хорошая новость (обновить число осознанно).
// 05.10.2026: было 20, стало 21 — фикс капа классного часа (час кап не тратит)
// поменял порядок упаковки жадника; fitted=0 держится (структурность intact).
public sealed class LeftoverProbeTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void LeftoverProbe()
    {
        var p = FinalSchoolRunTests.BuildFinalProblem();
        var greedy = GreedyPlacer.Place(p, 7);
        var start = greedy.Placed.Select(kv => new PlacedLesson
        {
            OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
            SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
        }).ToList();
        var occById = p.Occurrences.ToDictionary(o => o.Id);
        var placedIds = start.Select(x => x.OccurrenceId).ToHashSet();
        var unplaced = p.Occurrences.Where(o => !placedIds.Contains(o.Id))
            .OrderBy(o => o.StableKey, StringComparer.Ordinal).ToList();
        output.WriteLine($"LEFTOVER: unplaced={unplaced.Count}");
        var pos = start.ToDictionary(x => x.OccurrenceId);
        int fitted = 0;
        var still = new List<string>();
        foreach (var o in unplaced)
        {
            bool done = false;
            var days = p.AllowedDays.TryGetValue(o.Id, out var dd) ? dd : Enumerable.Range(0, p.DaysCount).ToList();
            var slots = p.AllowedSlots.TryGetValue(o.Id, out var ss) ? ss : Enumerable.Range(1, p.SlotsPerDay).ToList();
            foreach (int day in days.OrderBy(d => d))
            {
                foreach (int slot in slots.OrderBy(s => s))
                {
                    var hypo = new PlacedLesson { OccurrenceId = o.Id, DayIndex = day, SlotIndex = slot };
                    var list = pos.Values.Concat([hypo]).ToList();
                    var vr = PlacementValidator.Validate(p, list);
                    if (vr.HardViolations.Count == 0)
                    {
                        pos[o.Id] = hypo;
                        fitted++;
                        done = true;
                        break;
                    }
                }
                if (done) break;
            }
            if (!done)
                still.Add($"{p.Classes[o.ClassId].Name} {p.Subjects[o.SubjectId].Name} " +
                    $"{p.Teachers[o.TeacherId].Name}");
        }
        output.WriteLine($"LEFTOVER: fitted={fitted} still={still.Count}");
        foreach (var s in still.Take(25))
            output.WriteLine("LEFTOVER stuck: " + s);
        // Структурный факт: простой добивкой не лечится (см. шапку).
        Assert.Equal(0, fitted);
        Assert.Equal(21, still.Count);
    }
}
