using Amsur.Domain;
using ClosedXML.Excel;
using Amsur.Scheduling.Core;

namespace Amsur.Application;

// E9 — выгрузка АКТИВНОГО расписания в Excel (сетка по классам).
// Gate INV-01: при Hard>0 — отказ исключением, файл не пишется.
// Только ClosedXML (уже в проекте); PDF/QuestPDF — не scope (D5).

public static class ScheduleExcelExporter
{
    public static readonly string[] DayNames = ["Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс"];

    public static string DayName(int dayIndex) =>
        dayIndex >= 0 && dayIndex < DayNames.Length ? DayNames[dayIndex] : $"День {dayIndex + 1}";

    public static void ExportGrid(
        SchedulingProblem problem,
        IReadOnlyList<PlacedLesson> placements,
        Stream destination)
    {
        var validation = PlacementValidator.Validate(problem, placements);
        if (!validation.IsValid)
            throw new InvalidOperationException(
                $"Экспорт отклонён: {validation.HardViolations.Count} жёстких нарушений.");

        var occById = problem.Occurrences.ToDictionary(o => o.Id);
        var byClass = placements
            .GroupBy(p => occById[p.OccurrenceId].ClassId)
            .OrderBy(g => ClassName(problem, g.Key))
            .ToList();

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Расписание");
        int row = 1;
        foreach (var g in byClass)
        {
            // Бэнд смены класса: показываем только свои слоты с относительной нумерацией 1..N.
            int anchor = StudentCompactness.AnchorFor(problem, g.Key);
            int bandSize = BandSize(problem, g.Key, anchor);
            string shift = anchor <= 1 ? "1-я смена" : "2-я смена";
            ws.Cell(row, 1).Value = $"Класс {ClassName(problem, g.Key)} · {shift}";
            ws.Cell(row, 1).Style.Font.Bold = true;
            row++;
            ws.Cell(row, 1).Value = "Урок";
            for (int d = 0; d < problem.DaysCount; d++)
                ws.Cell(row, 2 + d).Value = DayName(d);
            row++;
            var byTime = g.GroupBy(p => (p.DayIndex, p.SlotIndex))
                .ToDictionary(x => x.Key, x => x.ToList());
            for (int rel = 1; rel <= bandSize; rel++)
            {
                int slot = anchor + rel - 1;
                ws.Cell(row, 1).Value = $"Урок {rel}";
                for (int d = 0; d < problem.DaysCount; d++)
                {
                    if (byTime.TryGetValue((d, slot), out var cell))
                        ws.Cell(row, 2 + d).Value = string.Join(" / ",
                            cell.Select(p => CellText(problem, occById[p.OccurrenceId])));
                }
                row++;
            }
            row++; // пустая строка между классами
        }
        ws.Columns().AdjustToContents();
        wb.SaveAs(destination);
    }

    private static int BandSize(SchedulingProblem problem, Guid classId, int anchor)
    {
        int max = anchor;
        foreach (var occ in problem.Occurrences)
        {
            if (occ.ClassId != classId) continue;
            if (problem.AllowedSlots.TryGetValue(occ.Id, out var slots))
                foreach (int s in slots)
                    if (s > max) max = s;
        }
        // Не больше 7 строк (смена), минимум 1.
        return Math.Clamp(max - anchor + 1, 1, 7);
    }

    private static string CellText(SchedulingProblem problem, LessonOccurrence occ)
    {
        string subj = problem.Subjects.TryGetValue(occ.SubjectId, out var s) ? s.Name : "?";
        string teacher = problem.Teachers.TryGetValue(occ.TeacherId, out var t) ? t.Name : "?";
        return $"{subj} · {teacher}";
    }

    private static string ClassName(SchedulingProblem problem, Guid classId) =>
        problem.Classes.TryGetValue(classId, out var c) ? c.Name : "?";
}
