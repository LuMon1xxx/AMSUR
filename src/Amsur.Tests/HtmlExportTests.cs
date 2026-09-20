using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// B4 — HTML-выгрузка: те же ворота и та же сетка, что у Excel.
public sealed class HtmlExportTests
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

    // --- 1. Клетки читаемы + печать-CSS на месте ---
    [Fact]
    public void ExportTiny_ReadableCells()
    {
        var (p, placements) = Tiny();
        string html = ScheduleHtmlExporter.ExportHtml(p, placements);
        Assert.Contains("Класс 5А", html);
        Assert.Contains("1-я смена", html);
        Assert.Contains("Пн", html);
        Assert.Contains("Мат · Иванов", html);
        Assert.Contains("<table>", html);
        Assert.Contains("@media print", html);
    }

    // --- 2. Потоковая перегрузка пишет тот же документ ---
    [Fact]
    public void ExportStream_MatchesString()
    {
        var (p, placements) = Tiny();
        using var ms = new MemoryStream();
        ScheduleHtmlExporter.ExportHtml(p, placements, ms);
        Assert.True(ms.Length > 0);
        string fromStream = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        Assert.Equal(ScheduleHtmlExporter.ExportHtml(p, placements), fromStream);
    }

    // --- 3. Битое расписание: отказ исключением ---
    [Fact]
    public void ExportInvalid_Refused()
    {
        var (p, placements) = Tiny();
        var broken = placements.Take(1).ToList(); // неполнота
        var ex = Assert.Throws<InvalidOperationException>(
            () => ScheduleHtmlExporter.ExportHtml(p, broken));
        Assert.Contains("жёстких", ex.Message);
    }

    // --- 4. Экранирование: имена с <&> не ломают разметку ---
    [Fact]
    public void ExportEscapes_XssChars()
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5<А>", Grade = 5, StudentCount = 25 };
        var teacher = new Teacher { Name = "Иванов & Сыновья", MaxLessonsPerDay = 6 };
        var subj = new Subject { Name = "Рисование", MaxPerDay = 2 };
        var item = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = subj.Id, TeacherId = teacher.Id, HoursPerWeek = 1
        };
        var input = new ProblemInput([cls], [teacher], [subj], [item],
            [], [], [], DaysCount: 2, SlotsPerDay: 3);
        var (p, e) = ProblemBuilder.Build(input,
            new SolverOptions(MaxTimeSeconds: 5, NumSearchWorkers: 1, RandomSeed: 1));
        Assert.Empty(e);
        var placements = p!.Occurrences.Select(o => new PlacedLesson
        {
            OccurrenceId = o.Id, DayIndex = 0, SlotIndex = 1
        }).ToList();
        string html = ScheduleHtmlExporter.ExportHtml(p, placements);
        Assert.Contains("5&lt;А&gt;", html);
        Assert.Contains("Иванов &amp; Сыновья", html);
        Assert.DoesNotContain("5<А>", html);
    }
}
