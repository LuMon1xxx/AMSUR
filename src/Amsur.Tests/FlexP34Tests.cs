using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;
using Amsur.Wpf;
using ClosedXML.Excel;
using System.Windows.Controls.Primitives;

namespace Amsur.Tests;

// P3/P4: сессия переживает flex, экспорт с листом учителей, окна строятся с данными.
public sealed class FlexP34Tests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"amsur-p34-{Guid.NewGuid():N}");

    public ValueTask DisposeAsync()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        return ValueTask.CompletedTask;
    }

    private static void RunSta(Func<Task> body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                dispatcher.InvokeAsync(async () =>
                {
                    try { await body(); }
                    catch (Exception ex) { error = ex; }
                    finally { dispatcher.InvokeShutdown(); }
                });
                System.Windows.Threading.Dispatcher.Run();
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "STA thread hung");
        if (error is not null) throw error;
    }

    private static void EnsureApp()
    {
        // Как WpfShellTests.EnsureApp: App + InitializeComponent грузит тему (BBg и др.).
        if (System.Windows.Application.Current is App) return;
        var app = new App();
        app.InitializeComponent();
    }

    // Сессия: flex применяется, переживает перезапуск, флаги доходят до задачи.
    [Fact]
    public async Task Session_Flex_Roundtrip()
    {
        Directory.CreateDirectory(_dir);
        var session = new AppSession(_dir);
        await session.InitAsync();
        await session.ImportLoadAsync(DemoSchoolTests.DemoRows(), days: 5, slots: 7);
        Assert.Empty(session.Flex.Rooms);

        var flex = FlexDataset.Empty with
        {
            Rooms = new List<RoomConfigRow>
            {
                new("Спортзал", false, null, 4, 2, true)
            },
            Settings = new FlexSettingsRow(true, 3, 2, 1, 7, TeacherAssignMode.HardClass),
        };
        await session.ApplyFlexAsync(flex);
        Assert.Single(session.Flex.Rooms);
        // Флаги дошли до сущностей и задачи.
        var gym = session.Data!.Rooms.Single(r => r.Name == "Спортзал");
        Assert.Equal(4, gym.MaxSimultaneousGroups);
        Assert.Equal(2, gym.DesiredGroups);
        var problem = session.BuildProblem();
        Assert.Equal(4, problem.Rooms[gym.Id].MaxSimultaneousGroups);
        Assert.Equal(3, problem.Flex.Grade11Weight);

        // Новая сессия читает flex с диска.
        var session2 = new AppSession(_dir);
        await session2.InitAsync();
        Assert.True(session2.HasData);
        Assert.Single(session2.Flex.Rooms);
        Assert.Equal(4, session2.Data!.Rooms.Single(r => r.Name == "Спортзал").MaxSimultaneousGroups);
    }

    // Битый flex не роняет данные (fail-loud, старое живо).
    [Fact]
    public async Task Session_Flex_BadKeepsOldData()
    {
        Directory.CreateDirectory(_dir);
        var session = new AppSession(_dir);
        await session.InitAsync();
        await session.ImportLoadAsync(DemoSchoolTests.DemoRows(), days: 5, slots: 7);
        int occBefore = session.BuildProblem().Occurrences.Count;
        var bad = FlexDataset.Empty with
        {
            HourOverrides = new List<HourOverrideRow> { new("6А", "Математика", 0) },
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ApplyFlexAsync(bad));
        Assert.Empty(session.Flex.HourOverrides);
        Assert.Equal(occBefore, session.BuildProblem().Occurrences.Count);
    }

    // R9: лист «Учителя» пишется и читается; флаг выключает.
    [Fact]
    public void Export_TeacherSheet()
    {
        var year = Guid.NewGuid();
        var cls = new SchoolClass { AcademicYearId = year, Name = "5А", Grade = 5, StudentCount = 20 };
        var t = new Teacher { Name = "Иванова", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Математика", MaxPerDay = 2 };
        var room = new Room { Name = "Каб", PhysicalCapacity = 30 };
        var input = new ProblemInput([cls], [t], [math],
            [new CurriculumItem { ClassId = cls.Id, SubjectId = math.Id, TeacherId = t.Id, HoursPerWeek = 2 }],
            [], [], [], DaysCount: 2, SlotsPerDay: 3, rooms: [room]);
        var (p, e) = ProblemBuilder.Build(input);
        Assert.Empty(e);
        var placed = GreedyPlacer.Place(p!);
        Assert.Empty(placed.Unplaced);
        var lessons = placed.Placed.Select(kv => new PlacedLesson
        {
            OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
            SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
        }).ToList();

        using var ms = new MemoryStream();
        ScheduleExcelExporter.ExportGrid(p!, lessons, ms, includeTeacherSheet: true);
        ms.Position = 0;
        using var wb = new XLWorkbook(ms);
        Assert.Contains(wb.Worksheets, w => w.Name == "Расписание");
        var ts = wb.Worksheets.Single(w => w.Name == "Учителя");
        Assert.Equal("Учитель", ts.Cell(1, 1).GetString());
        Assert.Equal("Иванова", ts.Cell(2, 1).GetString());
        Assert.Equal("5А", ts.Cell(2, 4).GetString());

        using var ms2 = new MemoryStream();
        ScheduleExcelExporter.ExportGrid(p!, lessons, ms2, includeTeacherSheet: false);
        ms2.Position = 0;
        using var wb2 = new XLWorkbook(ms2);
        Assert.DoesNotContain(wb2.Worksheets, w => w.Name == "Учителя");
    }

    // Окна с данными строятся; гриды заполнены.
    [Fact]
    public void Windows_WithData_Construct()
    {
        RunSta(async () =>
        {
            Directory.CreateDirectory(_dir);
            EnsureApp();
            var session = new AppSession(_dir);
            await session.InitAsync();
            await session.ImportLoadAsync(DemoSchoolTests.DemoRows(), days: 5, slots: 7);

            var school = new SchoolDataWindow(session);
            Assert.Equal(6, school.ClassesGrid.Items.Count);
            Assert.Equal(10, school.RoomsGrid.Items.Count);
            Assert.Equal(10, school.TeachersList.Items.Count);
            // Рендер off-screen: шаблоны колонок материализуются только при layout.
            school.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            school.Left = -10000; school.Top = -10000;
            school.Width = 1100; school.Height = 700;
            school.Show();
            school.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            school.Close();

            new SettingsWindow(session).Close();
            new ExportWindow(session).Close();
            new ScheduleWindow(session).Close();
        });
    }

    // Полный UI-путь: кнопка «Применить кабинеты» сохраняет срез в сессию и store.
    [Fact]
    public void Ui_RoomsApply_SavesSlice()
    {
        RunSta(async () =>
        {
            Directory.CreateDirectory(_dir);
            EnsureApp();
            var session = new AppSession(_dir);
            await session.InitAsync();
            await session.ImportLoadAsync(DemoSchoolTests.DemoRows(), days: 5, slots: 7);

            var win = new SchoolDataWindow(session);
            var btn = (System.Windows.Controls.Button)win.FindName("ApplyRoomsBtn");
            btn.RaiseEvent(new System.Windows.RoutedEventArgs(ButtonBase.ClickEvent));
            // async void-обработчик: даём диспетчеру отработать.
            for (int i = 0; i < 50 && session.Flex.Rooms.Count == 0; i++)
                await Task.Delay(100);
            Assert.Equal(10, session.Flex.Rooms.Count);
            var gym = session.Data!.Rooms.Single(r => r.Name == "Спортзал");
            Assert.Equal(1, gym.MaxSimultaneousGroups); // дефолты без изменений
            win.Close();

            // Перезапуск видит сохранённое.
            var session2 = new AppSession(_dir);
            await session2.InitAsync();
            Assert.Equal(10, session2.Flex.Rooms.Count);
        });
    }
}
