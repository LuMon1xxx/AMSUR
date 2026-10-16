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
}
