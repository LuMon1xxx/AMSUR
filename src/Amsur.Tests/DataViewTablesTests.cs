using Amsur.Application;
using Amsur.Wpf;
using Amsur.Wpf.Views;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Amsur.Tests;

// Таблицы экрана «Школа и данные»: зебра из темы, номера строк,
// даблклик/Delete на нагрузке, гриды учителей и подгрупп.
// Паттерны — как в WpfShellTests/FlexP34Tests: общий UiTestHost
// (один STA-поток + один App), окно-хост за экраном для layout.
public sealed class DataViewTablesTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"amsur-dvt-{Guid.NewGuid():N}");

    public ValueTask DisposeAsync()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        return ValueTask.CompletedTask;
    }

    private static void RunSta(Func<Task> body) => UiTestHost.Run(body);

    private static Window ShowHost(object content)
    {
        var host = new Window { Content = content };
        host.WindowStartupLocation = WindowStartupLocation.Manual;
        host.Left = -10000; host.Top = -10000;
        host.Width = 1100; host.Height = 700;
        host.Show();
        host.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        return host;
    }

    private async Task<DataView> MakeDemoViewAsync()
    {
        Directory.CreateDirectory(_dir);
        UiTestHost.EnsureApp();
        var session = new AppSession(_dir);
        await session.InitAsync();
        await session.ImportLoadAsync(DemoSchoolTests.DemoRows(), days: 5, slots: 7);
        return new DataView(session);
    }

    // Attached behavior: ширина заголовка + Header = индекс + 1, отписка без падения.
    [Fact]
    public void RowNumbersBehavior_SetsHeader()
    {
        RunSta(() =>
        {
            UiTestHost.EnsureApp();
            var grid = new DataGrid { ItemsSource = new[] { "a", "b", "c" } };
            DataGridRowNumbers.SetShowRowNumbers(grid, true);
            Assert.Equal(34, grid.RowHeaderWidth);
            Assert.True(DataGridRowNumbers.GetShowRowNumbers(grid));
            var host = ShowHost(grid);
            try
            {
                grid.UpdateLayout();
                var first = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
                var third = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(2);
                Assert.NotNull(first);
                Assert.NotNull(third);
                Assert.Equal("1", first!.Header?.ToString());
                Assert.Equal("3", third!.Header?.ToString());
            }
            finally
            {
                host.Close();
            }
            DataGridRowNumbers.SetShowRowNumbers(grid, false);
            Assert.False(DataGridRowNumbers.GetShowRowNumbers(grid));
            return Task.CompletedTask;
        });
    }

    // Все гриды DataView: зебра из темы (AlternationCount=2) + номера строк.
    [Fact]
    public void DataViewGrids_HaveZebraAndNumbers()
    {
        RunSta(async () =>
        {
            var view = await MakeDemoViewAsync();
            DataGrid[] grids =
            [
                view.LoadGrid, view.NormsGrid, view.OverridesGrid, view.ClassesGrid,
                view.AssignGrid, view.SubjectsGrid, view.RoomsGrid,
                view.TeachersGrid, view.GroupsGrid,
            ];
            foreach (var g in grids)
            {
                Assert.NotNull(g);
                Assert.Equal(2, g.AlternationCount);
                Assert.True(DataGridRowNumbers.GetShowRowNumbers(g));
            }
            // Сортировка кликом не запрещена (дефолт DataGrid).
            Assert.True(view.LoadGrid.CanUserSortColumns);
            Assert.True(view.TeachersGrid.CanUserSortColumns);
            Assert.True(view.GroupsGrid.CanUserSortColumns);
            // Мультиселект мышью/Shift на нагрузке не запрещён.
            Assert.Equal(DataGridSelectionUnit.FullRow, view.LoadGrid.SelectionUnit);
            Assert.Equal(DataGridSelectionMode.Extended, view.LoadGrid.SelectionMode);
        });
    }

    // Даблклик без выбора: тот же путь «Изменить» → подсказка в ErrorCard, без падения.
    [Fact]
    public void LoadGrid_DoubleClick_WithoutSelection_ShowsHint()
    {
        RunSta(async () =>
        {
            var view = await MakeDemoViewAsync();
            Assert.Null(view.LoadGrid.SelectedItem);
            Assert.Equal(Visibility.Collapsed, view.ErrorCard.Visibility);
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Control.MouseDoubleClickEvent,
            };
            view.LoadGrid.RaiseEvent(args);
            Assert.Equal(Visibility.Visible, view.ErrorCard.Visibility);
            Assert.Contains(view.ErrorList.Items.OfType<string>(), s => s.Contains("Выберите строку"));
        });
    }

    // Delete без выбора: подсказка в ErrorCard, без падения и без диалога.
    [Fact]
    public void LoadGrid_DeleteKey_WithoutSelection_ShowsHint()
    {
        RunSta(async () =>
        {
            var view = await MakeDemoViewAsync();
            Assert.Null(view.LoadGrid.SelectedItem);
            var host = ShowHost(view);
            try
            {
                var source = PresentationSource.FromVisual(view);
                Assert.NotNull(source);
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, source!, 0, Key.Delete)
                {
                    RoutedEvent = UIElement.PreviewKeyDownEvent,
                };
                view.LoadGrid.RaiseEvent(args);
                Assert.True(args.Handled);
                Assert.Equal(Visibility.Visible, view.ErrorCard.Visibility);
                Assert.Contains(view.ErrorList.Items.OfType<string>(), s => s.Contains("Выберите строку"));
            }
            finally
            {
                host.Close();
            }
        });
    }

    // Учителя демо-школы: 10 строк (столько же, сколько TeachersList),
    // часы/предметы/классы посчитаны из curriculum.
    [Fact]
    public void TeachersGrid_ShowsDemoTeachers()
    {
        RunSta(async () =>
        {
            var view = await MakeDemoViewAsync();
            Assert.Equal(10, view.TeachersGrid.Items.Count);
            var first = (TeacherRowVm)view.TeachersGrid.Items[0];
            Assert.False(string.IsNullOrWhiteSpace(first.Name));
            Assert.True(first.HoursPerWeek > 0);
            Assert.True(first.SubjectCount > 0);
            Assert.True(first.ClassCount > 0);
        });
    }

    // Подгруппы демо-школы: A/B 8Б (единственный сплит) — по 1 строке и 3 часа.
    [Fact]
    public void GroupsGrid_ShowsDemoGroups()
    {
        RunSta(async () =>
        {
            var view = await MakeDemoViewAsync();
            Assert.Equal(2, view.GroupsGrid.Items.Count);
            var rows = view.GroupsGrid.Items.OfType<GroupRowVm>().ToList();
            Assert.All(rows, r => Assert.Equal("8Б", r.ClassName));
            Assert.Equal(["A", "B"], rows.Select(r => r.GroupName).Order().ToArray());
            Assert.All(rows, r =>
            {
                Assert.Equal(1, r.LoadRowCount);
                Assert.Equal(3, r.Hours);
            });
            // Нагрузка целиком на месте.
            Assert.Equal(56, view.LoadGrid.Items.Count);
        });
    }
}
