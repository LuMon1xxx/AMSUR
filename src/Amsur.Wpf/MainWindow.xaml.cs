using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Amsur.Wpf.Views;

namespace Amsur.Wpf;

// P-D0: оболочка одного окна. Дашборд — Views/DashboardView, остальные разделы
// пока заглушки с временным открытием старых окон (пакеты P-D1..P-D3 плана).
public partial class MainWindow : Window, IViewNavigator
{
    private AppSession Session => ((App)System.Windows.Application.Current).Session;

    public DashboardView Dashboard { get; }

    private readonly Dictionary<string, Button> _navButtons = [];

    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKey;
        _navButtons["Dashboard"] = NavHome;
        _navButtons["Data"] = NavData;
        _navButtons["Settings"] = NavSettings;
        _navButtons["Generate"] = NavGenerate;
        _navButtons["Schedule"] = NavSchedule;
        _navButtons["Export"] = NavExport;
        _navButtons["Help"] = NavHelp;
        Dashboard = new DashboardView { Navigator = this, ChromeRefresh = RefreshChrome };
        ThemeBox.IsChecked = Session.ThemeName == "dark";
        NavigateTo("Dashboard");
        RefreshChrome();
    }

    // P-D5: смена темы — сохранение + перезапуск (StaticResource применяется при загрузке).
    private async void OnThemeChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box || !IsLoaded) return;
        string want = box.IsChecked == true ? "dark" : "light";
        if (want == Session.ThemeName) return;
        try { await Session.SetThemeAsync(want); }
        catch { return; }
        var res = MessageBox.Show(
            "Тема применится после перезапуска. Перезапустить АМСУР сейчас?",
            "АМСУР", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (res != MessageBoxResult.Yes) return;
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (exe is not null)
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch { return; }
        Close();
    }

    public void NavigateTo(string view)
    {
        UserControl next = view switch
        {
            "Data" => new DataView(Session) { Navigator = this },
            "Settings" => new SettingsView(Session) { Navigator = this },
            "Generate" => new PlaceholderView("Генерация расписания",
                "Запустите расчёт с главной (кнопка или F5).",
                "Открыть генерацию старым окном (временно)")
            { LegacyOpen = () => Dashboard.OnGenerateClick(this, new RoutedEventArgs()) },
            "Schedule" => new ScheduleView(Session) { Navigator = this },
            "Export" => new ExportView(Session),
            "Help" => new HelpView(),
            _ => Dashboard,
        };
        if (view == "Dashboard") Dashboard.RefreshAll();
        ViewHost.Content = next;
        PlayEnter(next);
        PaintNav(view);
    }

    // P-D0 анимация: появление раздела (fade + подъём 12px, 220мс).
    private static void PlayEnter(UIElement el)
    {
        el.Opacity = 0;
        el.RenderTransform = new TranslateTransform(0, 12);
        var sb = new Storyboard();
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220));
        Storyboard.SetTarget(fade, el);
        Storyboard.SetTargetProperty(fade, new PropertyPath("Opacity"));
        var slide = new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(slide, el);
        Storyboard.SetTargetProperty(slide,
            new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));
        sb.Children.Add(fade);
        sb.Children.Add(slide);
        sb.Begin();
    }

    private void PaintNav(string active)
    {
        var activeStyle = (Style)FindResource("NavBtnActive");
        var idleStyle = (Style)FindResource("NavBtn");
        foreach (var (key, btn) in _navButtons)
            btn.Style = key == active ? activeStyle : idleStyle;
        // «Профили» ведёт в настройки, но активным не подсвечивается.
        NavProfiles.Style = idleStyle;
    }

    // P-D3: запуск генерации внутри оболочки (view + оркестратор из GenerateHost).
    public void OpenGenerate(Amsur.Scheduling.Core.ProblemInput input)
    {
        var (view, _) = GenerateHost.CreateView(input, Session, Session.GenerateMode.Name);
        ViewHost.Content = view;
        PlayEnter(view);
        PaintNav("Generate");
    }

    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        if (sender == NavHome) NavigateTo("Dashboard");
        else if (sender == NavData) NavigateTo("Data");
        else if (sender == NavSettings || sender == NavProfiles) NavigateTo("Settings");
        else if (sender == NavGenerate) Dashboard.OnGenerateClick(sender, e);
        else if (sender == NavSchedule) NavigateTo("Schedule");
        else if (sender == NavExport) NavigateTo("Export");
        else if (sender == NavHelp) NavigateTo("Help");
    }

    private void OnHelpNav(object sender, RoutedEventArgs e) => NavigateTo("Help");

    private void OnPreviewKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F5)
        {
            NavigateTo("Dashboard");
            Dashboard.OnGenerateClick(sender, e);
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.F1)
        {
            NavigateTo("Help");
            e.Handled = true;
        }
    }

    public void RefreshChrome()
    {
        var s = Session.Summary;
        SchoolNavBadge.Text = s is null ? "" : $"{s.Classes} кл.";
        try
        {
            long mb = Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024;
            FooterLeftText.Text =
                $"Движок: CP-SAT + локальный поиск · Память: {mb} МБ · СанПиН: требует сверки с НПА №206/№35";
        }
        catch { }
        FooterRightText.Text = s is null
            ? "Нет данных"
            : $"{s.Classes} классов · {s.Teachers} учителей · {s.Days} дн. × {s.Slots} ур.";
    }
}
