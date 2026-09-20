using System.Windows;
using System.Windows.Controls;

namespace Amsur.Wpf.Views;

// P-D0: временная заглушка. См. PlaceholderView.xaml.
public partial class PlaceholderView : UserControl
{
    public Action? LegacyOpen { get; set; }

    public PlaceholderView(string title, string subtitle, string legacyLabel)
    {
        InitializeComponent();
        TitleText.Text = title;
        SubText.Text = subtitle;
        LegacyBtn.Content = legacyLabel;
    }

    private void OnLegacyClick(object sender, RoutedEventArgs e) => LegacyOpen?.Invoke();
}
