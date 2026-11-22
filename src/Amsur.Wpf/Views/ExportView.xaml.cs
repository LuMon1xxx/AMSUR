using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Amsur.Application;
using Microsoft.Win32;

namespace Amsur.Wpf.Views;

// P-D3: экспорт как view (логика из ExportWindow 1-в-1; Close убран).
public partial class ExportView : UserControl
{
    private readonly AppSession _session;
    /// <summary>Последний успешно выгруженный файл (кнопки «Открыть»/«Показать в папке»).</summary>
    private string? _lastFile;

    public ExportView(AppSession session)
    {
        _session = session;
        InitializeComponent();
        PathBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "расписание.xlsx");
        _ = LoadPreviewAsync();
    }

    private static void Bind(FrameworkElement el, DependencyProperty prop, string key)
    {
        if (el.TryFindResource(key) is not null)
            el.SetResourceReference(prop, key);
    }

    private string SelectedExtension() =>
        FormatHtmlRadio?.IsChecked == true ? ".html"
        : FormatCsvRadio?.IsChecked == true ? ".csv"
        : ".xlsx";

    private void OnFormatChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        string ext = SelectedExtension();
        bool isExcel = ext == ".xlsx";
        if (TeacherSheetBox is not null) TeacherSheetBox.IsEnabled = isExcel;
        if (RoomSheetBox is not null) RoomSheetBox.IsEnabled = isExcel;
        if (ExportBtnLabel is not null)
            ExportBtnLabel.Text = ext switch
            {
                ".html" => "Выгрузить HTML для печати",
                ".csv" => "Выгрузить таблицу CSV",
                _ => "Выгрузить в Excel (.xlsx)",
            };
        string current = PathBox.Text.Trim();
        if (current.Length > 0)
            PathBox.Text = Path.ChangeExtension(current, ext);
    }

    private async Task LoadPreviewAsync()
    {
        try
        {
            var active = await _session.GetActiveAsync();
            var names = _session.Data?.Teachers
                .OrderBy(t => t.Name).Select(t => t.Name).ToList() ?? [];
            TeacherBox.ItemsSource = names;
            if (TeacherBox.Items.Count > 0) TeacherBox.SelectedIndex = 0;
            if (active is null)
            {
                ClearReveal();
                ResultText.Text = "Нет активного расписания — сгенерируйте варианты и нажмите «Принять этот вариант».";
                Bind(ResultText, TextBlock.ForegroundProperty, "BBad");
                ExportBtn.IsEnabled = false;
                ExportBtn.ToolTip = "Сначала сгенерируйте расписание и примите подходящий вариант.";
                GateText.Text = "нет активного";
                Bind(GateText, TextBlock.ForegroundProperty, "BBad");
                Bind(GateBadge, Border.BackgroundProperty, "BBadBg");
                ScopeText.Text = "Нет данных";
                PreviewList.ItemsSource = Array.Empty<string>();
                return;
            }
            var problem = _session.BuildProblem();
            var occById = problem.Occurrences.ToDictionary(o => o.Id);
            string CN(Guid id) => occById.TryGetValue(id, out var o) && problem.Classes.TryGetValue(o.ClassId, out var c) ? c.Name : "?";
            string SN(Guid id) => occById.TryGetValue(id, out var o) && problem.Subjects.TryGetValue(o.SubjectId, out var s) ? s.Name : "?";
            string TN(Guid id) => occById.TryGetValue(id, out var o) && problem.Teachers.TryGetValue(o.TeacherId, out var t) ? t.Name : "?";
            string RN(Guid? id) => id.HasValue && problem.Rooms.TryGetValue(id.Value, out var r) ? r.Name : "—";

            var ordered = active.Placements
                .OrderBy(p => CN(p.OccurrenceId)).ThenBy(p => p.DayIndex).ThenBy(p => p.SlotIndex)
                .Take(12)
                .Select(p => $"{CN(p.OccurrenceId)} · день {p.DayIndex + 1}, урок {p.SlotIndex} — {SN(p.OccurrenceId)} ({TN(p.OccurrenceId)}, {UiFormat.Room(RN(p.RoomId))})")
                .ToList();
            PreviewList.ItemsSource = ordered;
            ScopeText.Text = $"{problem.Classes.Count} классов · {active.Placements.Count} уроков · версия {active.Number}";
            GateText.Text = "готово к выгрузке";
            Bind(GateText, TextBlock.ForegroundProperty, "BGood");
            Bind(GateBadge, Border.BackgroundProperty, "BGoodBg");
            ResultText.Text = "";
            ExportBtn.IsEnabled = true;
            ExportBtn.ToolTip = null;
        }
        catch (Exception ex)
        {
            ResultText.Text = "Ошибка: " + ex.Message;
            Bind(ResultText, TextBlock.ForegroundProperty, "BBad");
            ExportBtn.IsEnabled = false;
            GateText.Text = "ошибка";
        }
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        string ext = SelectedExtension();
        int idx = ext == ".html" ? 2 : ext == ".csv" ? 3 : 1;
        var dlg = new SaveFileDialog
        {
            Filter = "Excel (*.xlsx)|*.xlsx|HTML (*.html)|*.html|CSV (*.csv)|*.csv",
            FilterIndex = idx,
            FileName = Path.GetFileName(PathBox.Text),
        };
        if (dlg.ShowDialog() == true)
            PathBox.Text = dlg.FileName;
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = PathBox.Text.Trim();
            if (path.Length == 0) throw new InvalidOperationException("Укажите путь файла.");
            if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ||
                (FormatCsvRadio?.IsChecked == true &&
                 !path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) &&
                 !path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) &&
                 !path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)))
            {
                if (!path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                    path = Path.ChangeExtension(path, ".csv");
                await _session.ExportActiveCsvAsync(path);
            }
            else if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                     path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) ||
                     (FormatHtmlRadio?.IsChecked == true && !path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)))
            {
                if (!path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
                    path = Path.ChangeExtension(path, ".html");
                await _session.ExportActiveHtmlAsync(path);
            }
            else
            {
                if (!Path.HasExtension(path))
                    path += ".xlsx";
                await _session.ExportActiveAsync(path, TeacherSheetBox.IsChecked == true, RoomSheetBox.IsChecked == true);
            }
            PathBox.Text = path;
            ResultText.Text = "Выгружено: " + path;
            Bind(ResultText, TextBlock.ForegroundProperty, "BGood");
            ShowReveal(path);
        }
        catch (Exception ex)
        {
            ClearReveal();
            ResultText.Text = "Не выгружено: " + ex.Message;
            Bind(ResultText, TextBlock.ForegroundProperty, "BBad");
        }
    }

    private async void OnDraftClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new SaveFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", FileName = "черновик.xlsx" };
            if (dlg.ShowDialog() != true) return;
            var (placed, total) = await _session.ExportDraftAsync(dlg.FileName);
            ResultText.Text = $"Черновик собран: размещено {placed} из {total} " +
                $"(неназначенные — листом «Неназначенные»). Это прикидка, не финал.";
            Bind(ResultText, TextBlock.ForegroundProperty, "BGood");
            ShowReveal(dlg.FileName);
        }
        catch (Exception ex)
        {
            ClearReveal();
            ResultText.Text = "Черновик не собран: " + ex.Message;
            Bind(ResultText, TextBlock.ForegroundProperty, "BBad");
        }
    }

    private async void OnTeacherClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string? name = TeacherBox.SelectedItem as string;
            if (string.IsNullOrEmpty(name))
                throw new InvalidOperationException("Выберите учителя из списка.");
            var problem = _session.BuildProblem();
            var teacher = problem.Teachers.Values.FirstOrDefault(t => t.Name == name)
                ?? throw new InvalidOperationException("Учитель не найден в данных.");
            var dlg = new SaveFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", FileName = $"{name}.xlsx" };
            if (dlg.ShowDialog() != true) return;
            await _session.ExportTeacherAsync(dlg.FileName, teacher.Id);
            ResultText.Text = "Выгружено расписание: " + name;
            Bind(ResultText, TextBlock.ForegroundProperty, "BGood");
            ShowReveal(dlg.FileName);
        }
        catch (Exception ex)
        {
            ClearReveal();
            ResultText.Text = "Не выгружено: " + ex.Message;
            Bind(ResultText, TextBlock.ForegroundProperty, "BBad");
        }
    }

    // --- Пакет 6: после выгрузки — быстрые действия с файлом ---
    private void ShowReveal(string path)
    {
        _lastFile = path;
        RevealPanel.Visibility = File.Exists(path) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearReveal()
    {
        _lastFile = null;
        RevealPanel.Visibility = Visibility.Collapsed;
    }

    private bool RequireFile()
    {
        if (_lastFile is not null && File.Exists(_lastFile)) return true;
        ClearReveal();
        ResultText.Text = "Файла больше нет — выгрузите заново.";
        Bind(ResultText, TextBlock.ForegroundProperty, "BBad");
        return false;
    }

    private void OnOpenFileClick(object sender, RoutedEventArgs e)
    {
        if (!RequireFile()) return;
        try
        {
            Process.Start(new ProcessStartInfo(_lastFile!) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ResultText.Text = "Не удалось открыть файл: " + ex.Message;
            Bind(ResultText, TextBlock.ForegroundProperty, "BBad");
        }
    }

    private void OnShowInFolderClick(object sender, RoutedEventArgs e)
    {
        if (!RequireFile()) return;
        try
        {
            Process.Start("explorer.exe", $"/select,\"{_lastFile}\"");
        }
        catch (Exception ex)
        {
            ResultText.Text = "Не удалось показать в папке: " + ex.Message;
            Bind(ResultText, TextBlock.ForegroundProperty, "BBad");
        }
    }
}
