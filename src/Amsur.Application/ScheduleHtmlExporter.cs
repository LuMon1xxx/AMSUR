using System.Text;
using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Application;

// B4 — HTML-выгрузка АКТИВНОГО расписания (просмотр и печать на старых ПК
// без Excel: файл открывается в любом браузере, печать — через браузер).
// Тот же контракт, что ScheduleExcelExporter.ExportGrid: gate INV-01
// (при Hard>0 — отказ исключением, ничего не пишется), та же сетка
// «класс-блоки × дни × слоты», тот же текст клеток «Предмет · Учитель».

public static class ScheduleHtmlExporter
{
    public static string ExportHtml(
        SchedulingProblem problem,
        IReadOnlyList<PlacedLesson> placements,
        string? title = null)
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

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"ru\">\n<head>\n<meta charset=\"utf-8\">\n<title>");
        sb.Append(H(title ?? "Расписание"));
        sb.Append("</title>\n<style>\n");
        sb.Append("body{font-family:'Segoe UI',Arial,sans-serif;margin:24px;color:#111}\n");
        sb.Append("h1{font-size:22px;margin:0 0 16px}\n");
        sb.Append("h2{font-size:17px;margin:20px 0 8px}\n");
        sb.Append("table{border-collapse:collapse;width:100%;margin-bottom:8px}\n");
        sb.Append("th,td{border:1px solid #999;padding:4px 8px;text-align:left;font-size:14px}\n");
        sb.Append("th{background:#eee}\n");
        sb.Append(".class-block{page-break-inside:avoid}\n");
        sb.Append("@media print{body{margin:8px}h2{page-break-after:avoid}}\n");
        sb.Append("</style>\n</head>\n<body>\n<h1>");
        sb.Append(H(title ?? "Расписание"));
        sb.Append("</h1>\n");

        foreach (var g in byClass)
        {
            int anchor = StudentCompactness.AnchorFor(problem, g.Key);
            int bandSize = BandSize(problem, g.Key, anchor);
            string shift = anchor <= 1 ? "1-я смена" : "2-я смена";
            sb.Append("<div class=\"class-block\">\n<h2>Класс ");
            sb.Append(H(ClassName(problem, g.Key)));
            sb.Append(" · ");
            sb.Append(H(shift));
            sb.Append("</h2>\n<table>\n<tr><th>Урок</th>");
            for (int d = 0; d < problem.DaysCount; d++)
            {
                sb.Append("<th>");
                sb.Append(H(ScheduleExcelExporter.DayName(d)));
                sb.Append("</th>");
            }
            sb.Append("</tr>\n");
            var byTime = g.GroupBy(p => (p.DayIndex, p.SlotIndex))
                .ToDictionary(x => x.Key, x => x.ToList());
            for (int rel = 1; rel <= bandSize; rel++)
            {
                int slot = anchor + rel - 1;
                sb.Append("<tr><td>Урок ");
                sb.Append(rel);
                sb.Append("</td>");
                for (int d = 0; d < problem.DaysCount; d++)
                {
                    sb.Append("<td>");
                    if (byTime.TryGetValue((d, slot), out var cell))
                        sb.Append(H(string.Join(" / ",
                            cell.Select(p => CellText(problem, occById[p.OccurrenceId])))));
                    sb.Append("</td>");
                }
                sb.Append("</tr>\n");
            }
            sb.Append("</table>\n</div>\n");
        }
        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    public static void ExportHtml(
        SchedulingProblem problem,
        IReadOnlyList<PlacedLesson> placements,
        Stream destination,
        string? title = null)
    {
        string html = ExportHtml(problem, placements, title);
        using var writer = new StreamWriter(destination, new UTF8Encoding(false), 1024, leaveOpen: true);
        writer.Write(html);
        writer.Flush();
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
        // Как в Excel: не больше 7 строк (смена), минимум 1.
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

    private static string H(string s)
    {
        // Минимальное экранирование (читаемый UTF-8 + meta charset):
        // WebUtility.HtmlEncode здесь не годится — он превращает кириллицу
        // в &#NNN;-сущности, HTML становится нечитаемым.
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Replace("&", "&amp;").Replace("<", "&lt;")
            .Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
