using Amsur.Application;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// Pairs-v1 (decision-pairs.md): колонка Pair (10-я, опциональная) — две строки
// одного класса с одинаковым значением = одновременные уроки подгрупп A/B.
// Билдер/жадный/валидатор не меняются (семантика sync уже есть) — тестируем вход.
public sealed class PairImportTests
{
    private static List<LoadRow> Rows(params LoadRow[] rows) => rows.ToList();

    // --- 1. Валидная пара: общий SyncGroupId + группы A/B (порядок файла) ---
    [Fact]
    public void Pair_Valid_TwoRowsShareSync_GroupsAB()
    {
        var rows = Rows(
            new LoadRow("10А", "Химия (проф)", 1, "Петрова", false, null, "312", PairName: "П1"),
            new LoadRow("10А", "Биология (база)", 1, "Сидорова", false, null, "314", PairName: "П1"));
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows);
        Assert.Equal(2, data.Curriculum.Count);
        var first = data.Curriculum[0];
        var second = data.Curriculum[1];
        Assert.NotNull(first.SyncGroupId);
        Assert.Equal(first.SyncGroupId, second.SyncGroupId); // общий SyncGroupId пары
        Assert.NotNull(first.GroupId);
        Assert.NotNull(second.GroupId);
        Assert.NotEqual(first.GroupId, second.GroupId);
        var names = data.Groups.Where(g => g.ClassId == data.Classes[0].Id)
            .ToDictionary(g => g.Id, g => g.Name);
        Assert.Equal("A", names[first.GroupId!.Value]); // member1 → A
        Assert.Equal("B", names[second.GroupId!.Value]); // member2 → B
        // Движок видит пару как per-hour sync (без изменений билдера).
        var (problem, errors) = ProblemBuilder.Build(data.ToProblemInput());
        Assert.Empty(errors);
        var occs = problem!.Occurrences;
        Assert.Equal(2, occs.Count);
        Assert.Equal(occs[0].SyncGroupId, occs[1].SyncGroupId);
        Assert.NotEqual(occs[0].GroupId, occs[1].GroupId);
    }

    // --- 2. Пустая пара = как раньше (совместимость) ---
    [Fact]
    public void Pair_Empty_NoBehavior()
    {
        var rows = Rows(new LoadRow("5А", "Мат", 2, "Иванов", false, null, null));
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows);
        Assert.Null(data.Curriculum[0].GroupId);
        Assert.Null(data.Curriculum[0].SyncGroupId);
    }

    // --- 3. Ровно 2 строки (fail-loud, русские тексты) ---
    [Fact]
    public void Pair_ThreeRows_Rejected()
    {
        var rows = Rows(
            new LoadRow("10А", "Химия", 1, "Петрова", false, null, null, PairName: "П1"),
            new LoadRow("10А", "Биология", 1, "Сидорова", false, null, null, PairName: "П1"),
            new LoadRow("10А", "Физика", 1, "Иванова", false, null, null, PairName: "П1"));
        var ex = Assert.Throws<InvalidOperationException>(
            () => SchoolDataImporter.Import(Guid.NewGuid(), rows));
        Assert.Contains("ровно 2", ex.Message);
    }

    [Fact]
    public void Pair_SingleRow_Rejected()
    {
        var rows = Rows(
            new LoadRow("10А", "Химия", 1, "Петрова", false, null, null, PairName: "П1"));
        var ex = Assert.Throws<InvalidOperationException>(
            () => SchoolDataImporter.Import(Guid.NewGuid(), rows));
        Assert.Contains("ровно 2", ex.Message);
    }

    // --- 4. Scope PairId — в пределах класса: одно имя в разных классах = разные пары ---
    [Fact]
    public void Pair_SameNameDifferentClasses_Ok()
    {
        var rows = Rows(
            new LoadRow("10А", "Химия", 1, "Петрова", false, null, null, PairName: "П1"),
            new LoadRow("10А", "Биология", 1, "Сидорова", false, null, null, PairName: "П1"),
            new LoadRow("11А", "Химия", 1, "Петрова", false, null, null, PairName: "П1"),
            new LoadRow("11А", "Биология", 1, "Сидорова", false, null, null, PairName: "П1"));
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows);
        var syncs = data.Curriculum.Select(i => i.SyncGroupId).Distinct().ToList();
        Assert.Equal(2, syncs.Count); // две независимые пары, не одна на 4 строки
    }

    // --- 5. Равные часы (на resolved-часах) ---
    [Fact]
    public void Pair_UnequalHours_Rejected()
    {
        var rows = Rows(
            new LoadRow("10А", "Химия", 2, "Петрова", false, null, null, PairName: "П1"),
            new LoadRow("10А", "Биология", 1, "Сидорова", false, null, null, PairName: "П1"));
        var ex = Assert.Throws<InvalidOperationException>(
            () => SchoolDataImporter.Import(Guid.NewGuid(), rows));
        Assert.Contains("различаются", ex.Message);
    }

    // --- 6. Разные учителя ---
    [Fact]
    public void Pair_SameTeacher_Rejected()
    {
        var rows = Rows(
            new LoadRow("10А", "Химия", 1, "Петрова", false, null, null, PairName: "П1"),
            new LoadRow("10А", "Биология", 1, "Петрова", false, null, null, PairName: "П1"));
        var ex = Assert.Throws<InvalidOperationException>(
            () => SchoolDataImporter.Import(Guid.NewGuid(), rows));
        Assert.Contains("разные учителя", ex.Message);
    }

    // --- 7. Не сплит-строка ---
    [Fact]
    public void Pair_SplitRow_Rejected()
    {
        var rows = Rows(
            new LoadRow("10А", "Английский", 1, "Петрова", true, "Сидорова", null, PairName: "П1"),
            new LoadRow("10А", "Биология", 1, "Иванова", false, null, null, PairName: "П1"));
        var ex = Assert.Throws<InvalidOperationException>(
            () => SchoolDataImporter.Import(Guid.NewGuid(), rows));
        Assert.Contains("уберите Split", ex.Message);
    }

    // --- 8. Pair OrdinalIgnoreCase ---
    [Fact]
    public void Pair_CaseInsensitive()
    {
        var rows = Rows(
            new LoadRow("10А", "Химия", 1, "Петрова", false, null, null, PairName: "п1"),
            new LoadRow("10А", "Биология", 1, "Сидорова", false, null, null, PairName: "П1"));
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows);
        Assert.Equal(data.Curriculum[0].SyncGroupId, data.Curriculum[1].SyncGroupId);
    }

    // --- 9. R7 строже: пара из одного предмета с разными учителями — громкий отказ ---
    [Fact]
    public void Pair_SameSubjectDifferentTeachers_R7Rejected()
    {
        var rows = Rows(
            new LoadRow("10А", "Химия", 1, "Петрова", false, null, null, PairName: "П1"),
            new LoadRow("10А", "Химия", 1, "Сидорова", false, null, null, PairName: "П1"));
        // Валидация пары проходит (учителя разные — требование пары),
        // но R7 (один учитель на класс+предмет) исключений для пар не делает.
        var ex = Assert.Throws<InvalidOperationException>(
            () => SchoolDataImporter.Import(Guid.NewGuid(), rows));
        Assert.Contains("Закрепление", ex.Message);
    }

    // --- 10. Excel-roundtrip: Pair переживает экспорт/импорт ---
    [Fact]
    public void Pair_ExcelRoundtrip()
    {
        var rows = Rows(
            new LoadRow("10А", "Химия", 1, "Петрова", false, null, "312", PairName: "П1"),
            new LoadRow("10А", "Биология", 1, "Сидорова", false, null, "314", PairName: "П1"));
        using var ms = new MemoryStream();
        ExcelLoadExchange.ExportLoad(ms, rows);
        var back = ExcelLoadExchange.ImportLoad(new MemoryStream(ms.ToArray()));
        Assert.Equal(rows, back); // record equality включая PairName
    }

    // --- 11. Шаблон несёт колонки Pair/Shift (UI: шаблон авто) ---
    [Fact]
    public void Template_ContainsPairAndShiftColumns()
    {
        using var ms = new MemoryStream();
        SchoolDataImporter.ExportTemplate(ms);
        using var wb = new ClosedXML.Excel.XLWorkbook(new MemoryStream(ms.ToArray()));
        var header = wb.Worksheets.First().Row(1).CellsUsed()
            .Select(c => c.GetString()).ToList();
        Assert.Contains("Pair", header);
        Assert.Contains("Shift", header);
    }

    // --- 12. L8 drift-guard: дефолт сетки 5×8 (D-pair-01) ---
    [Fact]
    public void DefaultGrid_Is5x8_DriftGuard()
    {
        var rows = Rows(new LoadRow("5А", "Мат", 1, "Иванов", false, null, null));
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows); // без дней/слотов
        Assert.Equal(5, data.DaysCount);
        Assert.Equal(8, data.SlotsPerDay);
    }
}
