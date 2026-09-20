using System.Text;
using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Application;

// P-CSV — выгрузка активного расписания в CSV (мост к «Электронной школе»/РИОС
// и любым внешним системам: плоская таблица без формул).
// Тот же контракт: gate INV-01 (Hard>0 — отказ), имена вместо Id.
// Формат: Class;Day;Slot;Subject;Teacher;Room (UTF-8 с BOM для Excel, ';' —
// дефолтный разделитель русской локали Excel).

public static class ScheduleCsvExporter
{
    public static string ExportCsv(
        SchedulingProblem problem,
        IReadOnlyList<PlacedLesson> placements)
    {
        var validation = PlacementValidator.Validate(problem, placements);
        if (!validation.IsValid)
            throw new InvalidOperationException(
                $"Экспорт отклонён: {validation.HardViolations.Count} жёстких нарушений.");

        var occById = problem.Occurrences.ToDictionary(o => o.Id);
        var sb = new StringBuilder();
        sb.Append("Class;Day;Slot;Subject;Teacher;Room\n");
        foreach (var p in placements
                     .Where(x => occById.ContainsKey(x.OccurrenceId))
                     .OrderBy(x => ClassName(problem, occById[x.OccurrenceId].ClassId))
                     .ThenBy(x => x.DayIndex).ThenBy(x => x.SlotIndex))
        {
            var occ = occById[p.OccurrenceId];
            sb.Append(Cell(ClassName(problem, occ.ClassId))).Append(';');
            sb.Append(Cell(ScheduleExcelExporter.DayName(p.DayIndex))).Append(';');
            sb.Append(p.SlotIndex).Append(';');
            sb.Append(Cell(problem.Subjects.TryGetValue(occ.SubjectId, out var s) ? s.Name : "?")).Append(';');
            sb.Append(Cell(problem.Teachers.TryGetValue(occ.TeacherId, out var t) ? t.Name : "?")).Append(';');
            sb.Append(Cell(p.RoomId.HasValue &&
                problem.Rooms.TryGetValue(p.RoomId.Value, out var r) ? r.Name : "—"));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    public static void ExportCsv(
        SchedulingProblem problem,
        IReadOnlyList<PlacedLesson> placements,
        Stream destination)
    {
        string csv = ExportCsv(problem, placements);
        using var writer = new StreamWriter(destination, new UTF8Encoding(true), 1024, leaveOpen: true);
        writer.Write(csv);
        writer.Flush();
    }

    // CSV-экранирование: кавычки/точка с запятой/перенос — в кавычки с удвоением.
    private static string Cell(string s) =>
        s.Contains(';') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;

    private static string ClassName(SchedulingProblem problem, Guid classId) =>
        problem.Classes.TryGetValue(classId, out var c) ? c.Name : "?";
}
