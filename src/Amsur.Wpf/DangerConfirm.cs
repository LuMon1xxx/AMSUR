using System.Windows;
using Amsur.Application;

namespace Amsur.Wpf;

// B1: единая точка опасных подтверждений. Использовать ВЕЗДЕ, где трогают опасное.
// Шов для тестов: Prompter подменяет реальный диалог (модалки в тестах нет).
public static class DangerConfirm
{
    public static Func<Window?, string, string, (bool Proceed, bool DontAsk)> Prompter { get; set; } =
        (owner, title, consequence) =>
        {
            var dlg = new DangerDialog(title, consequence) { Owner = owner };
            bool ok = dlg.ShowDialog() == true;
            return (ok, ok && dlg.DontAsk);
        };

    /// <returns>(Proceed, DontAsk). DontAsk=true только при Proceed.</returns>
    public static (bool Proceed, bool DontAsk) Show(
        Window? owner, string code, string title, string consequence)
    {
        string name = QualityHints.For(code).Title;
        if (string.IsNullOrWhiteSpace(name) || name == code)
            name = title;
        return Prompter(owner, $"Осторожно: {name}", consequence);
    }
}
