using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Amsur.Application;
using Amsur.Domain;
using Amsur.Infrastructure;
using Amsur.Scheduling.Core;
using Amsur.Wpf;
using Amsur.Wpf.Views;
using Microsoft.Data.Sqlite;

namespace Amsur.Tests;

public sealed class Stage2DataEditorTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"amsur-s2-{Guid.NewGuid():N}");

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
        }
        catch
        {
            // temp cleanup best-effort
        }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task FlexStore_SubjectLimitsAndTeacherUnavailability_PersistAcrossReopen()
    {
        Directory.CreateDirectory(_dir);
        string db = $"Data Source={Path.Combine(_dir, "flex.db")}";
        var src = FlexDataset.Empty with
        {
            SubjectLimits =
            [
                new SubjectLimitRow("Математика", 2),
                new SubjectLimitRow("Физика", 1),
            ],
            TeacherDaysOff =
            [
                new TeacherDayOffRow("Иванова", 0),
                new TeacherDayOffRow("Петров", 4),
            ],
            TeacherUnavailSlots =
            [
                new TeacherUnavailSlotRow("Иванова", 1, 2),
                new TeacherUnavailSlotRow("Иванова", 2, 5),
                new TeacherUnavailSlotRow("Петров", 0, 1),
            ],
        };

        var store = new SqliteFlexStore(db);
        await store.InitializeAsync();
        await store.SaveAsync(src);

        var reopened = new SqliteFlexStore(db);
        await reopened.InitializeAsync();
        var got = await reopened.LoadAsync();

        Assert.Equal(2, got.SubjectLimits!.Count);
        Assert.Equal(1, Assert.Single(got.SubjectLimits!, x => x.SubjectName == "Физика").MaxPerDay);
        Assert.Equal(2, got.TeacherDaysOff!.Count);
        Assert.Equal(0, Assert.Single(got.TeacherDaysOff!, x => x.TeacherName == "Иванова").DayIndex);
        Assert.Equal(3, got.TeacherUnavailSlots!.Count);
        Assert.Contains(got.TeacherUnavailSlots!, x => x.TeacherName == "Иванова" && x.DayIndex == 1 && x.SlotIndex == 2);
    }

    [Fact]
    public void Importer_AppliesSubjectLimitsAndFlexTeacherUnavailability_EnforcedInProblemBuilder()
    {
        var rows = new List<LoadRow>
        {
            new("5А", "Математика", 4, "Иванова", false, null, "Каб 1"),
            new("5А", "История Беларуси", 2, "Сидорова", false, null, "Каб 1"),
        };
        var flex = FlexDataset.Empty with
        {
            SubjectLimits = [new SubjectLimitRow("Математика", 1)],
            TeacherDaysOff = [new TeacherDayOffRow("Иванова", 0)],
            TeacherUnavailSlots =
            [
                new TeacherUnavailSlotRow("Иванова", 1, 2),
                new TeacherUnavailSlotRow("Иванова", 2, 2),
                new TeacherUnavailSlotRow("Иванова", 3, 2),
                new TeacherUnavailSlotRow("Иванова", 4, 2),
            ],
        };

        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows, daysCount: 5, slotsPerDay: 7, flex: flex);
        var math = Assert.Single(data.Subjects, s => s.Name == "Математика");
        var ivanova = Assert.Single(data.Teachers, t => t.Name == "Иванова");
        Assert.Equal(1, math.MaxPerDay);
        Assert.Contains(data.DaysOff, d => d.TeacherId == ivanova.Id && d.DayIndex == 0);
        Assert.Contains(data.Unavailability, u => u.TeacherId == ivanova.Id && u.DayIndex == 1 && u.SlotIndex == 2);

        var (problem, errors) = ProblemBuilder.Build(data.ToProblemInput());
        Assert.Empty(errors);
        Assert.NotNull(problem);
        Assert.Equal(1, problem!.Subjects[math.Id].MaxPerDay);

        var mathOccs = problem.Occurrences.Where(o => o.SubjectId == math.Id).ToList();
        Assert.Equal(4, mathOccs.Count);
        Assert.All(mathOccs, occ =>
        {
            Assert.DoesNotContain(0, problem.AllowedDays[occ.Id]);
            Assert.DoesNotContain(2, problem.AllowedSlots[occ.Id]);
        });
    }

    [Fact]
    public void SchoolSetupPresets_BuildStarterSchool_IsCapacityCleanAndFeasible()
    {
        // Односменка (5–9 кл., по 2 класса)
        var basicSpecs = Enumerable.Range(5, 5)
            .Select(g => new ParallelSpec(g, ["А", "Б"], Shift: 1))
            .ToList();
        var (rows1, flex1) = SchoolSetupPresets.BuildStarterSchool(basicSpecs);
        var data1 = SchoolDataImporter.Import(Guid.NewGuid(), rows1, daysCount: 5, slotsPerDay: 8, flex: flex1);
        var (prob1, errs1) = ProblemBuilder.Build(data1.ToProblemInput());
        Assert.Empty(errs1);
        Assert.NotNull(prob1);
        Assert.Empty(CapacityDiagnostics.Analyze(prob1!));

        // Двухсменка (1–11 кл., 5–7 кл. во 2-ю смену, 12 уроков в сетке)
        var fullSpecs = Enumerable.Range(1, 11)
            .Select(g => new ParallelSpec(g, g <= 9 ? ["А", "Б"] : ["А"], Shift: g is >= 5 and <= 7 ? 2 : 1))
            .ToList();
        var (rows2, flex2) = SchoolSetupPresets.BuildStarterSchool(fullSpecs);
        var data2 = SchoolDataImporter.Import(Guid.NewGuid(), rows2, daysCount: 5, slotsPerDay: 12, flex: flex2);
        var (prob2, errs2) = ProblemBuilder.Build(data2.ToProblemInput());
        Assert.Empty(errs2);
        Assert.NotNull(prob2);
        Assert.Empty(CapacityDiagnostics.Analyze(prob2!));
    }

    [Fact]
    public void SchoolSetupPresets_RenameTeacher_UpdatesLoadAndFlexAtomically()
    {
        var rows = new List<LoadRow>
        {
            new("5А", "Математика", 4, "Иванова", false, null, "101"),
            new("5А", "Иностранный язык", 3, "Петрова", true, "Иванова", "102"),
        };
        var flex = FlexDataset.Empty with
        {
            Classes = [new ClassConfigRow("5А", "Иванова", 5, 25)],
            Assignments = [new TeacherAssignRow("Иванова", "Математика", AssignmentScope.Class, "5А", null)],
            TeacherLimits = [new TeacherLimitRow("Иванова", 5)],
            TeacherDaysOff = [new TeacherDayOffRow("Иванова", 2)],
            TeacherUnavailSlots = [new TeacherUnavailSlotRow("Иванова", 1, 3)],
        };

        var (updatedRows, updatedFlex) = SchoolSetupPresets.RenameTeacher(rows, flex, "Иванова", "Козлова И.В.");
        Assert.Equal("Козлова И.В.", updatedRows[0].TeacherName);
        Assert.Equal("Козлова И.В.", updatedRows[1].SplitTeacherBName);
        Assert.Equal("Козлова И.В.", Assert.Single(updatedFlex.Classes).ClassTeacherName);
        Assert.Equal("Козлова И.В.", Assert.Single(updatedFlex.Assignments).TeacherName);
        Assert.Equal("Козлова И.В.", Assert.Single(updatedFlex.TeacherLimits!).TeacherName);
        Assert.Equal("Козлова И.В.", Assert.Single(updatedFlex.TeacherDaysOff!).TeacherName);
        Assert.Equal("Козлова И.В.", Assert.Single(updatedFlex.TeacherUnavailSlots!).TeacherName);

        Assert.Throws<InvalidOperationException>(() =>
            SchoolSetupPresets.RenameTeacher(rows, flex, "Иванова", "Петрова"));
    }

    [Fact]
    public void DataView_LimitsAndUnavailability_SaveViaSaveAllBtn()
    {
        UiTestHost.Run(async () =>
        {
            Directory.CreateDirectory(_dir);
            UiTestHost.EnsureApp();
            var session = new AppSession(_dir);
            await session.InitAsync();
            await session.ImportLoadAsync(DemoSchoolTests.DemoRows(), days: 5, slots: 7);

            var view = new DataView(session);
            var host = ShowHost(view);
            try
            {
                var cls0 = view.ClassesGrid.Items.OfType<ClassRowVm>().First();
                cls0.MaxLessonsPerDay = 4;

                var teacher0 = view.TeachersGrid.Items.OfType<TeacherRowVm>().First();
                teacher0.MaxLessonsPerDay = 4;
                view.TeachersGrid.SelectedItem = teacher0;
                FlushUi(host);

                var subj0 = view.SubjectsGrid.Items.OfType<SubjectRowVm>().First();
                subj0.MaxPerDay = 1;

                var daysOffPanel = (StackPanel)view.FindName("TeacherDaysOffPanel")!;
                var monBtn = daysOffPanel.Children.OfType<Button>().First();
                monBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                FlushUi(host);

                var slotGrid = (Grid)view.FindName("TeacherScheduleGrid")!;
                var enabledSlotBtn = slotGrid.Children.OfType<Button>().First(b => b.IsEnabled);
                enabledSlotBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                FlushUi(host);

                view.SaveAllBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                for (int i = 0; i < 60 && (session.Flex.SubjectLimits?.Count ?? 0) == 0; i++)
                {
                    await Task.Delay(50);
                    FlushUi(host);
                }

                Assert.Equal(4, Assert.Single(session.Data!.Classes, c => c.Name == cls0.Name).MaxLessonsPerDay);
                Assert.Equal(4, Assert.Single(session.Data!.Teachers, t => t.Name == teacher0.Name).MaxLessonsPerDay);
                Assert.Equal(1, Assert.Single(session.Data!.Subjects, s => s.Name == subj0.Name).MaxPerDay);
                Assert.Contains(session.Flex.TeacherDaysOff!, d => d.TeacherName == teacher0.Name && d.DayIndex == 0);
                Assert.NotEmpty(session.Flex.TeacherUnavailSlots!);

                var reopened = new AppSession(_dir);
                await reopened.InitAsync();
                Assert.Equal(1, Assert.Single(reopened.Data!.Subjects, s => s.Name == subj0.Name).MaxPerDay);
                Assert.Contains(reopened.Flex.TeacherDaysOff!, d => d.TeacherName == teacher0.Name && d.DayIndex == 0);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void DataView_CapacityBanner_ShowsWhenClassOverloaded()
    {
        UiTestHost.Run(async () =>
        {
            Directory.CreateDirectory(_dir);
            UiTestHost.EnsureApp();
            var session = new AppSession(_dir);
            await session.InitAsync();

            var rows = new List<LoadRow>
            {
                new("5А", "Математика", 8, "Учитель 1", false, null, null),
                new("5А", "Русский язык", 8, "Учитель 2", false, null, null),
                new("5А", "История Беларуси", 8, "Учитель 3", false, null, null),
            };
            await session.ImportLoadAsync(rows, days: 5, slots: 4);

            var view = new DataView(session);
            Assert.Equal(Visibility.Visible, view.CapacityBanner.Visibility);
            var list = (ItemsControl)view.FindName("CapacityList")!;
            Assert.NotEmpty(list.Items.OfType<string>());
        });
    }

    [Fact]
    public void LoadRowWindow_ConstructsWithoutApp_AndSupportsPairAndComboBoxes()
    {
        UiTestHost.Run(() =>
        {
            var initial = new LoadRow("10А", "Английский язык", 3, "Петрова", true, "Иванова", "205",
                PairName: "Иностр/Инф", Shift: 1);
            var win = new LoadRowWindow(initial, ["10А"], ["Английский язык"], ["Петрова", "Иванова"], ["205"]);
            Assert.NotNull(win.ShiftBox);

            var pairBox = (TextBox)win.FindName("PairBox")!;
            var classBox = (ComboBox)win.FindName("ClassBox")!;
            Assert.Equal("Иностр/Инф", pairBox.Text);
            Assert.True(classBox.IsEditable);
            Assert.Equal("10А", classBox.Text);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void SchoolWizardWindow_ConstructsAndBuildsValidSchool()
    {
        UiTestHost.Run(() =>
        {
            UiTestHost.EnsureApp();
            var win = new SchoolWizardWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000,
                ShowInTaskbar = false,
            };

            win.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                var presetBtn = FindVisualChildren<Button>(win)
                    .First(b => b.Content is string s && s == "Базовая (5–9)");
                presetBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var createBtn = (Button)win.FindName("CreateBtn")!;
                createBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            });

            bool? ok = win.ShowDialog();
            Assert.True(ok);
            Assert.NotEmpty(win.ResultRows);
            Assert.Equal(10, win.ResultFlex.Classes.Count);
            Assert.Equal(5, win.ResultDays);
            Assert.Equal(8, win.ResultSlots);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task SqliteFlexStore_LegacyDbWithoutStage2Tables_LoadsEmptyStage2Lists()
    {
        Directory.CreateDirectory(_dir);
        string dbPath = Path.Combine(_dir, "legacy.db");
        string cs = $"Data Source={dbPath}";

        // Инициализируем актуальную схему, затем удаляем таблицы Stage 2, имитируя старую БД Stage 1.
        var initial = new SqliteFlexStore(cs);
        await initial.InitializeAsync();
        await initial.SaveAsync(FlexDataset.Empty with
        {
            Classes = [new ClassConfigRow("5А", null, 5, 25)],
        });

        await using (var conn = new SqliteConnection(cs))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DROP TABLE IF EXISTS SubjectLimit;
                DROP TABLE IF EXISTS TeacherDayOff;
                DROP TABLE IF EXISTS TeacherUnavailSlot;
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var store = new SqliteFlexStore(cs);
        await store.InitializeAsync();
        var got = await store.LoadAsync();
        Assert.Single(got.Classes);
        Assert.Empty(got.SubjectLimits!);
        Assert.Empty(got.TeacherDaysOff!);
        Assert.Empty(got.TeacherUnavailSlots!);
    }

    [Fact]
    public void Importer_AndDataView_MergeAndClearLegacyExcelUnavail()
    {
        UiTestHost.Run(async () =>
        {
            Directory.CreateDirectory(_dir);
            UiTestHost.EnsureApp();
            var session = new AppSession(_dir);
            await session.InitAsync();

            var rows = new List<LoadRow>
            {
                new("5А", "Математика", 3, "Иванова ", false, null, "101", UnavailDays: "1", UnavailSlots: "2,3"),
            };
            await session.ImportLoadAsync(rows, days: 5, slots: 7);
            await session.ApplyFlexAsync(session.Flex with
            {
                TeacherDaysOff = [new TeacherDayOffRow("Иванова", 4)],
                TeacherUnavailSlots = [new TeacherUnavailSlotRow("Иванова", 1, 5)],
            });

            var ivanova = Assert.Single(session.Data!.Teachers, t => t.Name == "Иванова");
            Assert.Contains(session.Data.DaysOff, d => d.TeacherId == ivanova.Id && d.DayIndex == 0);
            Assert.Contains(session.Data.DaysOff, d => d.TeacherId == ivanova.Id && d.DayIndex == 4);
            Assert.Contains(session.Data.Unavailability, u => u.TeacherId == ivanova.Id && u.DayIndex == 1 && u.SlotIndex == 3);
            Assert.Contains(session.Data.Unavailability, u => u.TeacherId == ivanova.Id && u.DayIndex == 1 && u.SlotIndex == 5);

            var view = new DataView(session);
            var host = ShowHost(view);
            try
            {
                view.TeachersGrid.SelectedItem = view.TeachersGrid.Items.OfType<TeacherRowVm>().First();
                FlushUi(host);

                var clearBtn = FindVisualChildren<Button>(view)
                    .First(b => b.Content is string s && s == "Сбросить");
                clearBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                FlushUi(host);

                view.SaveAllBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                for (int i = 0; i < 60 && session.Data!.DaysOff.Count > 0; i++)
                {
                    await Task.Delay(50);
                    FlushUi(host);
                }

                Assert.Empty(session.Data!.DaysOff);
                Assert.Empty(session.Data!.Unavailability);
                Assert.Null(session.LoadRows[0].UnavailDays);
                Assert.Null(session.LoadRows[0].UnavailSlots);

                var reopened = new AppSession(_dir);
                await reopened.InitAsync();
                Assert.Empty(reopened.Data!.DaysOff);
                Assert.Empty(reopened.Data!.Unavailability);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void DataView_HoursMatrix_EditsDeletesAndHidesFalseSuccessOnError()
    {
        UiTestHost.Run(async () =>
        {
            Directory.CreateDirectory(_dir);
            UiTestHost.EnsureApp();
            var session = new AppSession(_dir);
            await session.InitAsync();
            await session.ImportLoadAsync(
                [new LoadRow("5А", "Математика", 4, "Иванова", false, null, "101")],
                days: 5, slots: 7);

            var view = new DataView(session);
            var host = ShowHost(view);
            try
            {
                ((Button)view.FindName("LoadMatrixModeBtn")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                FlushUi(host);

                var matrixGrid = (Grid)view.FindName("HoursMatrixGrid")!;
                matrixGrid.Children.OfType<Button>().First().RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                FlushUi(host);

                var hoursBox = (TextBox)view.FindName("MatrixHoursBox")!;
                var applyBtn = (Button)view.FindName("MatrixApplyBtn")!;
                var statusBanner = (Border)view.FindName("StatusBanner")!;

                // Invalid value should trigger ErrorCard and never show green StatusBanner
                hoursBox.Text = "99";
                applyBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                FlushUi(host);
                Assert.Equal(Visibility.Visible, view.ErrorCard.Visibility);
                Assert.NotEqual(Visibility.Visible, statusBanner.Visibility);

                // Valid update
                hoursBox.Text = "5";
                applyBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                for (int i = 0; i < 40 && session.LoadRows[0].HoursPerWeek != 5; i++)
                {
                    await Task.Delay(50);
                    FlushUi(host);
                }
                Assert.Equal(5, session.LoadRows[0].HoursPerWeek);
                Assert.Equal(Visibility.Collapsed, view.ErrorCard.Visibility);
                Assert.Equal(Visibility.Visible, statusBanner.Visibility);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void DataView_AddClass_RenameTeacher_AndShiftAuto12Slots()
    {
        UiTestHost.Run(async () =>
        {
            Directory.CreateDirectory(_dir);
            UiTestHost.EnsureApp();
            var session = new AppSession(_dir);
            await session.InitAsync();
            await session.ImportLoadAsync(
                [new LoadRow("5А", "Математика", 4, "Иванова", false, null, "101")],
                days: 5, slots: 7);

            var view = new DataView(session);
            var host = ShowHost(view);
            try
            {
                ((TextBox)view.FindName("NewClassNameBox")!).Text = "5В";
                ((TextBox)view.FindName("NewClassGradeBox")!).Text = "5";
                ((Button)view.FindName("AddClassBtn")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                for (int i = 0; i < 60 && !session.Data!.Classes.Any(c => c.Name == "5В"); i++)
                {
                    await Task.Delay(50);
                    FlushUi(host);
                }
                Assert.Contains(session.Data!.Classes, c => c.Name == "5В");

                view.TeachersGrid.SelectedItem = view.TeachersGrid.Items.OfType<TeacherRowVm>().First(t => t.Name == "Иванова");
                FlushUi(host);
                ((TextBox)view.FindName("RenameTeacherBox")!).Text = "Смирнова А.А.";
                FindVisualChildren<Button>(view)
                    .First(b => b.Content is string s && s == "Переименовать")
                    .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                for (int i = 0; i < 60 && !session.Data!.Teachers.Any(t => t.Name == "Смирнова А.А."); i++)
                {
                    await Task.Delay(50);
                    FlushUi(host);
                }
                Assert.Contains(session.Data!.Teachers, t => t.Name == "Смирнова А.А.");
                Assert.DoesNotContain(session.Data!.Teachers, t => t.Name == "Иванова");

                view.ClassesGrid.Items.OfType<ClassRowVm>().First(c => c.Name == "5В").ShiftLabel = "2-я";
                view.SaveAllBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                for (int i = 0; i < 60 && session.Data!.SlotsPerDay != 12; i++)
                {
                    await Task.Delay(50);
                    FlushUi(host);
                }
                Assert.Equal(12, session.Data!.SlotsPerDay);
                Assert.Contains(session.LoadRows, r => r.ClassName == "5В" && r.Shift == 2);
            }
            finally
            {
                host.Close();
            }
        });
    }

    private static Window ShowHost(object content)
    {
        var host = new Window
        {
            Content = content,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            Width = 1280,
            Height = 760,
            ShowInTaskbar = false,
        };
        host.Show();
        FlushUi(host);
        return host;
    }

    private static void FlushUi(Window w)
    {
        w.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var sub in FindVisualChildren<T>(child)) yield return sub;
        }
    }
}

public sealed class Stage2VisualCaptureTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"amsur-s2vis-{Guid.NewGuid():N}");

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
        }
        catch
        {
            // temp cleanup best-effort
        }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void Stage2_ViewsRenderInLightAndDark_AndCaptureShotsWhenConfigured()
    {
        UiTestHost.Run(async () =>
        {
            Directory.CreateDirectory(_dir);
            var app = UiTestHost.EnsureApp();
            MainWindow? main = null;
            try
            {
                app.ApplyTheme("light");
                var session = new AppSession(_dir);
                await session.InitAsync();
                typeof(App).GetProperty("Session")!.SetValue(app, session);
                await session.ImportLoadAsync(DemoSchoolTests.DemoRows(), days: 5, slots: 7);

                string? shotsDir = Environment.GetEnvironmentVariable("AMSUR_CAPTURE_STAGE2_DIR");
                if (!string.IsNullOrWhiteSpace(shotsDir))
                    Directory.CreateDirectory(shotsDir);

                main = new MainWindow
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -10000,
                    Top = -10000,
                    ShowInTaskbar = false,
                };
                main.Show();
                await FlushAsync(main, 100);
                main.WindowState = WindowState.Normal;
                main.Width = 1536;
                main.Height = 864;
                await FlushAsync(main, 200);

                await CaptureThemeAsync(app, main, shotsDir, "light");

                var themeBox = (CheckBox)main.FindName("ThemeBox")!;
                themeBox.IsChecked = true;
                for (int i = 0; i < 20 && session.ThemeName != "dark"; i++)
                    await FlushAsync(main, 100);
                Assert.Equal("dark", session.ThemeName);

                await CaptureThemeAsync(app, main, shotsDir, "dark");
            }
            finally
            {
                main?.Close();
                app.ApplyTheme("light");
            }
        });
    }

    private static async Task CaptureThemeAsync(App app, MainWindow main, string? shotsDir, string theme)
    {
        main.NavigateTo("Data");
        await FlushAsync(main, 350);
        var dv = Assert.IsType<DataView>(main.ViewHost.Content);
        var tabList = (ListBox)dv.FindName("TabList")!;

        tabList.SelectedIndex = 0;
        ((Button)dv.FindName("LoadTableModeBtn")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await FlushAsync(main, 250);
        Capture(main, shotsDir, $"{theme}-data-load.png");

        ((Button)dv.FindName("LoadMatrixModeBtn")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await FlushAsync(main, 250);
        var matrixGrid = (Grid)dv.FindName("HoursMatrixGrid")!;
        var firstCell = matrixGrid.Children.OfType<Button>().FirstOrDefault();
        firstCell?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await FlushAsync(main, 250);
        Capture(main, shotsDir, $"{theme}-data-matrix.png");

        tabList.SelectedIndex = 2;
        await FlushAsync(main, 250);
        Capture(main, shotsDir, $"{theme}-data-classes.png");

        tabList.SelectedIndex = 3;
        await FlushAsync(main, 250);
        var daysOffPanel = (StackPanel)dv.FindName("TeacherDaysOffPanel")!;
        daysOffPanel.Children.OfType<Button>().FirstOrDefault()?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await FlushAsync(main, 150);
        var slotGrid = (Grid)dv.FindName("TeacherScheduleGrid")!;
        slotGrid.Children.OfType<Button>().Where(b => b.IsEnabled).Take(2).ToList()
            .ForEach(b => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)));
        await FlushAsync(main, 250);
        Capture(main, shotsDir, $"{theme}-data-teachers.png");

        tabList.SelectedIndex = 4;
        await FlushAsync(main, 250);
        Capture(main, shotsDir, $"{theme}-data-subjects.png");

        main.NavigateTo("Settings");
        await FlushAsync(main, 300);
        Capture(main, shotsDir, $"{theme}-settings-clean.png");

        var wizard = new SchoolWizardWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            Width = 960,
            Height = 700,
            ShowInTaskbar = false,
        };
        try
        {
            wizard.Show();
            await FlushAsync(wizard, 200);
            FindVisualChildren<Button>(wizard)
                .First(b => b.Content is string s && s == "Средняя (1–11)")
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await FlushAsync(wizard, 250);
            Capture(wizard, shotsDir, $"{theme}-wizard.png");
        }
        finally
        {
            wizard.Close();
        }
    }

    private static async Task FlushAsync(Window window, int delayMs)
    {
        if (delayMs > 0) await Task.Delay(delayMs);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
    }

    private static void Capture(Window win, string? shotsDir, string fileName)
    {
        var root = VisualTreeHelper.GetChild(win, 0) as FrameworkElement ?? (FrameworkElement)win;
        double wDip = root.ActualWidth > 0 ? root.ActualWidth : win.ActualWidth;
        double hDip = root.ActualHeight > 0 ? root.ActualHeight : win.ActualHeight;
        int pw = Math.Max(1, (int)Math.Round(wDip * 1.25));
        int ph = Math.Max(1, (int)Math.Round(hDip * 1.25));

        var rtb = new RenderTargetBitmap(pw, ph, 120, 120, PixelFormats.Pbgra32);
        rtb.Render(root);

        int stride = pw * 4;
        var pixels = new byte[stride * ph];
        rtb.CopyPixels(pixels, stride, 0);
        var distinct = new HashSet<int>();
        for (int y = 0; y < ph; y += Math.Max(1, ph / 36))
        {
            for (int x = 0; x < pw; x += Math.Max(1, pw / 64))
            {
                int off = y * stride + x * 4;
                int rgb = (pixels[off + 2] << 16) | (pixels[off + 1] << 8) | pixels[off];
                distinct.Add(rgb);
            }
        }
        Assert.True(distinct.Count > 15, $"{fileName}: rendered image looks blank ({distinct.Count} colors)");

        if (!string.IsNullOrWhiteSpace(shotsDir))
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(shotsDir, fileName));
            enc.Save(fs);
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var sub in FindVisualChildren<T>(child)) yield return sub;
        }
    }
}
