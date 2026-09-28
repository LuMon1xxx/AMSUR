using Amsur.Application;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// G1-mini (decision-pairs.md): колонка Shift (11-я, опциональная; пусто=1).
// twoShift ⟺ есть Shift==2; полосы — нумерация стены: shift1=[1..8], shift2=[6..12].
public sealed class ShiftImportTests
{
    private static List<LoadRow> Rows(params LoadRow[] rows) => rows.ToList();

    // --- 1. Полосы классов + якоря (полосовая относительность) ---
    [Fact]
    public void Shift_Bands_ClassSlotsAndAnchors()
    {
        var rows = Rows(
            new LoadRow("5А", "Мат", 2, "Иванов", false, null, null),
            new LoadRow("6А", "Мат", 2, "Петров", false, null, null, Shift: 2));
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows,
            daysCount: 5, slotsPerDay: 12);
        var byName = data.Classes.ToDictionary(c => c.Name);
        Assert.Equal(Enumerable.Range(1, 8), data.ClassSlots[byName["5А"].Id]);
        Assert.Equal(Enumerable.Range(6, 7), data.ClassSlots[byName["6А"].Id]);
        // Якоря полосово-относительны: 1-я смена → 1, 2-я → 6.
        var (problem, errors) = ProblemBuilder.Build(data.ToProblemInput());
        Assert.Empty(errors);
        Assert.Equal(1, StudentCompactness.AnchorFor(problem!, byName["5А"].Id));
        Assert.Equal(6, StudentCompactness.AnchorFor(problem!, byName["6А"].Id));
        // AllowedSlots билдера уважают полосы.
        var occ6 = problem!.Occurrences.Where(o => o.ClassId == byName["6А"].Id).ToList();
        Assert.All(occ6, o => Assert.All(problem.AllowedSlots[o.Id], s => Assert.InRange(s, 6, 12)));
    }

    // --- 2. Пусто = 1-я смена (односменка — как раньше) ---
    [Fact]
    public void Shift_EmptyIsFirst_NoBands()
    {
        var rows = Rows(new LoadRow("5А", "Мат", 2, "Иванов", false, null, null));
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows,
            daysCount: 5, slotsPerDay: 8);
        Assert.Empty(data.ClassSlots); // backward compatible: полос нет
        Assert.DoesNotContain(data.Notes, n => n.Contains("R-G1"));
    }

    // --- 3. Несогласие строк класса — fail-loud ---
    [Fact]
    public void Shift_Disagreement_Rejected()
    {
        var rows = Rows(
            new LoadRow("6А", "Мат", 2, "Петров", false, null, null, Shift: 2),
            new LoadRow("6А", "Рус", 2, "Петров", false, null, null, Shift: 1));
        var ex = Assert.Throws<InvalidOperationException>(
            () => SchoolDataImporter.Import(Guid.NewGuid(), rows,
                daysCount: 5, slotsPerDay: 12));
        Assert.Contains("по-разному", ex.Message);
    }

    // --- 4. Двухсменка требует 12 уроков (нумерация стены №1–12) ---
    [Fact]
    public void Shift_TwoShift_Needs12()
    {
        var rows = Rows(
            new LoadRow("6А", "Мат", 2, "Петров", false, null, null, Shift: 2));
        var ex = Assert.Throws<InvalidOperationException>(
            () => SchoolDataImporter.Import(Guid.NewGuid(), rows,
                daysCount: 5, slotsPerDay: 8));
        Assert.Contains("Двухсменка: поставьте 12", ex.Message);
    }

    // --- 5. Больше 12 при двухсменке — fail-loud (номера стены №1–12).
    // Односменка с абстрактной сеткой (D-28: 5×14, полосы задаёт код) —
    // как раньше, без отказа (V1 baseline: RealTeacherTests).
    [Fact]
    public void Shift_Over12_Rejected()
    {
        var rows = Rows(new LoadRow("6А", "Мат", 1, "Иванов", false, null, null, Shift: 2));
        var ex = Assert.Throws<InvalidOperationException>(
            () => SchoolDataImporter.Import(Guid.NewGuid(), rows,
                daysCount: 5, slotsPerDay: 13));
        Assert.Contains("не больше 12", ex.Message);
    }

    // --- 6. Мусор в смене ручной строки — fail-loud ---
    [Fact]
    public void Shift_BadValue_Rejected()
    {
        var rows = Rows(new LoadRow("5А", "Мат", 1, "Иванов", false, null, null, Shift: 3));
        var ex = Assert.Throws<InvalidOperationException>(
            () => SchoolDataImporter.Import(Guid.NewGuid(), rows));
        Assert.Contains("смена", ex.Message);
    }

    // --- 7. Мусор в смене Excel-ячейки — fail-loud при парсинге ---
    [Fact]
    public void Shift_Excel_BadValue_Rejected()
    {
        using var ms = new MemoryStream();
        using (var wb = new ClosedXML.Excel.XLWorkbook())
        {
            var ws = wb.AddWorksheet("Load");
            ws.Cell(1, 1).Value = "Class"; ws.Cell(1, 2).Value = "Subject";
            ws.Cell(1, 3).Value = "HoursPerWeek"; ws.Cell(1, 4).Value = "Teacher";
            ws.Cell(1, 11).Value = "Shift";
            ws.Cell(2, 1).Value = "5А"; ws.Cell(2, 2).Value = "Математика";
            ws.Cell(2, 3).Value = "2"; ws.Cell(2, 4).Value = "Иванов";
            ws.Cell(2, 11).Value = "3";
            wb.SaveAs(ms);
        }
        Assert.Throws<InvalidOperationException>(
            () => ExcelLoadExchange.ImportLoad(new MemoryStream(ms.ToArray())));
    }

    // --- 8. R-G1: перекрытие полос небезопасно → ShiftBands=null + запись ---
    [Fact]
    public void Shift_TwoShift_RG1_NoteAndNullBands()
    {
        var rows = Rows(
            new LoadRow("5А", "Мат", 2, "Иванов", false, null, null),
            new LoadRow("6А", "Мат", 2, "Петров", false, null, null, Shift: 2));
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows,
            daysCount: 5, slotsPerDay: 12);
        Assert.Contains(data.Notes, n => n.Contains("R-G1"));
        // Перекрытие [(1,8),(6,12)] для gap-split небезопасно (слот стыка дважды) —
        // билдер получает null и ставит одну полосу [(1,12)]: cross-shift схлопнут.
        var (problem, errors) = ProblemBuilder.Build(data.ToProblemInput());
        Assert.Empty(errors);
        Assert.Single(problem!.ShiftBands);
    }

    // --- 9. Односменка на 8 слотах — штатно, полос нет ---
    [Fact]
    public void Shift_SingleShift_SP8_Ok()
    {
        var rows = Rows(new LoadRow("5А", "Мат", 2, "Иванов", false, null, null, Shift: 1));
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows,
            daysCount: 5, slotsPerDay: 8);
        Assert.Empty(data.ClassSlots);
        var (problem, errors) = ProblemBuilder.Build(data.ToProblemInput());
        Assert.Empty(errors);
    }
}
