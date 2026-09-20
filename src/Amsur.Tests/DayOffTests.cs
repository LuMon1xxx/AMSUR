using Amsur.Application;
using Amsur.Domain;
using Amsur.Infrastructure;
using Amsur.Scheduling.Core;
using ClosedXML.Excel;

namespace Amsur.Tests;

// P-DAYOFF — недоступность учителей: парсинг, объединение, проброс в задачу,
// шаблон с 9 колонками, совместимость со старыми файлами, персист.
public sealed class DayOffTests
{
    private static List<LoadRow> Rows(params LoadRow[] rows) => rows.ToList();

    // --- 1. Дни парсятся (1-based → DayIndex) и объединяются по учителю ---
    [Fact]
    public void DaysOff_ParsedAndUnioned()
    {
        var rows = Rows(
            new LoadRow("5А", "Мат", 2, "Иванов", false, null, null, "1,3", null),
            new LoadRow("5Б", "Мат", 2, "Иванов", false, null, null, "3,5", null));
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows, daysCount: 5, slotsPerDay: 7);
        var tid = data.Teachers.Single(t => t.Name == "Иванов").Id;
        var days = data.DaysOff.Where(d => d.TeacherId == tid)
            .Select(d => d.DayIndex).OrderBy(d => d).ToList();
        Assert.Equal([0, 2, 4], days);
        Assert.Contains(data.Notes, n => n.Contains("Недоступность"));
    }

    // --- 2. День реально закрыт в построенной задаче ---
    [Fact]
    public void DaysOff_BlocksDayInBuild()
    {
        var rows = Rows(new LoadRow("5А", "Мат", 2, "Иванов", false, null, null, "1", null));
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows, daysCount: 2, slotsPerDay: 3);
        var (problem, errors) = ProblemBuilder.Build(data.ToProblemInput());
        Assert.Empty(errors);
        foreach (var occ in problem!.Occurrences)
        {
            var allowed = problem.AllowedDays[occ.Id];
            Assert.DoesNotContain(0, allowed);
            Assert.Contains(1, allowed);
        }
    }

    // --- 3. Слот закрыт ежедневно ---
    [Fact]
    public void Slots_BlockedDaily()
    {
        var rows = Rows(new LoadRow("5А", "Мат", 2, "Иванов", false, null, null, null, "1"));
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows, daysCount: 2, slotsPerDay: 3);
        Assert.Equal(2, data.Unavailability.Count); // слот 1 × 2 дня
        var (problem, errors) = ProblemBuilder.Build(data.ToProblemInput());
        Assert.Empty(errors);
        foreach (var occ in problem!.Occurrences)
            Assert.DoesNotContain(1, problem.AllowedSlots[occ.Id]);
    }

    // --- 4. Мусор — громкая ошибка с именем учителя ---
    [Theory]
    [InlineData("0", null)]
    [InlineData("6", null)]
    [InlineData("abc", null)]
    [InlineData(null, "0")]
    [InlineData(null, "9")]
    public void Invalid_ThrowsWithTeacher(string? days, string? slots)
    {
        var rows = Rows(new LoadRow("5А", "Мат", 2, "Иванов", false, null, null, days, slots));
        var ex = Assert.Throws<InvalidOperationException>(
            () => SchoolDataImporter.Import(Guid.NewGuid(), rows, daysCount: 5, slotsPerDay: 7));
        Assert.Contains("Иванов", ex.Message);
    }

    // --- 5. Roundtrip через Excel с 9 колонками ---
    [Fact]
    public void ExcelRoundtrip_NineColumns()
    {
        var rows = Rows(new LoadRow("5А", "Мат", 2, "Иванов", false, null, "101", "2,4", "1"));
        using var ms = new MemoryStream();
        ExcelLoadExchange.ExportLoad(ms, rows);
        var back = ExcelLoadExchange.ImportLoad(new MemoryStream(ms.ToArray()));
        var r = Assert.Single(back);
        Assert.Equal("2,4", r.UnavailDays);
        Assert.Equal("1", r.UnavailSlots);
        var data = SchoolDataImporter.Import(Guid.NewGuid(), back, daysCount: 5, slotsPerDay: 7);
        Assert.Equal(2, data.DaysOff.Count);
    }

    // --- 6. Старый файл из 7 колонок — импортируется, полей нет ---
    [Fact]
    public void OldSevenColumnFile_ImportsWithoutUnavail()
    {
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Load");
            string[] h = ["Class", "Subject", "HoursPerWeek", "Teacher", "Split", "TeacherB", "Room"];
            for (int c = 0; c < h.Length; c++) ws.Cell(1, c + 1).Value = h[c];
            ws.Cell(2, 1).Value = "5А"; ws.Cell(2, 2).Value = "Мат"; ws.Cell(2, 3).Value = 2;
            ws.Cell(2, 4).Value = "Иванов";
            wb.SaveAs(ms);
        }
        var back = ExcelLoadExchange.ImportLoad(new MemoryStream(ms.ToArray()));
        var r = Assert.Single(back);
        Assert.Null(r.UnavailDays);
        Assert.Null(r.UnavailSlots);
        var data = SchoolDataImporter.Import(Guid.NewGuid(), back);
        Assert.Empty(data.DaysOff);
        Assert.Empty(data.Unavailability);
    }

    // --- 7. Персист строк хранит недоступность ---
    [Fact]
    public async Task StoreRoundtrip_KeepsUnavail()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"amsur-dayoff-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SqliteSchoolDataStore($"Data Source={Path.Combine(dir, "school.db")}");
            await store.InitializeAsync();
            var rows = new List<StoredLoadRow>
            {
                new("5А", "Мат", 2, "Иванов", false, null, "101", "1,3", "2")
            };
            var year = Guid.NewGuid();
            await store.SaveAsync(year, rows, 5, 7, "test");
            var got = await store.LoadAsync();
            Assert.NotNull(got);
            var r = Assert.Single(got!.Rows);
            Assert.Equal("1,3", r.UnavailDays);
            Assert.Equal("2", r.UnavailSlots);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
