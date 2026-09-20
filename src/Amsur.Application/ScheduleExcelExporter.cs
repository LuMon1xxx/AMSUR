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
        Stream destination) =>
        ExportGrid(problem, placements, destination, includeTeacherSheet: true, includeRoomSheet: true);

    /// <summary>P4/R9: + опциональный лист «Учителя» (Учитель|День|Урок|Класс|Кабинет).</summary>
    public static void ExportGrid(
        SchedulingProblem problem,
        IReadOnlyList<PlacedLesson> placements,
        Stream destination,
        bool includeTeacherSheet) =>
        ExportGrid(problem, placements, destination, includeTeacherSheet, includeRoomSheet: true);

    /// <summary>Три вида из ТЗ §22: классы (обязат.) + учителя + кабинеты (опц.).</summary>
    public static void ExportGrid(
        SchedulingProblem problem,
        IReadOnlyList<PlacedLesson> placements,
        Stream destination,
        bool includeTeacherSheet,
        bool includeRoomSheet)
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
        if (includeTeacherSheet)
            WriteTeacherSheet(wb, problem, occById, placements);
        if (includeRoomSheet)
            WriteRoomSheet(wb, problem, occById, placements);
        wb.SaveAs(destination);
    }

    // R9: расписание учителей — каким уроком в какой день какой класс.
    private static void WriteTeacherSheet(
        XLWorkbook wb,
        SchedulingProblem problem,
        IReadOnlyDictionary<Guid, LessonOccurrence> occById,
        IReadOnlyList<PlacedLesson> placements)
    {
        var ws = wb.Worksheets.Add("Учителя");
        ws.Cell(1, 1).Value = "Учитель";
        ws.Cell(1, 2).Value = "День";
        ws.Cell(1, 3).Value = "Урок";
        ws.Cell(1, 4).Value = "Класс";
        ws.Cell(1, 5).Value = "Предмет";
        ws.Cell(1, 6).Value = "Кабинет";
        ws.Row(1).Style.Font.Bold = true;
        int row = 2;
        foreach (var p in placements
                     .Where(x => occById.ContainsKey(x.OccurrenceId))
                     .OrderBy(x => TeacherName(problem, occById[x.OccurrenceId].TeacherId))
                     .ThenBy(x => x.DayIndex).ThenBy(x => x.SlotIndex))
        {
            var occ = occById[p.OccurrenceId];
            ws.Cell(row, 1).Value = TeacherName(problem, occ.TeacherId);
            ws.Cell(row, 2).Value = DayName(p.DayIndex);
            ws.Cell(row, 3).Value = p.SlotIndex;
            ws.Cell(row, 4).Value = ClassName(problem, occ.ClassId);
            ws.Cell(row, 5).Value = problem.Subjects.TryGetValue(occ.SubjectId, out var s) ? s.Name : "?";
            ws.Cell(row, 6).Value = p.RoomId.HasValue &&
                problem.Rooms.TryGetValue(p.RoomId.Value, out var r) ? r.Name : "—";
            row++;
        }
        ws.Columns().AdjustToContents();
    }

    // Три вида из ТЗ §22: расписание кабинетов — какой класс каким уроком занят.
    private static void WriteRoomSheet(
        XLWorkbook wb,
        SchedulingProblem problem,
        IReadOnlyDictionary<Guid, LessonOccurrence> occById,
        IReadOnlyList<PlacedLesson> placements)
    {
        var ws = wb.Worksheets.Add("Кабинеты");
        ws.Cell(1, 1).Value = "Кабинет";
        ws.Cell(1, 2).Value = "День";
        ws.Cell(1, 3).Value = "Урок";
        ws.Cell(1, 4).Value = "Класс";
        ws.Cell(1, 5).Value = "Предмет";
        ws.Cell(1, 6).Value = "Учитель";
        ws.Row(1).Style.Font.Bold = true;
        int row = 2;
        foreach (var p in placements
                     .Where(x => occById.ContainsKey(x.OccurrenceId))
                     .OrderBy(x => RoomName(problem, x.RoomId))
                     .ThenBy(x => x.DayIndex).ThenBy(x => x.SlotIndex))
        {
            var occ = occById[p.OccurrenceId];
            ws.Cell(row, 1).Value = RoomName(problem, p.RoomId);
            ws.Cell(row, 2).Value = DayName(p.DayIndex);
            ws.Cell(row, 3).Value = p.SlotIndex;
            ws.Cell(row, 4).Value = ClassName(problem, occ.ClassId);
            ws.Cell(row, 5).Value = problem.Subjects.TryGetValue(occ.SubjectId, out var s) ? s.Name : "?";
            ws.Cell(row, 6).Value = TeacherName(problem, occ.TeacherId);
            row++;
        }
        ws.Columns().AdjustToContents();
    }

    private static string RoomName(SchedulingProblem problem, Guid? roomId) =>
        roomId.HasValue && problem.Rooms.TryGetValue(roomId.Value, out var r) ? r.Name : "—";

    /// <summary>
    /// ЧЕРНОВИК (D-42): выгрузка частичного расписания. Gate ослаблен честно:
    /// полнота/окна/поздний старт на частичных данных бессмысленны и исключены;
    /// всё остальное (коллизии учителей/классов/кабинетов, смены, кэпы, sync) —
    /// отказ как обычно. Неназначенные уроки — отдельным листом «Неназначенные».
    /// Файл помечен баннером ЧЕРНОВИК. UI-проводка — backlog.
    /// </summary>
    public static void ExportDraftGrid(
        SchedulingProblem problem,
        IReadOnlyList<PlacedLesson> placements,
        IReadOnlyList<Guid> unplacedOccurrenceIds,
        Stream destination,
        bool includeTeacherSheet = true,
        bool includeRoomSheet = true)
    {
        var validation = PlacementValidator.Validate(problem, placements);
        var blocking = validation.HardViolations
            .Where(v => v.Code is not ("placement-count" or "student-gap" or "student-late-start"))
            .ToList();
        if (blocking.Count > 0)
            throw new InvalidOperationException(
                $"Черновик отклонён: {blocking.Count} жёстких нарушений " +
                $"({string.Join(", ", blocking.Take(3).Select(b => b.Code))}).");

        var placedIds = placements.Select(p => p.OccurrenceId).ToHashSet();
        var unplaced = unplacedOccurrenceIds
            .Distinct()
            .Where(id => !placedIds.Contains(id) && problem.Occurrences.Any(o => o.Id == id))
            .Select(id => problem.Occurrences.First(o => o.Id == id))
            .ToList();

        var occById = problem.Occurrences.ToDictionary(o => o.Id);
        var byClass = placements
            .GroupBy(p => occById[p.OccurrenceId].ClassId)
            .OrderBy(g => ClassName(problem, g.Key))
            .ToList();

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Расписание");
        int row = 1;
        ws.Cell(row, 1).Value =
            $"ЧЕРНОВИК: размещено {placements.Count} из {problem.Occurrences.Count} уроков " +
            $"({unplaced.Count} не назначено — см. лист «Неназначенные»).";
        ws.Cell(row, 1).Style.Font.Bold = true;
        row += 2;
        foreach (var g in byClass)
        {
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
            row++;
        }
        ws.Columns().AdjustToContents();
        if (includeTeacherSheet)
            WriteTeacherSheet(wb, problem, occById, placements);
        if (includeRoomSheet)
            WriteRoomSheet(wb, problem, occById, placements);

        var un = wb.Worksheets.Add("Неназначенные");
        un.Cell(1, 1).Value = "Класс";
        un.Cell(1, 2).Value = "Предмет";
        un.Cell(1, 3).Value = "Учитель";
        un.Cell(1, 4).Value = "Подгруппа";
        un.Row(1).Style.Font.Bold = true;
        int ur = 2;
        foreach (var o in unplaced
                     .OrderBy(o => ClassName(problem, o.ClassId))
                     .ThenBy(o => problem.Subjects.TryGetValue(o.SubjectId, out var s) ? s.Name : "?"))
        {
            un.Cell(ur, 1).Value = ClassName(problem, o.ClassId);
            un.Cell(ur, 2).Value = problem.Subjects.TryGetValue(o.SubjectId, out var s) ? s.Name : "?";
            un.Cell(ur, 3).Value = TeacherName(problem, o.TeacherId);
            un.Cell(ur, 4).Value = o.GroupId.HasValue ? "подгруппа" : "весь класс";
            ur++;
        }
        un.Columns().AdjustToContents();
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

    private static string TeacherName(SchedulingProblem problem, Guid teacherId) =>
        problem.Teachers.TryGetValue(teacherId, out var t) ? t.Name : "?";

    /// <summary>
    /// A2: персональное расписание учителя (дни × уроки, класс·предмет·кабинет).
    /// Gate тот же (только для принятых/чистых размещений); черновики — через ExportDraftGrid.
    /// </summary>
    public static void ExportTeacherGrid(
        SchedulingProblem problem,
        IReadOnlyList<PlacedLesson> placements,
        Guid teacherId,
        Stream destination)
    {
        var validation = PlacementValidator.Validate(problem, placements);
        if (!validation.IsValid)
            throw new InvalidOperationException(
                $"Экспорт отклонён: {validation.HardViolations.Count} жёстких нарушений.");

        var mine = placements
            .Where(p => problem.Occurrences.Any(o => o.Id == p.OccurrenceId && o.TeacherId == teacherId))
            .ToList();
        var occById = problem.Occurrences.ToDictionary(o => o.Id);
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Расписание");
        ws.Cell(1, 1).Value = $"Учитель {TeacherName(problem, teacherId)}";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(2, 1).Value = "Урок";
        for (int d = 0; d < problem.DaysCount; d++)
            ws.Cell(2, 2 + d).Value = DayName(d);
        var byTime = mine.GroupBy(p => (p.DayIndex, p.SlotIndex))
            .ToDictionary(x => x.Key, x => x.ToList());
        int maxSlot = mine.Count == 0 ? 0 : mine.Max(p => p.SlotIndex);
        int row = 3;
        for (int slot = 1; slot <= maxSlot; slot++)
        {
            ws.Cell(row, 1).Value = $"Урок {slot}";
            for (int d = 0; d < problem.DaysCount; d++)
            {
                if (byTime.TryGetValue((d, slot), out var cell))
                    ws.Cell(row, 2 + d).Value = string.Join(" / ",
                        cell.Select(p => TeachCellText(problem, occById[p.OccurrenceId], p.RoomId)));
            }
            row++;
        }
        ws.Columns().AdjustToContents();
        wb.SaveAs(destination);
    }

    private static string TeachCellText(
        SchedulingProblem problem, LessonOccurrence occ, Guid? roomId)
    {
        string subj = problem.Subjects.TryGetValue(occ.SubjectId, out var s) ? s.Name : "?";
        string cls = ClassName(problem, occ.ClassId);
        string room = RoomName(problem, roomId);
        return $"{subj} · {cls} · {room}";
    }
}
