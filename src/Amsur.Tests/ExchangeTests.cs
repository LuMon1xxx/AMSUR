using Amsur.Application;

namespace Amsur.Tests;

// EPIC-C C2: Excel split roundtrip (P0-блокер; класс V1-бага UNIQUE-violation).
public sealed class ExchangeTests
{
    [Fact]
    public void Split_Roundtrip_SingleRow_NoDuplicates()
    {
        var rows = new List<LoadRow>
        {
            new("5А", "Математика", 5, "Иванов", false, null, "101"),
            new("8А", "Английский", 3, "Петрова", true, "Сидоров", null),
        };
        using var ms = new MemoryStream();
        ExcelLoadExchange.ExportLoad(ms, rows);
        ms.Position = 0;
        var back = ExcelLoadExchange.ImportLoad(ms);
        Assert.Equal(rows.Count, back.Count); // сплит — одна строка, дублей нет
        Assert.Equal(rows, back); // семантическое равенство (record equality)
    }

    [Fact]
    public void Split_WithoutTeacherB_Rejected()
    {
        var rows = new List<LoadRow>
        {
            new("8А", "Английский", 3, "Петрова", true, null, null),
        };
        using var ms = new MemoryStream();
        ExcelLoadExchange.ExportLoad(ms, rows);
        ms.Position = 0;
        Assert.Throws<InvalidOperationException>(() => ExcelLoadExchange.ImportLoad(ms));
    }

    [Fact]
    public void BadHours_Rejected()
    {
        using var ms = new MemoryStream();
        using (var wb = new ClosedXML.Excel.XLWorkbook())
        {
            var ws = wb.AddWorksheet("Load");
            ws.Cell(1, 1).Value = "Class"; ws.Cell(1, 2).Value = "Subject";
            ws.Cell(1, 3).Value = "HoursPerWeek"; ws.Cell(1, 4).Value = "Teacher";
            ws.Cell(2, 1).Value = "5А"; ws.Cell(2, 2).Value = "Математика";
            ws.Cell(2, 3).Value = "0"; ws.Cell(2, 4).Value = "Иванов";
            wb.SaveAs(ms);
        }
        ms.Position = 0;
        Assert.Throws<InvalidOperationException>(() => ExcelLoadExchange.ImportLoad(ms));
    }
}
