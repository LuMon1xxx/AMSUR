using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// Идеальная школа НА ОСНОВЕ НАШЕЙ (окт. 2026, по просьбе): структура СШ № 8
// 5–11 (по одному классу на параллель, реальные часы из SUBJECTS_RB.md §2,
// смены как в школе: 5–8 — 2-я, 9–11 — 1-я), но штат ЗДОРОВЫЙ: на каждый
// (класс, предмет) — свой учитель (нагрузки ≤ 5 ч/нед — никакого русского
// блока 122 ч на троих). Упрощения (честно): без сплитов подгрупп и без
// профильных пар; классный час выключен.
// Смысл: та же учебная масса, что висит в школе, но без кадровой дыры —
// движок обязан покрыть 100% (ср. tight-фикстуру 986/994 в карантине D-35).
public sealed class OurIdealSchoolTests(Xunit.Abstractions.ITestOutputHelper output)
{
    // (предмет, часы) по классам — SUBJECTS_RB.md §2, суммы 29/30/32/33/33/34/34.
    private static readonly (int Grade, string Cls, (string Subj, int Hours)[] Plan)[] Spec =
    [
        (5, "5А", [("Математика", 5), ("Русский язык", 3), ("Русская литература", 2),
            ("Белорусский язык", 2), ("Белорусская литература", 2), ("Иностранный язык", 3),
            ("История", 2), ("География", 2), ("Биология", 1), ("Информатика", 1),
            ("Музыка", 1), ("Изобразительное искусство", 1),
            ("Физическая культура и здоровье", 2), ("Трудовое обучение", 2)]),
        (6, "6А", [("Математика", 5), ("Русский язык", 3), ("Русская литература", 2),
            ("Белорусский язык", 2), ("Белорусская литература", 2), ("Иностранный язык", 3),
            ("История", 2), ("География", 2), ("Биология", 2), ("Информатика", 1),
            ("Музыка", 1), ("Физическая культура и здоровье", 3), ("Трудовое обучение", 2)]),
        (7, "7А", [("Алгебра", 4), ("Геометрия", 2), ("Русский язык", 2),
            ("Русская литература", 2), ("Белорусский язык", 2), ("Белорусская литература", 2),
            ("Иностранный язык", 3), ("История", 2), ("География", 2), ("Биология", 2),
            ("Физика", 2), ("Информатика", 2),
            ("Физическая культура и здоровье", 3), ("Трудовое обучение", 2)]),
        (8, "8А", [("Алгебра", 4), ("Геометрия", 2), ("Русский язык", 2),
            ("Русская литература", 2), ("Белорусский язык", 2), ("Белорусская литература", 1),
            ("Иностранный язык", 3), ("История", 2), ("География", 2), ("Биология", 2),
            ("Физика", 3), ("Химия", 2), ("Информатика", 1),
            ("Физическая культура и здоровье", 3), ("Трудовое обучение", 1)]),
        (9, "9А", [("Алгебра", 4), ("Геометрия", 2), ("Русский язык", 2),
            ("Русская литература", 2), ("Белорусский язык", 2), ("Белорусская литература", 1),
            ("Иностранный язык", 3), ("История", 2), ("География", 2), ("Биология", 2),
            ("Физика", 3), ("Химия", 2), ("Информатика", 1),
            ("Физическая культура и здоровье", 3), ("Трудовое обучение", 1)]),
        (10, "10А", [("Алгебра", 3), ("Геометрия", 2), ("Русский язык", 2),
            ("Русская литература", 3), ("Белорусский язык", 2), ("Белорусская литература", 1),
            ("Иностранный язык", 3), ("Физика", 3), ("Химия", 2), ("Биология", 2),
            ("География", 2), ("История", 2), ("Обществоведение", 2), ("Информатика", 2),
            ("Физическая культура и здоровье", 2), ("Допризывная и медицинская подготовка", 1)]),
        (11, "11А", [("Алгебра", 3), ("Геометрия", 2), ("Русский язык", 2),
            ("Русская литература", 3), ("Белорусский язык", 2), ("Белорусская литература", 1),
            ("Иностранный язык", 3), ("Физика", 3), ("Химия", 2), ("Биология", 2),
            ("География", 1), ("История", 2), ("Обществоведение", 2), ("Информатика", 2),
            ("Физическая культура и здоровье", 2), ("Допризывная и медицинская подготовка", 1),
            ("Астрономия", 1)]),
    ];

