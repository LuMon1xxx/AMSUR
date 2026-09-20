using Amsur.Application;
using Amsur.Wpf;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Amsur.Tests;

// E11 — оболочка: XAML грузится, пустые состояния честные (STA-поток + Dispatcher,
// как в реальном приложении; Show() не вызываем — только конструктор и данные).
public sealed class WpfShellTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"amsur-e11-{Guid.NewGuid():N}");

    public ValueTask DisposeAsync()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        return ValueTask.CompletedTask;
    }

    private static void RunSta(Func<System.Windows.Threading.Dispatcher, Task> body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                dispatcher.InvokeAsync(async () =>
                {
                    try { await body(dispatcher); }
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

    // --- 1. GenerateWindow собирается с VM (E4/E5/E6-разметка валидна) ---
    [Fact]
    public void GenerateWindow_ConstructsWithVm()
    {
        RunSta(_ =>
        {
            var win = new GenerateWindow(new GenerateViewModel());
            Assert.Equal("Составление расписания", ((GenerateViewModel)win.DataContext).Title);
            win.Close();
            return Task.CompletedTask;
        });
    }

    // --- 1b. Top-5 с 1 карточкой рендерится без XamlParseException (repro 13.09.2026:
    // GenerateWindow.xaml:182 BasedOn="{DynamicResource ...}" падал при первом кандидате,
    // пустой VM тест 1 его не ловил — шаблон инстанцируется только при Cards.Count > 0). ---
    [Fact]
    public void GenerateWindow_RendersTop5Card()
    {
        RunSta(_ =>
        {
            EnsureApp();
            var vm = new GenerateViewModel();
            var card = new CandidateCardModel
            {
                Rank = 1, SoftTotal = 123, HardViolations = 0, Badge = "Лучший",
                DifferenceLines = ["Лучшее найденное расписание"],
                QualitySummary = "Soft 123",
                QualityLines = ["Строка 1"],
                CanAccept = true, Candidate = null!,
            };
            vm.Top5 = new Top5PanelModel { Cards = [card], TotalFound = 1 };
            var win = new GenerateWindow(vm);
            // Show off-screen: шаблон карточки Top-5 материализуется только
            // в loaded-дереве при layout (Measure/Arrange после Show) — именно
            // там падал прод (Event Viewer .NET 1026, XamlParseException BasedOn).
            // LoadContent без Show ошибку НЕ воспроизводит (проверено 14.09.2026).
            win.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            win.Left = -10000; win.Top = -10000;
            win.Width = 1100; win.Height = 780;
            win.Show();
            win.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            win.Close();
            return Task.CompletedTask;
        });
    }

    // --- 1c. E2E прод-пути в реальном окне (repro 13.09.2026): DemoSchool →
    // GenerateHost (как MainWindow.OnGenerateClick) → RunAsync → рендер Top-5
    // с НАСТОЯЩИМИ кандидатами в показанном окне → Accept. Бюджет 4с/1 сид:
    // жадный старт даёт feasible сразу, узел краша (первая карточка) покрыт.
    // STANDARD целиком покрыт DemoSchool_StandardRun_ProducesSchedule (33с). ---
    [Fact]
    public void DemoSchool_GenerateWindow_AcceptsBest()
    {
        RunSta(async _ =>
        {
            Directory.CreateDirectory(_dir);
            EnsureApp();
            var session = new AppSession(_dir);
            await session.InitAsync();
            await session.ImportLoadAsync(DemoSchoolTests.DemoRows(), days: 5, slots: 7);
            var (win, orch) = GenerateHost.Create(session.Data!.ToProblemInput(),
                perSeedBudgetSeconds: 4, numWorkers: 1,
                dbPath: session.DbPath, rules: session.QualityRules, modeName: "Быстро");
            win.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            win.Left = -10000; win.Top = -10000;
            win.Width = 1100; win.Height = 780;
            win.Show();
            var outcome = await orch.RunAsync([11]);
            win.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.True(outcome.HasFeasible);
            Assert.NotNull(orch.ViewModel.Top5.Best);
            var accepted = await orch.AcceptAsync(orch.ViewModel.Top5.Best);
            Assert.True(accepted.Succeeded);
            win.Close();
        });
    }

    // --- 2. ScheduleWindow: нет активного — честное пустое состояние ---
    [Fact]
    public void ScheduleWindow_EmptyStateWhenNoActive()
    {
        RunSta(async _ =>
        {
            Directory.CreateDirectory(_dir);
            var session = new AppSession(_dir);
            await session.InitAsync();
            var win = new ScheduleWindow(session);
            for (int i = 0; i < 100 && win.StatusText.Text == "Загрузка…"; i++)
                await Task.Delay(50);
            Assert.Contains("Нет активного расписания", win.StatusText.Text);
            win.Close();
        });
    }

    // --- 2b. SettingsWindow: карточка перегрузки строится (выкл. и вкл.) ---    [Fact]
    public void SettingsWindow_OverloadCard_Constructs()
    {
        RunSta(async _ =>
        {
            Directory.CreateDirectory(_dir);
            EnsureApp();
            var session = new AppSession(_dir);
            await session.InitAsync();
            await session.ImportLoadAsync(DemoSchoolTests.DemoRows(), days: 5, slots: 7);
            var winOff = new SettingsWindow(session);
            winOff.Close();
            var flex = session.Flex with
            {
                Settings = session.Flex.Settings with
                {
                    AllowTeacherOverload = true, TeacherOverloadCap = 10,
                },
            };
            await session.ApplyFlexAsync(flex);
            var winOn = new SettingsWindow(session);
            winOn.Close();
        });
    }

    // --- 2c. ExportWindow: без активного — честное пустое состояние, черновик доступен ---
    [Fact]
    public void ExportWindow_EmptyStateDraftAvailable()
    {
        RunSta(async _ =>
        {
            Directory.CreateDirectory(_dir);
            EnsureApp();
            var session = new AppSession(_dir);
            await session.InitAsync();
            await session.ImportLoadAsync(DemoSchoolTests.DemoRows(), days: 5, slots: 7);
            var win = new ExportWindow(session);
            for (int i = 0; i < 100 && win.GateText.Text == "проверка…"; i++)
                await Task.Delay(50);
            Assert.Contains("нет активного", win.GateText.Text);
            Assert.False(win.ExportBtn.IsEnabled);
            Assert.True(win.DraftBtn.IsEnabled);
            Assert.True(win.TeacherBox.Items.Count > 0);
            win.Close();
        });
    }

    // --- 4. LoadRowWindow: диалог ручного ввода собирается и без ресурсов App ---    [Fact]
    public void LoadRowWindow_Constructs()
    {
        RunSta(_ =>
        {
            var win = new LoadRowWindow(null, ["5А"], ["Мат"], ["Иванов"], ["101"]);
            Assert.Null(win.Result);
            win.Close();
            return Task.CompletedTask;
        });
    }

    private static Button? FindCaptionButton(DependencyObject root, string tooltip)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button b && Equals(b.ToolTip, tooltip))
                return b;
            var found = FindCaptionButton(child, tooltip);
            if (found is not null) return found;
        }
        return null;
    }

    // Настоящий путь клика (IInvokeProvider → OnClick → Command), не RaiseEvent:
    // RaiseEvent(Click) команду не выполняет и ничего не доказывает.
    // Invoke асинхронен (BeginInvoke Input) — после клика промываем диспетчер.
    private static void Click(Button b)
    {
        ((IInvokeProvider)new ButtonAutomationPeer(b)).Invoke();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    // --- 5. Кнопки кастомной шапки реально работают (repro: свернуть/развернуть) ---
    [Fact]
    public void ChromeTitleButtons_MinMaxWork()
    {
        RunSta(async _ =>
        {
            Directory.CreateDirectory(_dir);
            var app = EnsureApp();
            var session = new AppSession(_dir);
            await session.InitAsync();
            typeof(App).GetProperty("Session")!.SetValue(app, session);
            var main = new MainWindow();
            main.ApplyTemplate();
            var min = FindCaptionButton(main, "Свернуть");
            var max = FindCaptionButton(main, "Развернуть");
            var restore = FindCaptionButton(main, "Восстановить");
            var close = FindCaptionButton(main, "Закрыть");
            Assert.NotNull(min);
            Assert.NotNull(max);
            Assert.NotNull(restore);
            Assert.NotNull(close);
            Assert.Equal(WindowState.Maximized, main.WindowState);
            Click(restore!);
            Assert.Equal(WindowState.Normal, main.WindowState);
            Click(max!);
            Assert.Equal(WindowState.Maximized, main.WindowState);
            Click(min!);
            Assert.Equal(WindowState.Minimized, main.WindowState);
            main.Close();
        });
    }

    private static bool _appResourcesLoaded;
    private static readonly object _appLock = new();

    private static App EnsureApp()
    {
        // new App() не грузит App.xaml (в проде это делает generated-Main);
        // здесь грузим ресурсы темы один раз, явно (только тесты).
        lock (_appLock)
        {
            var app = System.Windows.Application.Current as App ?? new App();
            if (!_appResourcesLoaded)
            {
                app.InitializeComponent();
                _appResourcesLoaded = true;
            }
            return app;
        }
    }

    // --- 3. Новые окна: один STA-поток + один App (ресурсы темы на том же потоке) ---
    [Fact]
    public void NewWindows_Construct()
    {
        RunSta(async _ =>
        {
            Directory.CreateDirectory(_dir);
            var app = EnsureApp();
            var session = new AppSession(_dir);
            await session.InitAsync();
            typeof(App).GetProperty("Session")!.SetValue(app, session);
            var main = new MainWindow();
            Assert.Equal(System.Windows.Visibility.Visible, main.StateNoData.Visibility);
            Assert.Equal("Не загружены", main.MiniDataStatus.Text);
            Assert.Equal("Нет данных", main.FooterRightText.Text);
            main.Close();
            var settings = new SettingsWindow(session);
            settings.Close();
            new HelpWindow().Close();
            new QualityDetailsWindow("Хорошее", ["Окна учителей: 5"]).Close();
            new SchoolDataWindow(session).Close();
        });
    }
}
