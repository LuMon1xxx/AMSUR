using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// Временная витрина: расписание идеальной 5–11 в Excel + статистика окон.
// Удалить после показа (артефакт остаётся в Samples_Export).
public sealed class OurIdealShowcaseTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void Showcase_ExportAndStats()
    {
        var data = SchoolDataImporter.Import(Guid.NewGuid(), OurIdealSchoolTests.Rows(), slotsPerDay: 12);
        var (p, errors) = ProblemBuilder.Build(data.ToProblemInput());
        Assert.NotNull(p);
        Assert.Empty(errors);
        var problem = p!;
        var greedy = GreedyPlacer.Place(problem);
        Assert.Empty(greedy.Unplaced);
        var start = greedy.Placed.Select(kv => new PlacedLesson
        {
            OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
            SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
        }).ToList();
        var repaired = CompactRepair.Repair(problem, start);
        var placements = repaired.Placements;
        var vr = PlacementValidator.Validate(problem, placements);
        Assert.True(vr.IsValid);

        var occById = problem.Occurrences.ToDictionary(o => o.Id);
        string TName(Guid id) => problem.Teachers[id].Name;
        string CName(Guid id) => problem.Classes[id].Name;
        string SName(Guid id) => problem.Subjects[id].Name;

        // Учителя: окна = пустые слоты между первым и последним уроком дня.
        int teacherDays = 0, teacherWindows = 0, daysWithWindows = 0, maxWindows = 0;
        string maxOwner = "";
        foreach (var g in placements.GroupBy(x => occById[x.OccurrenceId].TeacherId))
        {
            foreach (var day in g.GroupBy(x => x.DayIndex))
            {
                var slots = day.Select(x => x.SlotIndex).Distinct().OrderBy(s => s).ToList();
                teacherDays++;
                int w = slots.Count <= 1 ? 0 : (slots[^1] - slots[0] + 1) - slots.Count;
                teacherWindows += w;
                if (w > 0) daysWithWindows++;
                if (w > maxWindows) { maxWindows = w; maxOwner = $"{TName(g.Key)} день {day.Key + 1}"; }
            }
        }
        // Ученики: окна и поздние старты.
        int pupilWindows = 0, pupilLate = 0;
        foreach (var g in placements.GroupBy(x => occById[x.OccurrenceId].ClassId))
        {
            int anchor = StudentCompactness.AnchorFor(problem, g.Key);
            foreach (var day in g.GroupBy(x => x.DayIndex))
            {
                var slots = day.Select(x => x.SlotIndex).Distinct().OrderBy(s => s).ToList();
                if (slots.Count > 1)
                    pupilWindows += (slots[^1] - slots[0] + 1) - slots.Count;
                pupilLate += StudentCompactness.LateExcess(slots, anchor);
            }
        }
        output.WriteLine($"SHOWCASE: teachers={problem.Teachers.Count} teacherDays={teacherDays} " +
            $"teacherWindows={teacherWindows} daysWithWindows={daysWithWindows} max={maxWindows} ({maxOwner}) " +
            $"per100={100.0 * teacherWindows / placements.Count:F1}");
        output.WriteLine($"SHOWCASE: pupilWindows={pupilWindows} pupilLate={pupilLate} lessons={placements.Count}");
        // Раскладка по классам: день -> число уроков.
        foreach (var g in placements.GroupBy(x => occById[x.OccurrenceId].ClassId)
                     .OrderBy(g => CName(g.Key)))
        {
            var days = g.GroupBy(x => x.DayIndex).OrderBy(d => d.Key)
                .Select(d => $"Д{d.Key + 1}:{d.Select(x => x.SlotIndex).Distinct().Count()}");
            output.WriteLine($"SHOWCASE: {CName(g.Key)}: {string.Join(" ", days)}");
        }
        // Плотность учителей: топ нагрузок.
        foreach (var g in placements.GroupBy(x => occById[x.OccurrenceId].TeacherId)
                     .OrderByDescending(g => g.Count()).Take(5))
            output.WriteLine($"SHOWCASE: {TName(g.Key)}: {g.Count()} ч/нед, дней {g.Select(x => x.DayIndex).Distinct().Count()}");

        var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Samples_Export");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "OurIdeal_5-11.xlsx");
        using (var fs = new FileStream(path, FileMode.Create))
            ScheduleExcelExporter.ExportGrid(problem, placements, fs);
        output.WriteLine($"SHOWCASE: xlsx={path}");
    }
}
