using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;
using ClosedXML.Excel;

namespace Amsur.Tests;

// E9 — сетка активного расписания в Excel + gate.
public sealed class ExportScheduleTests
{
    private static (SchedulingProblem Problem, List<PlacedLesson> Placements) Tiny()
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5, StudentCount = 25 };
        var teacher = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Мат", MaxPerDay = 2 };
        var item = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = math.Id, TeacherId = teacher.Id, HoursPerWeek = 2
        };
        var input = new ProblemInput([cls], [teacher], [math], [item],
            [], [], [], DaysCount: 2, SlotsPerDay: 3);
        var (p, e) = ProblemBuilder.Build(input,
            new SolverOptions(MaxTimeSeconds: 5, NumSearchWorkers: 1, RandomSeed: 1));
        Assert.Empty(e);
        var placements = p!.Occurrences.Select((o, i) => new PlacedLesson
        {
            OccurrenceId = o.Id, DayIndex = 0, SlotIndex = i + 1
        }).ToList();
        return (p, placements);
    }

    private static string[,] ReadGrid(Stream s)
    {
        using var wb = new XLWorkbook(s);
        var ws = wb.Worksheet("Расписание");
        var used = ws.RangeUsed();
        int rows = used.RowCount(), cols = used.ColumnCount();
        var grid = new string[rows, cols];
        for (int r = 1; r <= rows; r++)
            for (int c = 1; c <= cols; c++)
                grid[r - 1, c - 1] = ws.Cell(r, c).GetString();
        return grid;
    }

    // --- 1. Клетки читаемы: класс, дни, уроки с предметом и учителем ---
    [Fact]
    public void ExportTiny_ReadableCells()
    {
        var (p, placements) = Tiny();
        using var ms = new MemoryStream();
        ScheduleExcelExporter.ExportGrid(p, placements, ms);
        Assert.True(ms.Length > 0);
        var grid = ReadGrid(new MemoryStream(ms.ToArray()));
        var flat = string.Join("\n", grid.Cast<string>());
        Assert.Contains("Класс 5А", flat);
        Assert.Contains("1-я смена", flat);
        Assert.Contains("Пн", flat);
        Assert.DoesNotContain("День 1", flat);
        Assert.Contains("Мат · Иванов", flat);
    }

    // --- 2. Битое расписание: отказ, файла нет ---
    [Fact]
    public void ExportInvalid_Refused()
    {
        var (p, placements) = Tiny();
        var broken = placements.Take(1).ToList(); // неполнота
        using var ms = new MemoryStream();
        var ex = Assert.Throws<InvalidOperationException>(
            () => ScheduleExcelExporter.ExportGrid(p, broken, ms));
        Assert.Contains("жёстких", ex.Message);
        Assert.Equal(0, ms.Length);
    }

    // --- 3. Два класса: оба блока ---
    [Fact]
    public void ExportTwoClasses_BothBlocks()
    {
        var year = Guid.NewGuid();
        var classes = new[] { "5А", "5Б" }.Select(n => new SchoolClass
        {
            AcademicYearId = year, Name = n, Grade = 5, StudentCount = 20
        }).ToList();
        var tA = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var tB = new Teacher { Name = "Петрова", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Мат", MaxPerDay = 2 };
        var curriculum = classes.Select((c, i) => new CurriculumItem
        {
            ClassId = c.Id, SubjectId = math.Id,
            TeacherId = (i == 0 ? tA : tB).Id, HoursPerWeek = 1
        }).ToList();
        var input = new ProblemInput(classes, [tA, tB], [math], curriculum,
            [], [], [], DaysCount: 2, SlotsPerDay: 3);
        var (p, e) = ProblemBuilder.Build(input);
        Assert.Empty(e);
        var placements = p!.Occurrences.Select(o => new PlacedLesson
        {
            OccurrenceId = o.Id, DayIndex = 0, SlotIndex = 1 // разные учителя — коллизий нет
        }).ToList();
        // Один класс в (0,1) у обоих? Нет: два класса могут делить время (разные учителя).
        using var ms = new MemoryStream();
        ScheduleExcelExporter.ExportGrid(p, placements, ms);
        var flat = string.Join("\n", ReadGrid(new MemoryStream(ms.ToArray())).Cast<string>());
        Assert.Contains("Класс 5А", flat);
        Assert.Contains("Класс 5Б", flat);
        Assert.Contains("Петрова", flat);
    }
}
