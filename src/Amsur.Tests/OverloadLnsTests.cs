using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// Фаза D (OverloadLns): ремонт перегруза, кап дыр, детерминизм.
// Только unit-уровень на синтетике, быстрые.
public sealed class OverloadLnsTests
{
    // Класс 5-й параллели: кэп ремонта 6 при cap=7. День 0 — 7 distinct-слотов
    // (перегруз 1), день 1 — 1 урок. Учителя в кэпах (≤6/день).
    private static SchedulingProblem Tiny(out List<PlacedLesson> placements)
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5, StudentCount = 25 };
        var ta = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var tb = new Teacher { Name = "Петров", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Мат", MaxPerDay = 5 };
        var rus = new Subject { Name = "Рус", MaxPerDay = 5 };
        var items = new List<CurriculumItem>
        {
            new() { ClassId = cls.Id, SubjectId = math.Id, TeacherId = ta.Id, HoursPerWeek = 4 },
            new() { ClassId = cls.Id, SubjectId = rus.Id, TeacherId = tb.Id, HoursPerWeek = 4 },
        };
        var input = new ProblemInput([cls], [ta, tb], [math, rus], items,
            [], [], [], DaysCount: 2, SlotsPerDay: 8);
        var (p, e) = ProblemBuilder.Build(input);
        Assert.Empty(e);
        var occ = p!.Occurrences.OrderBy(o => o.StableKey, StringComparer.Ordinal).ToList();
        var mathOcc = occ.Where(o => o.SubjectId == math.Id).ToList();
        var rusOcc = occ.Where(o => o.SubjectId == rus.Id).ToList();
        placements =
        [
            new() { OccurrenceId = mathOcc[0].Id, DayIndex = 0, SlotIndex = 1 },
            new() { OccurrenceId = mathOcc[1].Id, DayIndex = 0, SlotIndex = 2 },
            new() { OccurrenceId = mathOcc[2].Id, DayIndex = 0, SlotIndex = 3 },
            new() { OccurrenceId = mathOcc[3].Id, DayIndex = 0, SlotIndex = 4 },
            new() { OccurrenceId = rusOcc[0].Id, DayIndex = 0, SlotIndex = 5 },
            new() { OccurrenceId = rusOcc[1].Id, DayIndex = 0, SlotIndex = 6 },
            new() { OccurrenceId = rusOcc[2].Id, DayIndex = 0, SlotIndex = 7 },
            new() { OccurrenceId = rusOcc[3].Id, DayIndex = 1, SlotIndex = 1 },
        ];
        return p;
    }

    [Fact]
    public void RepairsOverload_Synthetic()
    {
        var p = Tiny(out var placements);
        var occById = p.Occurrences.ToDictionary(o => o.Id);
        Assert.Equal(1, OverloadLns.CountOverloads(p, occById, placements, cap: 7));
        var res = OverloadLns.Improve(p, placements, TimeSpan.FromSeconds(5), seed: 11, cap: 7);
        Assert.Equal(0, res.Overloads);
        Assert.Equal(0, res.PupilHard);
        Assert.Equal(placements.Count, res.Placements.Count);
    }

    [Fact]
    public void NeverWorsens_GapsCap()
    {
        var p = Tiny(out var placements);
        var occById = p.Occurrences.ToDictionary(o => o.Id);
        int g0 = TeacherDayLns.TeacherGridGaps(occById, placements);
        var res = OverloadLns.Improve(p, placements, TimeSpan.FromSeconds(5), seed: 11, cap: 7);
        // Приём: дыры ≤ +2/ход; один ход → итог ≤ g0+2.
        Assert.True(res.TeacherGaps <= g0 + 2);
        Assert.True(res.Overloads <= 1);
    }

    [Fact]
    public void Deterministic_SameSeed()
    {
        var p = Tiny(out var placements);
        var a = OverloadLns.Improve(p, placements, TimeSpan.FromSeconds(3), seed: 7, cap: 7);
        var b = OverloadLns.Improve(p, placements, TimeSpan.FromSeconds(3), seed: 7);
        Assert.Equal(a.Overloads, b.Overloads);
        Assert.Equal(a.TeacherGaps, b.TeacherGaps);
        Assert.Equal(a.Accepted, b.Accepted);
    }
}
