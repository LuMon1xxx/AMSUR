using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// E10 — импорт нагрузки: имена→сущности, сплиты, шаблон, roundtrip.
public sealed class ImportSchoolDataTests
{
    private static List<LoadRow> Rows(params LoadRow[] rows) => rows.ToList();

    // --- 1. База: схлопывание имён, подсчёт часов ---
    [Fact]
    public void Basic_NamesCollapsedHoursKept()
    {
        var rows = Rows(
            new LoadRow("5А", "Мат", 3, "Иванов", false, null, null),
            new LoadRow("5А", "Рус", 2, "Иванов", false, null, null),
            new LoadRow("5Б", "Мат", 2, "Петрова", false, null, null));
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows);
        Assert.Equal(2, data.Classes.Count);
        Assert.Equal(2, data.Subjects.Count);
        Assert.Equal(2, data.Teachers.Count); // Иванов один на две строки
        Assert.Equal(3, data.Curriculum.Count);
        var (problem, errors) = ProblemBuilder.Build(data.ToProblemInput());
        Assert.Empty(errors);
        Assert.Equal(7, problem!.Occurrences.Count); // 3+2+2
    }

    // --- 2. Сплит: группы A/B + SplitTeachers + sync в построении ---
    [Fact]
    public void Split_GroupsAndSyncBuilt()
    {
        var rows = Rows(new LoadRow("5А", "Англ", 1, "Иванов", true, "Петрова", null));
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows);
        Assert.Equal(2, data.Groups.Count);
        Assert.Single(data.SplitTeachers);
        var (problem, errors) = ProblemBuilder.Build(data.ToProblemInput());
        Assert.Empty(errors);
        Assert.Equal(2, problem!.Occurrences.Count);
        Assert.NotNull(problem.Occurrences[0].SyncGroupId);
    }

    // --- 3. Кабинет: создаётся и привязывается ---
    [Fact]
    public void Room_CreatedAndLinked()
    {
        var rows = Rows(new LoadRow("5А", "Мат", 1, "Иванов", false, null, "101"));
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows);
        var room = Assert.Single(data.Rooms);
        Assert.Equal("101", room.Name);
        Assert.Equal(room.Id, data.Curriculum[0].RoomId);
    }

    // --- 4. Пусто — честная ошибка ---
    [Fact]
    public void Empty_ThrowsHonestError()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SchoolDataImporter.Import(Guid.NewGuid(), []));
        Assert.Contains("не содержит строк", ex.Message);
    }

    // --- 5. Roundtrip через Excel: шаблон → заполнение → импорт ---
    [Fact]
    public void Roundtrip_TemplateHeaderOnly_ThenFilled()
    {
        using var template = new MemoryStream();
        SchoolDataImporter.ExportTemplate(template);
        var empty = ExcelLoadExchange.ImportLoad(new MemoryStream(template.ToArray()));
        Assert.Empty(empty); // шапка без строк — ноль строк, не ошибка формата

        var filled = Rows(new LoadRow("6Б", "Физра", 2, "Сидоров", false, null, null));
        using var ms = new MemoryStream();
        ExcelLoadExchange.ExportLoad(ms, filled);
        var back = ExcelLoadExchange.ImportLoad(new MemoryStream(ms.ToArray()));
        var data = SchoolDataImporter.Import(Guid.NewGuid(), back);
        Assert.Single(data.Classes);
        Assert.Equal("6Б", data.Classes[0].Name);
        var (problem, errors) = ProblemBuilder.Build(data.ToProblemInput());
        Assert.Empty(errors);
    }

    // --- 6. Настройки сетки пробрасываются ---
    [Fact]
    public void GridSettings_PassedThrough()
    {
        var rows = Rows(new LoadRow("5А", "Мат", 1, "Иванов", false, null, null));
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows, daysCount: 6, slotsPerDay: 8);
        Assert.Equal(6, data.ToProblemInput().DaysCount);
        Assert.Equal(8, data.ToProblemInput().SlotsPerDay);
        Assert.NotEmpty(data.Notes);
    }
}
