using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Amsur.Application;
using Amsur.Domain;
using Microsoft.Win32;

namespace Amsur.Wpf.Views;

public sealed record LoadRowVm(
    int SourceIndex,
    string ClassName, string SubjectName, int Hours,
    string TeacherName, string RoomName, string GroupText);

// P3/R1–R9: редактируемые строки гибких настроек (plain settable — читаем по Apply).
public sealed class ClassRowVm
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int Grade { get; set; }
    public int StudentCount { get; set; } = 25;
    public string ClassTeacher { get; set; } = "—";
}

public sealed class AssignRowVm
{
    public string Teacher { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Scope { get; set; } = "Класс";
    public string Class { get; set; } = "—";
    public string Grade { get; set; } = "";
}

public sealed class SubjectRowVm
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int Difficulty { get; set; } = 5;
    public int MaxPerDay { get; set; } = 2;
}

public sealed class RoomRowVm
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Mode { get; set; } = "Универсальный";
    public string OnlySubject { get; set; } = "—";
    public int MaxGroups { get; set; } = 1;
    public int DesiredGroups { get; set; }
    public bool CountSubgroupAsGroup { get; set; } = true;
}

public sealed class HourNormVm
{
    public string Subject { get; set; } = "";
    public int Grade { get; set; }
    public int Hours { get; set; }
}

public sealed class HourOverrideVm
{
    public string Class { get; set; } = "";
    public string Subject { get; set; } = "";
    public int Hours { get; set; }
}

// P-D3: данные как view (логика из SchoolDataWindow 1-в-1).
public partial class DataView : UserControl
{
    public IViewNavigator? Navigator { get; set; }
    private const string None = "—";
    private static readonly string[] RoomModes = ["Универсальный", "Только вручную", "Только предмет"];
    private static readonly string[] AssignScopes = ["Класс", "Параллель"];
    private static readonly string[] AssignModes = ["Выключено", "Мягкое (подсветка)", "Строго на класс", "Строго на параллель"];
    private static readonly string[] WeekDays = ["Понедельник", "Вторник", "Среда", "Четверг", "Пятница"];

    private readonly AppSession _session;
    private ObservableCollection<LoadRowVm> _allLoad = [];
    private ObservableCollection<ClassRowVm> _classRows = [];
    private ObservableCollection<AssignRowVm> _assignRows = [];
    private ObservableCollection<SubjectRowVm> _subjectRows = [];
    private ObservableCollection<RoomRowVm> _roomRows = [];
    private ObservableCollection<HourNormVm> _normRows = [];
    private ObservableCollection<HourOverrideVm> _overrideRows = [];

