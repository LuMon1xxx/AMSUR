using System.Windows;

namespace Amsur.Wpf;

// B1: диалог опасного изменения. Дефолт — Отмена (IsCancel, без IsDefault на
// «Продолжить»: Enter не продолжает). Использовать через DangerConfirm.Show.
public partial class DangerDialog : Window
{
    public bool DontAsk { get; private set; }

    public DangerDialog(string title, string consequence)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = consequence;
        CancelBtn.Focus();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnContinueClick(object sender, RoutedEventArgs e)
    {
        DontAsk = DontAskBox.IsChecked == true;
        DialogResult = true;
        Close();
    }
}
