using Amsur.Wpf.Views;

namespace Amsur.Tests;

// Регресс на падение матрицы расписания (CRITICAL, найдено ревью 16.10.2026):
// BuildMatrix строил ToDictionary по ключу (DayIndex, Slot) и падал
// ArgumentException, когда в одной клетке оказывалось больше одного занятия.
// Клетка с дублем достижима штатно: сплит A/B — это sync-группа, то есть два
// занятия одного класса в одном и том же слоте (PlacementValidator: sync-equality).
public sealed class ScheduleMatrixCellsTests
{
    private static ScheduleRowVm Row(int day, int slot, string teacher, string cls = "8Б") =>
        new(Guid.NewGuid(), cls, "Английский", teacher, day, slot, "101", day, null);

    [Fact]
    public void DuplicateCell_KeepsAllRows_NoThrow()
    {
        var rows = new[]
        {
            Row(0, 1, "Иванов"),   // половина A
            Row(0, 1, "Петров"),   // половина B — тот же день и слот
            Row(0, 2, "Сидоров"),
        };

        var cells = ScheduleMatrixCells.Group(rows);

        Assert.Equal(2, cells.Count);
        Assert.Equal(2, cells[(0, 1)].Count);
        Assert.Equal(["Иванов", "Петров"], cells[(0, 1)].Select(r => r.TeacherName));
        Assert.Single(cells[(0, 2)]);
    }

    [Fact]
    public void OrderWithinCell_FollowsInput()
    {
        var rows = new[] { Row(1, 3, "Первый"), Row(1, 3, "Второй"), Row(1, 3, "Третий") };

        var cells = ScheduleMatrixCells.Group(rows);

        Assert.Equal(["Первый", "Второй", "Третий"],
            cells[(1, 3)].Select(r => r.TeacherName));
    }

    [Fact]
    public void Empty_ReturnsEmpty()
    {
        Assert.Empty(ScheduleMatrixCells.Group([]));
    }
}