    public DataView(AppSession session)
    {
        _session = session;
        InitializeComponent();
        SearchBox.ToolTip = "Поиск по классу, предмету или учителю";
        AssignModeBox.ItemsSource = AssignModes;
        CommonDayBox.ItemsSource = WeekDays;
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
        TabHours.Content = $"Часы ({_session.Flex.HourNorms.Count + _session.Flex.HourOverrides.Count})";

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

        var teacherNames = d.Teachers.OrderBy(t => t.Name).Select(t => t.Name).ToList();
        var subjectNames = d.Subjects.OrderBy(s => s.Name).Select(s => s.Name).ToList();
        var classNames = d.Classes.OrderBy(c => c.Name).Select(c => c.Name).ToList();
        string TN(Guid? id) => id.HasValue && d.Teachers.FirstOrDefault(t => t.Id == id.Value) is { } t ? t.Name : None;

        TeachersList.ItemsSource = d.Teachers.OrderBy(t => t.Name)
            .Select(t => $"{t.Name} — {d.Curriculum.Where(x => x.TeacherId == t.Id).Sum(x => x.HoursPerWeek)} ч/нед").ToList();
        GroupsList.ItemsSource = d.Groups.Count == 0
            ? new[] { "Делений на подгруппы нет." }
            : d.Groups.Select(g => $"{CN(g.ClassId)} — группа {g.Name}").ToList();
        NotesList.ItemsSource = d.Notes.Count == 0
            ? new[] { "Замечаний нет." }
            : d.Notes.Take(20).ToList();

        // P3: гибкие настройки → гриды.
        var flex = _session.Flex;
        _classRows = new ObservableCollection<ClassRowVm>(d.Classes.OrderBy(c => c.Name)
            .Select(c => new ClassRowVm
            {
                Id = c.Id, Name = c.Name, Grade = c.Grade, StudentCount = c.StudentCount,
                ClassTeacher = TN(c.ClassTeacherId),
            }));
        ClassesGrid.ItemsSource = _classRows;
        ClassTeacherCol.ItemsSource = new[] { None }.Concat(teacherNames).ToList();

        AssignModeBox.SelectedIndex = flex.Settings.AssignMode switch
        {
            TeacherAssignMode.Soft => 1,
            TeacherAssignMode.HardClass => 2,
            TeacherAssignMode.HardParallel => 3,
            _ => 0,
        };
        _assignRows = new ObservableCollection<AssignRowVm>(flex.Assignments
            .Select(a => new AssignRowVm
            {
                Teacher = a.TeacherName, Subject = a.SubjectName,
                Scope = a.Scope == AssignmentScope.Parallel ? "Параллель" : "Класс",
                Class = a.ClassName ?? None, Grade = a.Grade?.ToString() ?? "",
            }));
        AssignGrid.ItemsSource = _assignRows;
        AsTeacherCol.ItemsSource = teacherNames;
        AsSubjectCol.ItemsSource = subjectNames;
        AsScopeCol.ItemsSource = AssignScopes;
        AsClassCol.ItemsSource = new[] { None }.Concat(classNames).ToList();

        _subjectRows = new ObservableCollection<SubjectRowVm>(d.Subjects.OrderBy(s => s.Name)
            .Select(s => new SubjectRowVm
            {
                Id = s.Id, Name = s.Name, Difficulty = s.Difficulty, MaxPerDay = s.MaxPerDay,
            }));
        SubjectsGrid.ItemsSource = _subjectRows;

        _roomRows = new ObservableCollection<RoomRowVm>(d.Rooms.OrderBy(r => r.Name)
            .Select(r => new RoomRowVm
            {
                Id = r.Id, Name = r.Name,
                Mode = r.IsManualOnly ? RoomModes[1] : r.OnlySubjectId.HasValue ? RoomModes[2] : RoomModes[0],
                OnlySubject = r.OnlySubjectId.HasValue &&
                    d.Subjects.FirstOrDefault(s => s.Id == r.OnlySubjectId.Value) is { } os ? os.Name : None,
                MaxGroups = r.MaxSimultaneousGroups, DesiredGroups = r.DesiredGroups,
                CountSubgroupAsGroup = r.CountSubgroupAsGroup,
            }));
        RoomsGrid.ItemsSource = _roomRows;
        RoomModeCol.ItemsSource = RoomModes;
        RoomSubjectCol.ItemsSource = new[] { None }.Concat(subjectNames).ToList();

        _normRows = new ObservableCollection<HourNormVm>(flex.HourNorms
            .OrderBy(n => n.SubjectName).ThenBy(n => n.Grade)
            .Select(n => new HourNormVm { Subject = n.SubjectName, Grade = n.Grade, Hours = n.HoursPerWeek }));
        NormsGrid.ItemsSource = _normRows;
        NormSubjectCol.ItemsSource = subjectNames;
        _overrideRows = new ObservableCollection<HourOverrideVm>(flex.HourOverrides
            .OrderBy(o => o.ClassName).ThenBy(o => o.SubjectName)
            .Select(o => new HourOverrideVm { Class = o.ClassName, Subject = o.SubjectName, Hours = o.HoursPerWeek }));
        OverridesGrid.ItemsSource = _overrideRows;
        OvClassCol.ItemsSource = classNames;
        OvSubjectCol.ItemsSource = subjectNames;

        var common = flex.CommonLesson;
        CommonEnabledBox.IsChecked = common?.Enabled == true;
        CommonDayBox.SelectedIndex = common is null ? 3 : Math.Clamp(common.DayIndex, 0, 4);
        CommonSlotBox.Text = (common?.SlotIndex ?? 1).ToString();
        CommonSlot2Box.Text = (common?.SlotIndexShift2 ?? 0).ToString();
        CommonGradesBox.Text = common?.GradesCsv ?? "5,6,7,8,9,10,11";
        CommonOwnRoomsBox.IsChecked = common?.UseOwnRooms ?? true;

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
        HoursPanel.Visibility = i == 1 ? Visibility.Visible : Visibility.Collapsed;
        ClassesPanel.Visibility = i == 2 ? Visibility.Visible : Visibility.Collapsed;
        TeachersPanel.Visibility = i == 3 ? Visibility.Visible : Visibility.Collapsed;
        SubjectsPanel.Visibility = i == 4 ? Visibility.Visible : Visibility.Collapsed;
        RoomsPanel.Visibility = i == 5 ? Visibility.Visible : Visibility.Collapsed;
        GroupsList.Visibility = i == 6 ? Visibility.Visible : Visibility.Collapsed;
        CommonPanel.Visibility = i == 7 ? Visibility.Visible : Visibility.Collapsed;
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
            Owner = System.Windows.Window.GetWindow(this),
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
        if (MessageBox.Show(System.Windows.Window.GetWindow(this),
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

    // --- P3: применение гибких настроек (срез FlexDataset → сессия → персист) ---
    private async Task ApplyFlexSlice(Func<FlexDataset, FlexDataset> patch)
    {
        try
        {
            await _session.ApplyFlexAsync(patch(_session.Flex));
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

    private static TeacherAssignMode AssignModeFromIndex(int i) => i switch
    {
        1 => TeacherAssignMode.Soft,
        2 => TeacherAssignMode.HardClass,
        3 => TeacherAssignMode.HardParallel,
        _ => TeacherAssignMode.Off,
    };

    private void SayApplyError(string text)
    {
        ErrorCard.Visibility = Visibility.Visible;
        ErrorList.ItemsSource = new[] { text };
    }

    private async void OnClassesApplyClick(object sender, RoutedEventArgs e)
    {
        foreach (var r in _classRows)
        {
            if (r.Grade < 0 || r.Grade > 12) { SayApplyError($"Класс «{r.Name}»: параллель — 0..12."); return; }
            if (r.StudentCount <= 0) { SayApplyError($"Класс «{r.Name}»: учеников должно быть больше 0."); return; }
        }
        var rows = _classRows.Select(r => new ClassConfigRow(r.Name,
            r.ClassTeacher == None ? null : r.ClassTeacher, r.Grade, r.StudentCount)).ToList();
        await ApplyFlexSlice(f => f with { Classes = rows });
    }

    private async void OnTeachersApplyClick(object sender, RoutedEventArgs e)
    {
        var list = new List<TeacherAssignRow>();
        foreach (var r in _assignRows)
        {
            if (string.IsNullOrWhiteSpace(r.Teacher) || string.IsNullOrWhiteSpace(r.Subject))
            { SayApplyError("Назначение: укажите учителя и предмет (или удалите строку)."); return; }
            int? grade = null;
            if (!string.IsNullOrWhiteSpace(r.Grade))
            {
                if (!int.TryParse(r.Grade, out int g) || g < 1 || g > 12)
                { SayApplyError($"Назначение {r.Teacher} — {r.Subject}: параллель — 1..12."); return; }
                grade = g;
            }
            list.Add(new TeacherAssignRow(r.Teacher, r.Subject,
                r.Scope == "Параллель" ? AssignmentScope.Parallel : AssignmentScope.Class,
                r.Class == None ? null : r.Class, grade));
        }
        var mode = AssignModeFromIndex(AssignModeBox.SelectedIndex);
        await ApplyFlexSlice(f => f with
        {
            Assignments = list,
            Settings = f.Settings with { AssignMode = mode },
        });
    }

    private void OnAssignAddClick(object sender, RoutedEventArgs e)
    {
        var d = _session.Data;
        _assignRows.Add(new AssignRowVm
        {
            Teacher = d?.Teachers.OrderBy(t => t.Name).FirstOrDefault()?.Name ?? "",
            Subject = d?.Subjects.OrderBy(s => s.Name).FirstOrDefault()?.Name ?? "",
            Scope = "Класс",
            Class = d?.Classes.OrderBy(c => c.Name).FirstOrDefault()?.Name ?? None,
        });
    }

    private void OnAssignDelClick(object sender, RoutedEventArgs e)
    {
        if (AssignGrid.SelectedItem is AssignRowVm sel) _assignRows.Remove(sel);
        else SayApplyError("Выберите назначение в таблице, затем «Удалить».");
    }

    private async void OnSubjectsApplyClick(object sender, RoutedEventArgs e)
    {
        foreach (var r in _subjectRows)
            if (r.Difficulty < 1 || r.Difficulty > 10)
            { SayApplyError($"Предмет «{r.Name}»: сложность — 1..10."); return; }
        var rows = _subjectRows.Select(r => new SubjectDifficultyRow(r.Name, r.Difficulty)).ToList();
        await ApplyFlexSlice(f => f with { SubjectDifficulty = rows });
    }

    private async void OnRoomsApplyClick(object sender, RoutedEventArgs e)
    {
        var list = new List<RoomConfigRow>();
        foreach (var r in _roomRows)
        {
            if (r.MaxGroups < 1) { SayApplyError($"Кабинет «{r.Name}»: максимум групп — от 1."); return; }
            if (r.DesiredGroups < 0 || r.DesiredGroups > r.MaxGroups)
            { SayApplyError($"Кабинет «{r.Name}»: желательно — 0 (авто) или 1..{r.MaxGroups}."); return; }
            bool manual = r.Mode == RoomModes[1];
            bool only = r.Mode == RoomModes[2];
            if (only && (string.IsNullOrWhiteSpace(r.OnlySubject) || r.OnlySubject == None))
            { SayApplyError($"Кабинет «{r.Name}»: режим «Только предмет» требует предмет."); return; }
            list.Add(new RoomConfigRow(r.Name, manual, only ? r.OnlySubject : null,
                r.MaxGroups, r.DesiredGroups, r.CountSubgroupAsGroup));
        }
        await ApplyFlexSlice(f => f with { Rooms = list });
    }

    private async void OnHoursApplyClick(object sender, RoutedEventArgs e)
    {
        foreach (var n in _normRows)
        {
            if (string.IsNullOrWhiteSpace(n.Subject)) { SayApplyError("Норма: укажите предмет."); return; }
            if (n.Grade < 0 || n.Grade > 12) { SayApplyError($"Норма «{n.Subject}»: параллель — 0 (дефолт)..12."); return; }
            if (n.Hours <= 0) { SayApplyError($"Норма «{n.Subject}»: часов должно быть больше 0."); return; }
        }
        foreach (var o in _overrideRows)
        {
            if (string.IsNullOrWhiteSpace(o.Class) || string.IsNullOrWhiteSpace(o.Subject))
            { SayApplyError("Переопределение: укажите класс и предмет."); return; }
            if (o.Hours <= 0) { SayApplyError($"Переопределение «{o.Class} — {o.Subject}»: часов больше 0."); return; }
        }
        var norms = _normRows.Select(n => new HourNormRow(n.Subject, n.Grade, n.Hours)).ToList();
        var ovs = _overrideRows.Select(o => new HourOverrideRow(o.Class, o.Subject, o.Hours)).ToList();
        await ApplyFlexSlice(f => f with { HourNorms = norms, HourOverrides = ovs });
    }

    private void OnNormAddClick(object sender, RoutedEventArgs e)
    {
        _normRows.Add(new HourNormVm
        {
            Subject = _session.Data?.Subjects.OrderBy(s => s.Name).FirstOrDefault()?.Name ?? "",
        });
    }

    private void OnNormDelClick(object sender, RoutedEventArgs e)
    {
        if (NormsGrid.SelectedItem is HourNormVm sel) _normRows.Remove(sel);
        else SayApplyError("Выберите норму в таблице, затем «Удалить».");
    }

    private void OnOverrideAddClick(object sender, RoutedEventArgs e)
    {
        var d = _session.Data;
        _overrideRows.Add(new HourOverrideVm
        {
            Class = d?.Classes.OrderBy(c => c.Name).FirstOrDefault()?.Name ?? "",
            Subject = d?.Subjects.OrderBy(s => s.Name).FirstOrDefault()?.Name ?? "",
        });
    }

    private void OnOverrideDelClick(object sender, RoutedEventArgs e)
    {
        if (OverridesGrid.SelectedItem is HourOverrideVm sel) _overrideRows.Remove(sel);
        else SayApplyError("Выберите строку в таблице, затем «Удалить».");
    }

    private async void OnCommonApplyClick(object sender, RoutedEventArgs e)
    {
        bool enabled = CommonEnabledBox.IsChecked == true;
        int day = CommonDayBox.SelectedIndex >= 0 ? CommonDayBox.SelectedIndex : 3;
        if (!int.TryParse(CommonSlotBox.Text, out int slot) || slot < 1)
        { SayApplyError("Общий урок: номер урока — число от 1."); return; }
        if (!int.TryParse(CommonSlot2Box.Text, out int slot2) || slot2 < 0)
        { SayApplyError("Общий урок: урок 2-й смены — число от 0 (0 = нет второй группы)."); return; }
        string grades = (CommonGradesBox.Text ?? "").Trim();
        var row = new CommonLessonRow(enabled, day, slot, grades,
            CommonOwnRoomsBox.IsChecked == true, slot2);
        await ApplyFlexSlice(f => f with { CommonLesson = row });
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Navigator?.NavigateTo("Dashboard");
}
