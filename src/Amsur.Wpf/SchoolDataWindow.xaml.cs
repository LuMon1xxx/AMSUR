using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Amsur.Application;
using Microsoft.Win32;

namespace Amsur.Wpf;

public sealed record LoadRowVm(
    int SourceIndex,
    string ClassName, string SubjectName, int Hours,
    string TeacherName, string RoomName, string GroupText);

public partial class SchoolDataWindow : Window
{
    private readonly AppSession _session;
    private ObservableCollection<LoadRowVm> _allLoad = [];

    public SchoolDataWindow(AppSession session)
    {
        _session = session;
        InitializeComponent();
        SearchBox.ToolTip = "Поиск по классу, предмету или учителю";
        LoadAll();
    }

    private void LoadAll()
    {
        var d = _session.Data;
        if (d is null)
        {
            SummaryText.Text = "Данные не загружены.";
            CountText.Text = "";
            return;
        }
        var classes = d.Classes.ToDictionary(c => c.Id);
        string CN(Guid id) => classes.TryGetValue(id, out var c) ? c.Name : "?";

        SummaryText.Text = $"{d.Classes.Count} классов · {d.Teachers.Count} учителей · " +
            $"{d.Subjects.Count} предметов · {d.Rooms.Count} кабинетов · " +
            $"{d.Curriculum.Sum(c => c.HoursPerWeek)} ч нагрузки · " +
            $"{d.DaysCount} дн. × {d.SlotsPerDay} ур.";
        TabLoad.Content = $"Нагрузка ({d.Curriculum.Count})";
        TabClasses.Content = $"Классы ({d.Classes.Count})";
        TabTeachers.Content = $"Учителя ({d.Teachers.Count})";
        TabSubjects.Content = $"Предметы ({d.Subjects.Count})";
        TabRooms.Content = $"Кабинеты ({d.Rooms.Count})";
        TabGroups.Content = $"Подгруппы ({d.Groups.Count})";

        // P3: вид нагрузки строим из исходных строк (сохраняем индекс для правок).
        _allLoad = new ObservableCollection<LoadRowVm>(_session.LoadRows
            .Select((r, i) => (Row: r, Index: i))
            .OrderBy(x => x.Row.ClassName).ThenBy(x => x.Row.SubjectName)
            .Select(x => new LoadRowVm(x.Index, x.Row.ClassName, x.Row.SubjectName, x.Row.HoursPerWeek,
                x.Row.TeacherName, x.Row.RoomName ?? "—", x.Row.SplitSubgroups ? "A/B" : "Весь класс")));
        LoadGrid.ItemsSource = _allLoad;
        DaysBox.Text = d.DaysCount.ToString();
        SlotsBox.Text = d.SlotsPerDay.ToString();
        ClassFilterBox.ItemsSource = new[] { "Все классы" }
            .Concat(d.Classes.OrderBy(c => c.Name).Select(c => c.Name)).ToList();
        ClassFilterBox.SelectedIndex = 0;

        ClassesList.ItemsSource = d.Classes.OrderBy(c => c.Name)
            .Select(c => $"{c.Name} — {d.Curriculum.Where(x => x.ClassId == c.Id).Sum(x => x.HoursPerWeek)} ч/нед").ToList();
        TeachersList.ItemsSource = d.Teachers.OrderBy(t => t.Name)
            .Select(t => $"{t.Name} — {d.Curriculum.Where(x => x.TeacherId == t.Id).Sum(x => x.HoursPerWeek)} ч/нед").ToList();
        SubjectsList.ItemsSource = d.Subjects.OrderBy(s => s.Name).Select(s => s.Name).ToList();
        RoomsList.ItemsSource = d.Rooms.OrderBy(r => r.Name).Select(r => r.Name).ToList();
        GroupsList.ItemsSource = d.Groups.Count == 0
            ? new[] { "Делений на подгруппы нет." }
            : d.Groups.Select(g => $"{CN(g.ClassId)} — группа {g.Name}").ToList();
        NotesList.ItemsSource = d.Notes.Count == 0
            ? new[] { "Замечаний нет." }
            : d.Notes.Take(20).ToList();

        var errs = _session.LastImportErrors;
        if (errs.Count > 0)
        {
            ErrorCard.Visibility = Visibility.Visible;
            ErrorList.ItemsSource = errs.Take(8).ToList();
        }
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string q = (SearchBox.Text ?? "").Trim().ToLowerInvariant();
        string? cls = ClassFilterBox.SelectedItem as string;
        var rows = _allLoad.Where(r =>
            (string.IsNullOrEmpty(q) || r.ClassName.ToLowerInvariant().Contains(q) ||
             r.SubjectName.ToLowerInvariant().Contains(q) || r.TeacherName.ToLowerInvariant().Contains(q)) &&
            (cls is null || cls == "Все классы" || r.ClassName == cls)).ToList();
        LoadGrid.ItemsSource = rows;
        CountText.Text = $"Показано {rows.Count} из {_allLoad.Count} записей";
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyFilter();
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyFilter();
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        int i = TabList.SelectedIndex;
        LoadGrid.Visibility = i <= 0 ? Visibility.Visible : Visibility.Collapsed;
        LoadToolbar.Visibility = i <= 0 ? Visibility.Visible : Visibility.Collapsed;
        ClassesList.Visibility = i == 1 ? Visibility.Visible : Visibility.Collapsed;
        TeachersList.Visibility = i == 2 ? Visibility.Visible : Visibility.Collapsed;
        SubjectsList.Visibility = i == 3 ? Visibility.Visible : Visibility.Collapsed;
        RoomsList.Visibility = i == 4 ? Visibility.Visible : Visibility.Collapsed;
        GroupsList.Visibility = i == 5 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTemplateClick(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", FileName = "нагрузка.xlsx" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            using var fs = File.Create(dlg.FileName);
            SchoolDataImporter.ExportTemplate(fs);
        }
        catch (Exception ex)
        {
            ErrorCard.Visibility = Visibility.Visible;
            ErrorList.ItemsSource = new[] { ex.Message };
        }
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Excel (*.xlsx)|*.xlsx" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            if (!int.TryParse(DaysBox.Text, out int days) || days <= 0 ||
                !int.TryParse(SlotsBox.Text, out int slots) || slots <= 0)
                throw new InvalidOperationException("Дни и уроки должны быть положительными числами.");
            using var fs = File.OpenRead(dlg.FileName);
            var rows = ExcelLoadExchange.ImportLoad(fs);
            await _session.ImportLoadAsync(rows, days, slots);
            ErrorCard.Visibility = Visibility.Collapsed;
            LoadAll();
        }
        catch (Exception ex)
        {
            var errs = _session.LastImportErrors;
            ErrorCard.Visibility = Visibility.Visible;
            ErrorList.ItemsSource = (errs.Count > 0 ? errs : new[] { ex.Message }).Take(8).ToList();
            LoadAll();
        }
    }

    // --- P3: ручной ввод строк нагрузки (без Excel) ---
    private int GridDays() =>
        int.TryParse(DaysBox.Text, out int days) && days > 0
            ? days : _session.Data?.DaysCount ?? 5;

    private int GridSlots() =>
        int.TryParse(SlotsBox.Text, out int slots) && slots > 0
            ? slots : _session.Data?.SlotsPerDay ?? 7;

    private IReadOnlyList<string> KnownClasses() => _session.LoadRows
        .Select(r => r.ClassName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();

    private IReadOnlyList<string> KnownSubjects() => _session.LoadRows
        .Select(r => r.SubjectName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();

    private IReadOnlyList<string> KnownTeachers() => _session.LoadRows
        .SelectMany(r => new[] { r.TeacherName, r.SplitTeacherBName })
        .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!)
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();

    private IReadOnlyList<string> KnownRooms() => _session.LoadRows
        .Select(r => r.RoomName).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!)
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();

    private LoadRowWindow OpenRowDialog(LoadRow? existing)
    {
        var dlg = new LoadRowWindow(existing,
            KnownClasses(), KnownSubjects(), KnownTeachers(), KnownRooms())
        {
            Owner = this,
        };
        return dlg;
    }

    private async Task ApplyManualRows(List<LoadRow> rows, int days, int slots)
    {
        try
        {
            await _session.SetManualRowsAsync(rows, days, slots);
            ErrorCard.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            var errs = _session.LastImportErrors;
            ErrorCard.Visibility = Visibility.Visible;
            ErrorList.ItemsSource = (errs.Count > 0 ? errs : new[] { ex.Message }).Take(8).ToList();
        }
        LoadAll();
    }

    private async void OnAddClick(object sender, RoutedEventArgs e)
    {
        var dlg = OpenRowDialog(null);
        if (dlg.ShowDialog() != true || dlg.Result is null) return;
        var rows = _session.LoadRows.Concat([dlg.Result]).ToList();
        await ApplyManualRows(rows, GridDays(), GridSlots());
    }

    private async void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (LoadGrid.SelectedItem is not LoadRowVm sel)
        {
            ErrorCard.Visibility = Visibility.Visible;
            ErrorList.ItemsSource = new[] { "Выберите строку в таблице, затем «Изменить»." };
            return;
        }
        var dlg = OpenRowDialog(_session.LoadRows[sel.SourceIndex]);
        if (dlg.ShowDialog() != true || dlg.Result is null) return;
        var rows = _session.LoadRows.ToList();
        rows[sel.SourceIndex] = dlg.Result;
        await ApplyManualRows(rows, GridDays(), GridSlots());
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (LoadGrid.SelectedItem is not LoadRowVm sel)
        {
            ErrorCard.Visibility = Visibility.Visible;
            ErrorList.ItemsSource = new[] { "Выберите строку в таблице, затем «Удалить»." };
            return;
        }
        var row = _session.LoadRows[sel.SourceIndex];
        if (MessageBox.Show(this,
                $"Удалить строку «{row.ClassName} — {row.SubjectName} ({row.TeacherName})»?",
                "Удаление строки", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var rows = _session.LoadRows.ToList();
        rows.RemoveAt(sel.SourceIndex);
        await ApplyManualRows(rows, GridDays(), GridSlots());
    }

    private async void OnGridApplyClick(object sender, RoutedEventArgs e)
    {
        if (!_session.HasData)
        {
            ErrorCard.Visibility = Visibility.Visible;
            ErrorList.ItemsSource = new[] { "Нет данных: загрузите Excel или добавьте первую строку кнопкой «Добавить»." };
            return;
        }
        if (!int.TryParse(DaysBox.Text, out int days) || days <= 0 ||
            !int.TryParse(SlotsBox.Text, out int slots) || slots <= 0)
        {
            ErrorCard.Visibility = Visibility.Visible;
            ErrorList.ItemsSource = new[] { "Дни и уроки должны быть положительными числами." };
            return;
        }
        await ApplyManualRows(_session.LoadRows.ToList(), days, slots);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
