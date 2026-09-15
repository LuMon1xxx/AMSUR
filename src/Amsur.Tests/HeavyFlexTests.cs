using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// P5: тяжёлая школа с включённым flex (ответ на вопрос «тяжёлые тесты»):
// 14 классов (5–11 × А/Б), ~238 часов, нормы + override + классруки + общий урок +
// ONLY-кабинет + спортзал, пайплайн build → greedy → repair → validator clean.
// Без CP-SAT (быстро для сьюта); полный solve покрыт DemoSchool_StandardRun.
public sealed class HeavyFlexTests
{
    private static readonly string[] Subjects =
        ["Математика", "Русский язык", "Литература", "Английский язык",
         "История", "Физкультура", "Биология"];

    private static int HoursOf(string s) => s switch
    {
        "Математика" or "Русский язык" => 3,
        _ => 2,
    };

    [Fact]
    public void HeavySchool_FlexComposed_ValidatorClean()
    {
        var year = Guid.NewGuid();
        var rows = new List<LoadRow>();
        var classRows = new List<ClassConfigRow>();
        // Учителя-предметники общие на параллель (кроме классных).
        for (int g = 5; g <= 11; g++)
        {
            foreach (string ab in new[] { "А", "Б" })
            {
                string cls = $"{g}{ab}";
                string classTeacher = $"Классная {cls}";
                classRows.Add(new ClassConfigRow(cls, classTeacher, g, 25));
                foreach (string s in Subjects)
                {
                    string teacher = s == "Физкультура" ? $"Физрук {g}" : $"Учитель {s} {g}";
                    string? room = s == "Английский язык" ? "Линг"
                        : s == "Физкультура" ? "Спортзал" : $"Каб {cls}";
                    rows.Add(new LoadRow(cls, s, HoursOf(s), teacher, false, null, room));
                }
                // Классный руководитель — только через ClassConfig (ведёт общий урок).
            }
        }

        var flex = FlexDataset.Empty with
        {
            Classes = classRows,
            Rooms = new List<RoomConfigRow>
            {
                // Спортзал: до 4 классов, желательно 2.
                new("Спортзал", false, null, 4, 2, true),
                // Линг — только английский.
                new("Линг", false, "Английский язык", 1, 1, true),
            },
            HourNorms = new List<HourNormRow> { new("Математика", 9, 5) },
            HourOverrides = new List<HourOverrideRow> { new("9Б", "Математика", 7) },
            SubjectDifficulty = new List<SubjectDifficultyRow> { new("Математика", 8) },
            CommonLesson = new CommonLessonRow(true, 3, 1, "5,6,7,8,9,10,11", true),
            Settings = FlexSettingsRow.Default,
        };

        var data = SchoolDataImporter.Import(year, rows, daysCount: 5, slotsPerDay: 7, flex: flex);
        // 14 классов × (3+3+2+2+2+2+2=16) + норма 9-х/override 9Б поверх строк.
        Assert.Equal(14, data.Classes.Count);
        var input = data.ToProblemInput();
        var (problem, errors) = ProblemBuilder.Build(input);
        Assert.Empty(errors);
        Assert.NotNull(problem);
        var p = problem!;

        // R2: 9А — 5ч (норма), 9Б — 7ч (override), 8А — 3ч (строка).
        int MathHours(string cls) => p.Occurrences.Count(o =>
            p.Classes[o.ClassId].Name == cls && p.Subjects[o.SubjectId].Name == "Математика");
        Assert.Equal(5, MathHours("9А"));
        Assert.Equal(7, MathHours("9Б"));
        Assert.Equal(3, MathHours("8А"));

        // R3: 14 общих уроков, один SyncGroup, классруки.
        var commons = p.Occurrences.Where(o => p.Subjects[o.SubjectId].Name == "Классный час").ToList();
        Assert.Equal(14, commons.Count);
        Assert.Single(commons.Select(o => o.SyncGroupId).Distinct());

        // Пайплайн без CP-SAT.
        var greedy = GreedyPlacer.Place(p);
        Assert.Empty(greedy.Unplaced);
        var start = greedy.Placed.Select(kv => new PlacedLesson
        {
            OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
            SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
        }).ToList();
        var repaired = CompactRepair.Repair(p, start);
        var vr = PlacementValidator.Validate(p, repaired.Placements);
        Assert.True(vr.IsValid);

        var byOcc = repaired.Placements.ToDictionary(x => x.OccurrenceId);
        // R3: все в одной клетке (Чт, 1-й).
        foreach (var c in commons)
        {
            Assert.Equal(3, byOcc[c.Id].DayIndex);
            Assert.Equal(1, byOcc[c.Id].SlotIndex);
        }
        // R1: в Линге — только английский (ONLY), чужого нет.
        var lingId = p.Rooms.Values.Single(r => r.Name == "Линг").Id;
        foreach (var x in repaired.Placements.Where(x => x.RoomId == lingId))
            Assert.Equal("Английский язык", p.Subjects[p.Occurrences.Single(o => o.Id == x.OccurrenceId).SubjectId].Name);
        // R5: спортзал не переполнен.
        var gymId = p.Rooms.Values.Single(r => r.Name == "Спортзал").Id;
        foreach (var g in repaired.Placements
                     .Where(x => x.RoomId == gymId)
                     .GroupBy(x => (x.DayIndex, x.SlotIndex)))
            Assert.True(g.Count() <= 4);
        // R7: разрывов нет; R6/R8-коды в оценке присутствуют.
        var bd = SoftEvaluator.Evaluate(p, repaired.Placements, RuleResolver.Resolve("STANDARD"));
        Assert.Equal(0, bd.Components.Single(c => c.Code == "teacher-split").Value);
        Assert.Contains(bd.Components, c => c.Code == "room-crowding");
        Assert.Contains(bd.Components, c => c.Code == "heavy-edge");
    }
}
