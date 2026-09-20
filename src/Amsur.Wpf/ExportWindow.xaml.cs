using System.IO;
using System.Windows;
using System.Windows.Media;
using Amsur.Application;
using Microsoft.Win32;

namespace Amsur.Wpf;

// P0-6: честный экспорт — Excel (классы+учителя+кабинеты) и HTML (печать в PDF из браузера).
// Отдельной кнопки PDF нет осознанно: QuestPDF-лицензия не покрыта (D5), HTML печатается в PDF без потерь.
public partial class ExportWindow : Window
{
    private readonly AppSession _session;

    public ExportWindow(AppSession session)
    {
        _session = session;
        InitializeComponent();
        PathBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "расписание.xlsx");
        _ = LoadPreviewAsync();
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
                ResultText.Text = "Нет активного расписания — сгенерируйте варианты и нажмите «Принять расписание».";
                ResultText.Foreground = (Brush)FindResource("BBad");
                ExportBtn.IsEnabled = false;
                ExportBtn.ToolTip = "Сначала сгенерируйте расписание и примите лучший вариант.";
                GateText.Text = "нет активного";
                GateText.Foreground = (Brush)FindResource("BBad");
                GateBadge.Background = (Brush)FindResource("BBadBg");
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
                .Select(p => $"{CN(p.OccurrenceId)} · день {p.DayIndex + 1}, урок {p.SlotIndex} — {SN(p.OccurrenceId)} ({TN(p.OccurrenceId)}, каб. {RN(p.RoomId)})")
                .ToList();
            PreviewList.ItemsSource = ordered;
            ScopeText.Text = $"{problem.Classes.Count} классов · {active.Placements.Count} уроков · версия {active.Number}";
            GateText.Text = "готово к выгрузке";
            GateText.Foreground = (Brush)FindResource("BGood");
            GateBadge.Background = (Brush)FindResource("BGoodBg");
            ResultText.Text = "";
            ExportBtn.IsEnabled = true;
            ExportBtn.ToolTip = null;
        }
        catch (Exception ex)
        {
            ResultText.Text = "Ошибка: " + ex.Message;
            ResultText.Foreground = (Brush)FindResource("BBad");
            ExportBtn.IsEnabled = false;
            GateText.Text = "ошибка";
        }
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "Excel (*.xlsx)|*.xlsx|HTML (*.html)|*.html", FileName = Path.GetFileName(PathBox.Text) };
        if (dlg.ShowDialog() == true)
            PathBox.Text = dlg.FileName;
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)    {
        try
        {
            string path = PathBox.Text.Trim();
            if (path.Length == 0) throw new InvalidOperationException("Укажите путь файла.");
            if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
                await _session.ExportActiveHtmlAsync(path);
            else
                await _session.ExportActiveAsync(path, TeacherSheetBox.IsChecked == true, RoomSheetBox.IsChecked == true);
            ResultText.Text = "Выгружено: " + path;
            ResultText.Foreground = (Brush)FindResource("BGood");
        }
        catch (Exception ex)
        {
            ResultText.Text = "Не выгружено: " + ex.Message;
            ResultText.Foreground = (Brush)FindResource("BBad");
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
            ResultText.Foreground = (Brush)FindResource("BGood");
        }
        catch (Exception ex)
        {
            ResultText.Text = "Черновик не собран: " + ex.Message;
            ResultText.Foreground = (Brush)FindResource("BBad");
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
            ResultText.Foreground = (Brush)FindResource("BGood");
        }
        catch (Exception ex)
        {
            ResultText.Text = "Не выгружено: " + ex.Message;
            ResultText.Foreground = (Brush)FindResource("BBad");
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
