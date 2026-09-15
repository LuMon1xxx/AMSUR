using System.IO;
using System.Windows;
using System.Windows.Media;
using Amsur.Application;
using Microsoft.Win32;

namespace Amsur.Wpf;

// P0-6: честный экспорт — только Excel (один лист «Расписание» по классам).
// PDF-карточка в XAML disabled с бейджем «Скоро». Без журнала/fullscreen/табов листов.
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
            if (active is null)
            {
                ResultText.Text = "Нет активного расписания — сгенерируйте варианты и нажмите «Принять расписание».";
                ResultText.Foreground = (Brush)FindResource("BWarn");
                ExportBtn.IsEnabled = false;
                GateText.Text = "нет активного";
                ScopeText.Text = "—";
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
            ResultText.Text = "";
            ExportBtn.IsEnabled = true;
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
        var dlg = new SaveFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", FileName = Path.GetFileName(PathBox.Text) };
        if (dlg.ShowDialog() == true)
            PathBox.Text = dlg.FileName;
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = PathBox.Text.Trim();
            if (path.Length == 0) throw new InvalidOperationException("Укажите путь файла.");
            await _session.ExportActiveAsync(path, TeacherSheetBox.IsChecked == true);
            ResultText.Text = "Выгружено: " + path;
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
