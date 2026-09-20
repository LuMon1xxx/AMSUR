using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// P-CSV — плоская выгрузка: шапка, строки, gate, экранирование.
public sealed class CsvExportTests
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

    // --- 1. Шапка + строки с именами ---
    [Fact]
    public void ExportTiny_HeaderAndRows()
    {
        var (p, placements) = Tiny();
        string csv = ScheduleCsvExporter.ExportCsv(p, placements);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Class;Day;Slot;Subject;Teacher;Room", lines[0]);
        Assert.Equal(3, lines.Length); // шапка + 2 урока
        Assert.Contains("5А;Пн;1;Мат;Иванов;", lines[1]);
    }

    // --- 2. Битое — отказ ---
    [Fact]
    public void ExportInvalid_Refused()
    {
        var (p, placements) = Tiny();
        var ex = Assert.Throws<InvalidOperationException>(
            () => ScheduleCsvExporter.ExportCsv(p, placements.Take(1).ToList()));
        Assert.Contains("жёстких", ex.Message);
    }

    // --- 3. Точка с запятой в имени — в кавычках ---
    [Fact]
    public void ExportEscapes_Semicolon()
    {
        var (p, placements) = Tiny();
        var occ = p.Occurrences[0];
        p.Subjects[occ.SubjectId].Name = "Проект; исследование";
        string csv = ScheduleCsvExporter.ExportCsv(p, placements);
        Assert.Contains("\"Проект; исследование\"", csv);
    }
}
