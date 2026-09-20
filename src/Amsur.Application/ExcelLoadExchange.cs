namespace Amsur.Application;

using ClosedXML.Excel;

// Обмен учебной нагрузкой через Excel (EPIC-C C2, D-07).
// Ключевое исправление против V1-бага: сплит подгрупп — ОДНА строка
// (флаг Split + TeacherB), а не N строк на один CurriculumItem.
// Поэтому импорт никогда не создаёт дубликаты (UNIQUE-класс бага исключён форматом).
// P0-scope: симметрия формата (export→import→semantic equality); привязка имён к Id — P2.
public sealed record LoadRow(
    string ClassName,
    string SubjectName,
    int HoursPerWeek,
    string TeacherName,
    bool SplitSubgroups,
    string? SplitTeacherBName,
    string? RoomName,
    // P-DAYOFF: дни (1-based номера через запятую) и слоты, когда учителя нет.
    // Опциональны: старые файлы из 7 колонок импортируются как раньше.
    string? UnavailDays = null,
    string? UnavailSlots = null);

public static class ExcelLoadExchange
{
    private static readonly string[] Header =
        ["Class", "Subject", "HoursPerWeek", "Teacher", "Split", "TeacherB", "Room",
         "UnavailDays", "UnavailSlots"];

    public static void ExportLoad(Stream destination, IReadOnlyList<LoadRow> rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Load");
        for (int c = 0; c < Header.Length; c++)
            ws.Cell(1, c + 1).Value = Header[c];
        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            ws.Cell(r + 2, 1).Value = row.ClassName;
            ws.Cell(r + 2, 2).Value = row.SubjectName;
            ws.Cell(r + 2, 3).Value = row.HoursPerWeek;
            ws.Cell(r + 2, 4).Value = row.TeacherName;
            ws.Cell(r + 2, 5).Value = row.SplitSubgroups ? "A/B" : "";
            ws.Cell(r + 2, 6).Value = row.SplitTeacherBName ?? "";
            ws.Cell(r + 2, 7).Value = row.RoomName ?? "";
            ws.Cell(r + 2, 8).Value = row.UnavailDays ?? "";
            ws.Cell(r + 2, 9).Value = row.UnavailSlots ?? "";
        }
        wb.SaveAs(destination);
    }

    public static IReadOnlyList<LoadRow> ImportLoad(Stream source)
    {
        using var wb = new XLWorkbook(source);
        var ws = wb.Worksheets.FirstOrDefault()
            ?? throw new InvalidOperationException("Workbook has no worksheets.");
        var rows = new List<LoadRow>();
        int r = 2;
        while (true)
        {
            string cls = ws.Cell(r, 1).GetString().Trim();
            string subj = ws.Cell(r, 2).GetString().Trim();
            if (string.IsNullOrEmpty(cls) && string.IsNullOrEmpty(subj)) break; // конец данных
            if (string.IsNullOrEmpty(cls) || string.IsNullOrEmpty(subj))
                throw new InvalidOperationException($"Row {r}: Class and Subject are required.");
            if (!int.TryParse(ws.Cell(r, 3).GetString().Trim(), out int hours) || hours <= 0)
                throw new InvalidOperationException($"Row {r}: HoursPerWeek must be a positive integer.");
            string teacher = ws.Cell(r, 4).GetString().Trim();
            if (string.IsNullOrEmpty(teacher))
                throw new InvalidOperationException($"Row {r}: Teacher is required.");
            string split = ws.Cell(r, 5).GetString().Trim();
            string teacherB = ws.Cell(r, 6).GetString().Trim();
            string room = ws.Cell(r, 7).GetString().Trim();
            string unavailDays = ws.Cell(r, 8).GetString().Trim();
            string unavailSlots = ws.Cell(r, 9).GetString().Trim();
            bool isSplit = split.Equals("A/B", StringComparison.OrdinalIgnoreCase);
            if (isSplit && string.IsNullOrEmpty(teacherB))
                throw new InvalidOperationException($"Row {r}: split requires TeacherB.");
            if (!isSplit && !string.IsNullOrEmpty(teacherB))
                throw new InvalidOperationException($"Row {r}: TeacherB without split flag.");
            rows.Add(new LoadRow(cls, subj, hours, teacher, isSplit,
                string.IsNullOrEmpty(teacherB) ? null : teacherB,
                string.IsNullOrEmpty(room) ? null : room,
                string.IsNullOrEmpty(unavailDays) ? null : unavailDays,
                string.IsNullOrEmpty(unavailSlots) ? null : unavailSlots));
            r++;
        }
        return rows;
    }
}