    public static List<LoadRow> Rows()
    {
        var rows = new List<LoadRow>();
        foreach (var (grade, cls, plan) in Spec)
        {
            int? shift = grade is 5 or 6 or 7 or 8 ? 2 : null; // 5–8 — вторая смена
            foreach (var (subj, hours) in plan)
            {
                string teacher = $"Учитель {subj} {cls}";
                string? room = subj == "Физическая культура и здоровье" ? "Спортзал" : $"Каб-{cls}";
                rows.Add(new(cls, subj, hours, teacher, false, null, room,
                    null, null, null, shift));
            }
        }
        return rows;
    }

    private static SchedulingProblem BuildOurIdeal()
    {
        var data = SchoolDataImporter.Import(Guid.NewGuid(), Rows(), slotsPerDay: 12);
        var (p, errors) = ProblemBuilder.Build(data.ToProblemInput());
        Assert.NotNull(p);
        Assert.True(errors.Count == 0, "build: " + string.Join(";", errors));
        return p!;
    }

    [Fact]
    public void OurIdeal_BuildsFullCoverage()
    {
        var p = BuildOurIdeal();
        // ОБЖ нет в 8–9-х (по составу учителей: ведётся только в 5-х) — часы уточнить у завуча.
        Assert.Equal(29 + 30 + 32 + 32 + 32 + 34 + 34, p.Occurrences.Count); // 223
        // Учителей хватает: максимальная нагрузка — 5 ч/нед (математика 5-х).
        var load = p.Occurrences.GroupBy(o => o.TeacherId).Select(g => g.Count()).ToList();
        Assert.True(load.Max() <= 5);
        var greedy = GreedyPlacer.Place(p);
        Assert.True(greedy.Unplaced.Count == 0,
            "unplaced: " + string.Join(";", greedy.Unplaced.Take(5).Select(u =>
                p.Occurrences.First(o => o.Id == u).StableKey)));
    }

    [Fact]
    public void OurIdeal_RepairMeasured()
    {
        var p = BuildOurIdeal();
        var greedy = GreedyPlacer.Place(p);
        Assert.Empty(greedy.Unplaced);
        var start = greedy.Placed.Select(kv => new PlacedLesson
        {
            OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
            SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
        }).ToList();
        var repaired = CompactRepair.Repair(p, start);
        var vr = PlacementValidator.Validate(p, repaired.Placements);
        var bd = SoftEvaluator.Evaluate(p, repaired.Placements, EffectiveRuleSet.Default);
        output.WriteLine($"OUR-IDEAL: hard={vr.HardViolations.Count} soft={bd.Total} " +
            $"repaired={repaired.RepairedDays} failed={repaired.FailedDays} [" +
            string.Join(",", bd.Components.Where(c => c.Value != 0).Take(8)
                .Select(c => c.Code + "=" + c.Value)) + "]");
        // Здоровый штат: жадник+ремонт сразу дают валидное (ср. tight-фикстуру).
        Assert.True(vr.IsValid);
    }

    [Fact]
    public void OurIdeal_WeeklyNormsFlagKnownConflict()
    {
        // Фото СанПиН vs наши планы: 5А — 29 ч при базовой норме 25.
        // Чекер обязан это подсветить (предупреждение, не gate) — вопрос завучу №1.
        var p = BuildOurIdeal();
        var greedy = GreedyPlacer.Place(p);
        Assert.Empty(greedy.Unplaced);
        var placements = greedy.Placed.Select(kv => new PlacedLesson
        {
            OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
            SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
        }).ToList();
        var findings = SanPinChecker.Check(p, placements);
        Assert.Contains(findings, f => f.Text.Contains("5А") && f.Text.Contains("29 ч/нед"));
        // А учителя при здоровом штате чисты (все ≤ 5 ч/нед).
        Assert.DoesNotContain(findings, f => f.Text.StartsWith("Учитель"));
    }
}
