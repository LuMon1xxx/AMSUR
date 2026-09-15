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

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Amsur");
            Directory.CreateDirectory(dir);
            Session = new AppSession(dir);
            await Session.InitAsync();
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
