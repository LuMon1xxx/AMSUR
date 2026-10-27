namespace Amsur.Wpf.Views;

// Ячейки матрицы расписания: ключ (день, слот) → ВСЕ занятия клетки.
// Сплит A/B (sync-группа, INV-03) штатно даёт два занятия одного класса в одной
// клетке, вид «кабинет» — тоже; поэтому значение — список, а не одна запись.
// Раньше BuildMatrix строил здесь ToDictionary и падал ArgumentException на дубле
// ключа. Вынесено отдельно, чтобы поведение было покрыто тестом без UI-сессии.
public static class ScheduleMatrixCells
{
    public static Dictionary<(int Day, int Slot), List<ScheduleRowVm>> Group(
        IEnumerable<ScheduleRowVm> rows) =>
        rows.GroupBy(r => (r.DayIndex, r.Slot))
            .ToDictionary(g => g.Key, g => g.ToList());

    /// <summary>
    /// Занятия клетки (день, слот). Слоты в движке 1-based (GapUtils.cs:5,
    /// ProblemBuilder: Range(1, SlotsPerDay)), поэтому клетки с slot=0 не существует:
    /// строка «1 урок» — это slot=1.
    /// Регресс 16.10.2026 (CRITICAL): BuildMatrix искал клетку 0-based индексом цикла,
    /// из-за чего строка «1 урок» всегда была пуста, каждый урок рисовался на строку
    /// ниже своей нумерации, а последний урок дня не отображался вовсе.
    /// </summary>
    public static List<ScheduleRowVm>? Cell(
        Dictionary<(int Day, int Slot), List<ScheduleRowVm>> byCell, int day, int slot) =>
        byCell.TryGetValue((day, slot), out var list) ? list : null;
}
