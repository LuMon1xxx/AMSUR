using System.IO;
using System.Windows;
using System.Windows.Input;

namespace Amsur.Wpf;

// Точка входа рабочей версии: сессия (БД + год + данные) живёт здесь,
// окна берут её через ((App)Application.Current).Session.
public partial class App : System.Windows.Application
{
    public AppSession Session { get; private set; } = null!;

    // P1-shell fix: у Window НЕТ встроенных биндингов SystemCommands —
    // кнопки кастомной шапки (Command в ChromeWindow-стиле) молчали.
    // Регистрируем один раз на класс: работает и на показанных, и на
    // скрытых окнах (чистый WindowState/Close, без Win32-хэндлов).
    static App()
    {
        CommandManager.RegisterClassCommandBinding(typeof(Window), new CommandBinding(
            SystemCommands.MinimizeWindowCommand,
            (s, _) => ((Window)s).WindowState = WindowState.Minimized));
        CommandManager.RegisterClassCommandBinding(typeof(Window), new CommandBinding(
            SystemCommands.MaximizeWindowCommand,
            (s, _) => ((Window)s).WindowState = WindowState.Maximized));
        CommandManager.RegisterClassCommandBinding(typeof(Window), new CommandBinding(
            SystemCommands.RestoreWindowCommand,
            (s, _) => ((Window)s).WindowState = WindowState.Normal));
        CommandManager.RegisterClassCommandBinding(typeof(Window), new CommandBinding(
            SystemCommands.CloseWindowCommand,
            (s, _) => ((Window)s).Close()));
    }

    // P-D5: тёмная тема копированием записей поверх Resources.
    // StaticResource резолвится при загрузке окон — позже созданные окна
    // увидят тёмные значения без переделки XAML.
    private void ApplyDarkTheme()
    {
        try
        {
            var dict = new ResourceDictionary
            {
                Source = new Uri("Themes/Dark.xaml", UriKind.Relative),
            };
            foreach (var key in dict.Keys.Cast<object>().ToList())
                Resources[key] = dict[key];
        }
        catch { /* тёмная не легла — остаёмся на светлой, честно без падения */ }
    }

    // D-48: детект тестраннера (vstest/testhost): прод-старт запрещён.
    private static bool IsTestHost()
    {
        try
        {
            var entry = System.Reflection.Assembly.GetEntryAssembly()?.FullName ?? "";
            if (entry.Contains("testhost", StringComparison.OrdinalIgnoreCase) ||
                entry.Contains("vstest", StringComparison.OrdinalIgnoreCase))
                return true;
            return AppDomain.CurrentDomain.FriendlyName.Contains("testhost",
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        // D-48: STA-тесты создают App + крутят Dispatcher — конструктор
        // Application постит отложенный OnStartup, и без этой стражи тесты
        // поднимали бы НАСТОЯЩЕЕ окно с НАСТОЯЩИМИ данными юзера (ghost window,
        // найдено 20.09.2026 на рендер-тестах). Под тестраннером — только ресурсы.
        if (IsTestHost()) return;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Amsur");
            Directory.CreateDirectory(dir);
            Session = new AppSession(dir);
            await Session.InitAsync();
            // P-D5: тёмная тема — поверх ресурсов ДО создания окон.
            if (Session.ThemeName == "dark") ApplyDarkTheme();
            // A3: окно создаём ЯВНО после InitAsync (без StartupUri) — конструктор
            // MainWindow больше не может отработать до готовых данных.
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось запустить: " + ex.Message,
                "АМСУР", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
