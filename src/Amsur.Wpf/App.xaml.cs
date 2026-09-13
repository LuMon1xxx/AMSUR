using System.IO;
using System.Windows;

namespace Amsur.Wpf;

// Точка входа рабочей версии: сессия (БД + год + данные) живёт здесь,
// окна берут её через ((App)Application.Current).Session.
public partial class App : System.Windows.Application
{
    public AppSession Session { get; private set; } = null!;

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
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось запустить: " + ex.Message,
                "АМСУР", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
