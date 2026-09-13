using System.Windows;

namespace Amsur.Wpf;

public partial class HelpWindow : Window
{
    public HelpWindow() => InitializeComponent();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
