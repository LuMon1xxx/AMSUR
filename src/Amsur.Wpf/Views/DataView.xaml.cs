using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;
using Microsoft.Win32;

namespace Amsur.Wpf.Views;

public sealed record LoadRowVm(
    int SourceIndex,
    string ClassName, string SubjectName, int Hours,
    string TeacherName, string RoomName, string GroupText,
    string PairText, string ShiftText);

public sealed class ClassRowVm
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int Grade { get; set; }
    public int StudentCount { get; set; } = 25;
    public int MaxLessonsPerDay { get; set; } = 6;
    public string ShiftLabel { get; set; } = "1-я";
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

public sealed class TeacherRowVm : INotifyPropertyChanged
{
    private string _unavailSummary = "—";
    public string Name { get; set; } = "";
    public int HoursPerWeek { get; set; }
    public int SubjectCount { get; set; }
    public int ClassCount { get; set; }
    public int MaxLessonsPerDay { get; set; } = 6;

    public string UnavailSummary
    {
        get => _unavailSummary;
        set
        {
            if (_unavailSummary == value) return;
            _unavailSummary = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnavailSummary)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class GroupRowVm
{
    public string ClassName { get; set; } = "";
    public string GroupName { get; set; } = "";
    public int LoadRowCount { get; set; }
    public int Hours { get; set; }
}

public partial class DataView : UserControl
{
    public IViewNavigator? Navigator { get; set; }
    private const string None = "—";
    private static readonly string[] RoomModes = ["Универсальный", "Только вручную", "Только предмет"];
    private static readonly string[] AssignScopes = ["Класс", "Параллель"];
    private static readonly string[] AssignModes = ["Выключено", "Мягкое (подсветка)", "Строго на класс", "Строго на параллель"];
    private static readonly string[] WeekDays = ["Понедельник", "Вторник", "Среда", "Четверг", "Пятница", "Суббота"];
    private static readonly string[] ShortDays = ["Пн", "Вт", "Ср", "Чт", "Пт", "Сб"];
    private static readonly string[] ShiftLabels = ["1-я", "2-я"];

    private readonly AppSession _session;
    private ObservableCollection<LoadRowVm> _allLoad = [];
    private ObservableCollection<ClassRowVm> _classRows = [];
    private ObservableCollection<AssignRowVm> _assignRows = [];
    private ObservableCollection<SubjectRowVm> _subjectRows = [];
    private ObservableCollection<RoomRowVm> _roomRows = [];
    private ObservableCollection<HourNormVm> _normRows = [];
    private ObservableCollection<HourOverrideVm> _overrideRows = [];
    private ObservableCollection<TeacherRowVm> _teacherRows = [];
    private ObservableCollection<GroupRowVm> _groupRows = [];

    private readonly Dictionary<string, HashSet<int>> _workingDaysOff = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<(int Day, int Slot)>> _workingUnavailSlots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _editedUnavailTeachers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _extraMatrixSubjects = [];
    private string? _selectedTeacher;
    private (string Subject, string Class)? _matrixSelectedCell;
    private bool _isMatrixMode;

    public DataView(AppSession session)
    {
        _session = session;
        InitializeComponent();
        AssignModeBox.ItemsSource = AssignModes;
        CommonDayBox.ItemsSource = WeekDays;
        ClassShiftCol.ItemsSource = ShiftLabels;
        Loaded += OnViewLoaded;
        Unloaded += OnViewUnloaded;
        LoadAll();
    }

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app)
        {
            app.ThemeChanged -= OnThemeChanged;
            app.ThemeChanged += OnThemeChanged;
        }
    }

    private void OnViewUnloaded(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app)
            app.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(string _)
    {
        if (_isMatrixMode) BuildHoursMatrix();
        BuildTeacherScheduleGrid();
    }

    private void LoadAll(bool clearErrorCard = true)
    {
        var d = _session.Data;
        var flex = _session.Flex;
        _editedUnavailTeachers.Clear();
        _workingDaysOff.Clear();
        _workingUnavailSlots.Clear();

        if (d is null)
        {
            SummaryText.Text = "Данные не загружены — откройте «Мастер школы» или добавьте данные.";
            CountText.Text = "";
            _allLoad = [];
            _classRows = [];
            _teacherRows = [];
            _subjectRows = [];
            _roomRows = new ObservableCollection<RoomRowVm>(flex.Rooms.Select(r => new RoomRowVm
            {
                Name = r.RoomName,
                Mode = r.IsManualOnly ? RoomModes[1] : r.OnlySubjectName is not null ? RoomModes[2] : RoomModes[0],
                OnlySubject = r.OnlySubjectName ?? None,
                MaxGroups = r.MaxGroups,
                DesiredGroups = r.DesiredGroups,
                CountSubgroupAsGroup = r.CountSubgroupAsGroup,
            }));
            _groupRows = [];
            LoadGrid.ItemsSource = _allLoad;
            ClassesGrid.ItemsSource = _classRows;
            TeachersGrid.ItemsSource = _teacherRows;
            SubjectsGrid.ItemsSource = _subjectRows;
            RoomsGrid.ItemsSource = _roomRows;
            RoomModeCol.ItemsSource = RoomModes;
            RoomSubjectCol.ItemsSource = new[] { None }.Concat(CanonicalSubjects.All).ToList();
            GroupsGrid.ItemsSource = _groupRows;
            TeachersList.ItemsSource = Array.Empty<string>();
            NotesList.ItemsSource = new[] { "Нет загруженных данных." };
            NotesExpander.Header = "Замечания";
            TabLoad.Content = "Нагрузка";
            TabClasses.Content = "Классы";
            TabTeachers.Content = "Учителя";
            TabSubjects.Content = "Предметы";
            TabRooms.Content = $"Кабинеты ({_roomRows.Count})";
            TabGroups.Content = "Подгруппы";
            TabHours.Content = $"Часы ({flex.HourNorms.Count + flex.HourOverrides.Count})";
            LoadFlexSharedTabs(flex, [], [], GridDays());
            if (clearErrorCard && _session.LastImportErrors.Count == 0)
                ErrorCard.Visibility = Visibility.Collapsed;
            CapacityBanner.Visibility = Visibility.Collapsed;
            UpdateEmptyStateVisibility();
            BuildHoursMatrix();
            BuildTeacherScheduleGrid();
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
        TabHours.Content = $"Часы ({flex.HourNorms.Count + flex.HourOverrides.Count})";

        _allLoad = new ObservableCollection<LoadRowVm>(_session.LoadRows
            .Select((r, i) => (Row: r, Index: i))
            .OrderBy(x => x.Row.ClassName).ThenBy(x => x.Row.SubjectName)
            .Select(x => new LoadRowVm(x.Index, x.Row.ClassName, x.Row.SubjectName, x.Row.HoursPerWeek,
                x.Row.TeacherName, x.Row.RoomName ?? None, x.Row.SplitSubgroups ? "A/B" : "Весь класс",
                x.Row.PairName ?? None, x.Row.Shift == 2 ? "2-я" : "1-я")));
        LoadGrid.ItemsSource = _allLoad;
        DaysBox.Text = d.DaysCount.ToString();
        SlotsBox.Text = d.SlotsPerDay.ToString();
        ClassFilterBox.ItemsSource = new[] { "Все классы" }
            .Concat(d.Classes.OrderBy(c => c.Name).Select(c => c.Name)).ToList();
        ClassFilterBox.SelectedIndex = 0;

        var teacherNames = d.Teachers.OrderBy(t => t.Name).Select(t => t.Name).ToList();
        var classNames = d.Classes.OrderBy(c => c.Name).Select(c => c.Name).ToList();
        string TN(Guid? id) => id.HasValue && d.Teachers.FirstOrDefault(t => t.Id == id.Value) is { } t ? t.Name : None;

        foreach (var t in d.Teachers)
        {
            _workingDaysOff[t.Name] = d.DaysOff
                .Where(x => x.TeacherId == t.Id)
                .Select(x => x.DayIndex)
                .ToHashSet();
            _workingUnavailSlots[t.Name] = d.Unavailability
                .Where(u => u.TeacherId == t.Id && u.Kind == AvailabilityKind.Forbidden)
                .Select(u => (u.DayIndex, u.SlotIndex))
                .ToHashSet();
        }

        TeachersList.ItemsSource = d.Teachers.OrderBy(t => t.Name)
            .Select(t => $"{t.Name} — {d.Curriculum.Where(x => x.TeacherId == t.Id).Sum(x => x.HoursPerWeek)} ч/нед").ToList();
        _teacherRows = new ObservableCollection<TeacherRowVm>(d.Teachers.OrderBy(t => t.Name)
            .Select(t => new TeacherRowVm
            {
                Name = t.Name,
                HoursPerWeek = d.Curriculum.Where(x => x.TeacherId == t.Id).Sum(x => x.HoursPerWeek),
                SubjectCount = d.Curriculum.Where(x => x.TeacherId == t.Id).Select(x => x.SubjectId).Distinct().Count(),
                ClassCount = d.Curriculum.Where(x => x.TeacherId == t.Id).Select(x => x.ClassId).Distinct().Count(),
                MaxLessonsPerDay = t.MaxLessonsPerDay,
                UnavailSummary = FormatUnavailSummary(t.Name),
            }));
        TeachersGrid.ItemsSource = _teacherRows;
        if (_selectedTeacher is null || !_teacherRows.Any(x => string.Equals(x.Name, _selectedTeacher, StringComparison.OrdinalIgnoreCase)))
            _selectedTeacher = _teacherRows.FirstOrDefault()?.Name;
        if (_selectedTeacher is not null)
            TeachersGrid.SelectedItem = _teacherRows.FirstOrDefault(x => string.Equals(x.Name, _selectedTeacher, StringComparison.OrdinalIgnoreCase));

        _groupRows = new ObservableCollection<GroupRowVm>(d.Groups
            .OrderBy(g => CN(g.ClassId)).ThenBy(g => g.Name)
            .Select(g =>
            {
                string clsName = CN(g.ClassId);
                var splitRows = _session.LoadRows
                    .Where(r => string.Equals(r.ClassName, clsName, StringComparison.OrdinalIgnoreCase)
                        && r.SplitSubgroups).ToList();
                var pairItems = d.Curriculum.Where(c => c.GroupId == g.Id).ToList();
                return new GroupRowVm
                {
                    ClassName = clsName,
                    GroupName = g.Name,
                    LoadRowCount = splitRows.Count + pairItems.Count,
                    Hours = splitRows.Sum(r => r.HoursPerWeek) + pairItems.Sum(c => c.HoursPerWeek),
                };
            }));
        GroupsGrid.ItemsSource = _groupRows;
        NotesList.ItemsSource = d.Notes.Count == 0
            ? new[] { "Замечаний нет." }
            : d.Notes.Take(20).ToList();
        NotesExpander.Header = d.Notes.Count == 0
            ? "Замечания"
            : $"Замечания ({d.Notes.Count})";

        _classRows = new ObservableCollection<ClassRowVm>(d.Classes.OrderBy(c => c.Grade).ThenBy(c => c.Name)
            .Select(c => new ClassRowVm
            {
                Id = c.Id,
                Name = c.Name,
                Grade = c.Grade,
                StudentCount = c.StudentCount,
                MaxLessonsPerDay = c.MaxLessonsPerDay,
                ShiftLabel = _session.LoadRows.Any(r =>
                    string.Equals(r.ClassName, c.Name, StringComparison.OrdinalIgnoreCase) && r.Shift == 2)
                    ? "2-я" : "1-я",
                ClassTeacher = TN(c.ClassTeacherId),
            }));
        ClassesGrid.ItemsSource = _classRows;
        ClassShiftCol.ItemsSource = ShiftLabels;
        ClassTeacherCol.ItemsSource = new[] { None }.Concat(teacherNames).ToList();

        _subjectRows = new ObservableCollection<SubjectRowVm>(d.Subjects.OrderBy(s => s.Name)
            .Select(s => new SubjectRowVm
            {
                Id = s.Id,
                Name = s.Name,
                Difficulty = s.Difficulty,
                MaxPerDay = s.MaxPerDay,
            }));
        SubjectsGrid.ItemsSource = _subjectRows;

        var roomVms = d.Rooms.OrderBy(r => r.Name)
            .Select(r => new RoomRowVm
            {
                Id = r.Id,
                Name = r.Name,
                Mode = r.IsManualOnly ? RoomModes[1] : r.OnlySubjectId.HasValue ? RoomModes[2] : RoomModes[0],
                OnlySubject = r.OnlySubjectId.HasValue &&
                    d.Subjects.FirstOrDefault(s => s.Id == r.OnlySubjectId.Value) is { } os ? os.Name : None,
                MaxGroups = r.MaxSimultaneousGroups,
                DesiredGroups = r.DesiredGroups,
                CountSubgroupAsGroup = r.CountSubgroupAsGroup,
            }).ToList();
        foreach (var fr in flex.Rooms)
        {
            if (roomVms.Any(x => string.Equals(x.Name, fr.RoomName, StringComparison.OrdinalIgnoreCase)))
                continue;
            roomVms.Add(new RoomRowVm
            {
                Name = fr.RoomName,
                Mode = fr.IsManualOnly ? RoomModes[1] : fr.OnlySubjectName is not null ? RoomModes[2] : RoomModes[0],
                OnlySubject = fr.OnlySubjectName ?? None,
                MaxGroups = fr.MaxGroups,
                DesiredGroups = fr.DesiredGroups,
                CountSubgroupAsGroup = fr.CountSubgroupAsGroup,
            });
        }
        _roomRows = new ObservableCollection<RoomRowVm>(roomVms.OrderBy(r => r.Name));
        RoomsGrid.ItemsSource = _roomRows;
        RoomModeCol.ItemsSource = RoomModes;
        RoomSubjectCol.ItemsSource = new[] { None }.Concat(KnownSubjects()).ToList();

        LoadFlexSharedTabs(flex, teacherNames, classNames, d.DaysCount);

        var errs = _session.LastImportErrors;
        if (errs.Count > 0)
        {
            ErrorCard.Visibility = Visibility.Visible;
            ErrorList.ItemsSource = errs.Take(8).ToList();
        }
        else if (clearErrorCard)
        {
            ErrorCard.Visibility = Visibility.Collapsed;
        }

        UpdateEmptyStateVisibility();
        RefreshCapacityBanner();
        ApplyFilter();
        BuildHoursMatrix();
        BuildTeacherScheduleGrid();
    }

    private void LoadFlexSharedTabs(
        FlexDataset flex, IReadOnlyList<string> teacherNames, IReadOnlyList<string> classNames, int daysCount)
    {
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
                Teacher = a.TeacherName,
                Subject = a.SubjectName,
                Scope = a.Scope == AssignmentScope.Parallel ? "Параллель" : "Класс",
                Class = a.ClassName ?? None,
                Grade = a.Grade?.ToString() ?? "",
            }));
        AssignGrid.ItemsSource = _assignRows;
        AsTeacherCol.ItemsSource = teacherNames;
        AsSubjectCol.ItemsSource = KnownSubjects();
        AsScopeCol.ItemsSource = AssignScopes;
        AsClassCol.ItemsSource = new[] { None }.Concat(classNames).ToList();

        _normRows = new ObservableCollection<HourNormVm>(flex.HourNorms
            .OrderBy(n => n.SubjectName).ThenBy(n => n.Grade)
            .Select(n => new HourNormVm { Subject = n.SubjectName, Grade = n.Grade, Hours = n.HoursPerWeek }));
        NormsGrid.ItemsSource = _normRows;
        NormSubjectCol.ItemsSource = KnownSubjects();
        _overrideRows = new ObservableCollection<HourOverrideVm>(flex.HourOverrides
            .OrderBy(o => o.ClassName).ThenBy(o => o.SubjectName)
            .Select(o => new HourOverrideVm { Class = o.ClassName, Subject = o.SubjectName, Hours = o.HoursPerWeek }));
        OverridesGrid.ItemsSource = _overrideRows;
        OvClassCol.ItemsSource = classNames;
        OvSubjectCol.ItemsSource = KnownSubjects();

        var common = flex.CommonLesson;
        CommonEnabledBox.IsChecked = common?.Enabled == true;
        CommonDayBox.SelectedIndex = common is null ? 3 : Math.Clamp(common.DayIndex, 0, Math.Min(WeekDays.Length - 1, Math.Max(0, daysCount - 1)));
        CommonDayBox.IsEnabled = true;
        CommonDayBox.ToolTip = "Обычно — четверг первым уроком смены (можно изменить).";
        CommonSlotBox.Text = (common?.SlotIndex ?? 1).ToString();
        CommonSlot2Box.Text = (common?.SlotIndexShift2 ?? 0).ToString();
        CommonGradesBox.Text = common?.GradesCsv ?? "5,6,7,8,9,10,11";
        CommonOwnRoomsBox.IsChecked = common?.UseOwnRooms ?? true;
    }

    private void UpdateEmptyStateVisibility()
    {
        bool showLoad = TabList.SelectedIndex <= 0;
        EmptyCard.Visibility = showLoad && !_isMatrixMode && !_session.HasData
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void RefreshCapacityBanner()
    {
        if (!_session.HasData)
        {
            CapacityBanner.Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            var issues = CapacityDiagnostics.Analyze(_session.BuildProblem(), _session.QualityRules);
            if (issues.Count == 0)
            {
                CapacityBanner.Visibility = Visibility.Collapsed;
                return;
            }
            CapacityList.ItemsSource = issues.Take(6).ToList();
            CapacityBanner.Visibility = Visibility.Visible;
        }
        catch
        {
            CapacityBanner.Visibility = Visibility.Collapsed;
        }
    }

    private string FormatUnavailSummary(string teacherName)
    {
        _workingDaysOff.TryGetValue(teacherName, out var daysOff);
        _workingUnavailSlots.TryGetValue(teacherName, out var slots);
        int dayCount = daysOff?.Count ?? 0;
        int slotCount = slots is null ? 0 : slots.Count(s => daysOff is null || !daysOff.Contains(s.Day));
        if (dayCount == 0 && slotCount == 0) return None;
        var parts = new List<string>();
        if (dayCount > 0)
        {
            var names = daysOff!.Where(i => i >= 0 && i < ShortDays.Length).OrderBy(i => i).Select(i => ShortDays[i]);
            parts.Add($"Вых: {string.Join(",", names)}");
        }
        if (slotCount > 0) parts.Add($"{slotCount} ур.");
        return string.Join("; ", parts);
    }

    private void SayStatus(string message)
    {
        ErrorCard.Visibility = Visibility.Collapsed;
        StatusBannerText.Text = message;
        StatusBanner.Visibility = Visibility.Visible;
    }

    private void ClearStatus() => StatusBanner.Visibility = Visibility.Collapsed;

    private void ApplyFilter()
    {
        string q = (SearchBox.Text ?? "").Trim().ToLowerInvariant();
        string? cls = ClassFilterBox.SelectedItem as string;
        var rows = _allLoad.Where(r =>
            (string.IsNullOrEmpty(q) || r.ClassName.ToLowerInvariant().Contains(q) ||
             r.SubjectName.ToLowerInvariant().Contains(q) || r.TeacherName.ToLowerInvariant().Contains(q)) &&
            (cls is null || cls == "Все классы" || r.ClassName == cls)).ToList();
        LoadGrid.ItemsSource = rows;
        if (TabList.SelectedIndex <= 0 && !_isMatrixMode)
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

    private void OnLoadTableModeClick(object sender, RoutedEventArgs e) => SetLoadMode(false);

    private void OnLoadMatrixModeClick(object sender, RoutedEventArgs e) => SetLoadMode(true);

    private void SetLoadMode(bool matrix)
    {
        _isMatrixMode = matrix;
        LoadTableModeBtn.SetResourceReference(StyleProperty, matrix ? "BtnSecondary" : "BtnPrimary");
        LoadMatrixModeBtn.SetResourceReference(StyleProperty, matrix ? "BtnPrimary" : "BtnSecondary");
        SearchBox.Visibility = matrix ? Visibility.Collapsed : Visibility.Visible;
        ClassFilterBox.Visibility = matrix ? Visibility.Collapsed : Visibility.Visible;
        LoadAddBtn.Visibility = matrix ? Visibility.Collapsed : Visibility.Visible;
        LoadEditBtn.Visibility = matrix ? Visibility.Collapsed : Visibility.Visible;
        LoadDeleteBtn.Visibility = matrix ? Visibility.Collapsed : Visibility.Visible;
        bool onLoadTab = TabList.SelectedIndex <= 0;
        LoadGrid.Visibility = onLoadTab && !matrix ? Visibility.Visible : Visibility.Collapsed;
        HoursMatrixPanel.Visibility = onLoadTab && matrix ? Visibility.Visible : Visibility.Collapsed;
        UpdateEmptyStateVisibility();
        if (matrix)
        {
            BuildHoursMatrix();
            CountText.Text = $"Классов: {_classRows.Count} · Предметов: {MatrixSubjectList().Count}";
        }
        else
        {
            ApplyFilter();
        }
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        int i = TabList.SelectedIndex;
        bool onLoad = i <= 0;
        LoadToolbar.Visibility = onLoad ? Visibility.Visible : Visibility.Collapsed;
        LoadGrid.Visibility = onLoad && !_isMatrixMode ? Visibility.Visible : Visibility.Collapsed;
        HoursMatrixPanel.Visibility = onLoad && _isMatrixMode ? Visibility.Visible : Visibility.Collapsed;
        HoursPanel.Visibility = i == 1 ? Visibility.Visible : Visibility.Collapsed;
        ClassesPanel.Visibility = i == 2 ? Visibility.Visible : Visibility.Collapsed;
        TeachersPanel.Visibility = i == 3 ? Visibility.Visible : Visibility.Collapsed;
        SubjectsPanel.Visibility = i == 4 ? Visibility.Visible : Visibility.Collapsed;
        RoomsPanel.Visibility = i == 5 ? Visibility.Visible : Visibility.Collapsed;
        GroupsPanel.Visibility = i == 6 ? Visibility.Visible : Visibility.Collapsed;
        CommonPanel.Visibility = i == 7 ? Visibility.Visible : Visibility.Collapsed;
        UpdateEmptyStateVisibility();

        if (i == 0)
        {
            if (_isMatrixMode) CountText.Text = $"Классов: {_classRows.Count} · Предметов: {MatrixSubjectList().Count}";
            else ApplyFilter();
        }
        else
        {
            int n = i switch
            {
                1 => _normRows.Count + _overrideRows.Count,
                2 => _classRows.Count,
                3 => _teacherRows.Count,
                4 => _subjectRows.Count,
                5 => _roomRows.Count,
                6 => _groupRows.Count,
                _ => 0,
            };
            CountText.Text = n > 0 ? $"Записей: {n}" : "";
        }
    }

    // --- Мастер школы ---
    private async void OnWizardClick(object sender, RoutedEventArgs e)
    {
        ClearStatus();
        var dlg = new SchoolWizardWindow { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || dlg.ResultRows is null || dlg.ResultFlex is null)
            return;
        if (!ConfirmReplaceData("Мастер школы"))
            return;
        try
        {
            await _session.ImportWizardAsync(dlg.ResultRows, dlg.ResultFlex, dlg.ResultDays, dlg.ResultSlots);
            ErrorCard.Visibility = Visibility.Collapsed;
            LoadAll();
            SayStatus($"Школа создана Мастером: {dlg.ResultRows.Count} строк нагрузки, сетка {dlg.ResultDays}×{dlg.ResultSlots}.");
        }
        catch (Exception ex)
        {
            var errs = _session.LastImportErrors;
            ErrorCard.Visibility = Visibility.Visible;
            ErrorList.ItemsSource = (errs.Count > 0 ? errs : new[] { ex.Message }).Take(8).ToList();
            LoadAll(clearErrorCard: false);
        }
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

    private bool ConfirmReplaceData(string action)
    {
        if (!_session.HasData) return true;
        return MessageBox.Show(Window.GetWindow(this),
            $"{action} заменит текущие данные школы. Загруженные строки и справочники будут перезаписаны. Продолжить?",
            "Замена данных", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        ClearStatus();
        var dlg = new OpenFileDialog { Filter = "Excel (*.xlsx)|*.xlsx" };
        if (dlg.ShowDialog() != true) return;
        if (!ConfirmReplaceData("Загрузка Excel")) return;
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
            LoadAll(clearErrorCard: false);
        }
    }

    private async void OnDemoClick(object sender, RoutedEventArgs e)
    {
        ClearStatus();
        if (!ConfirmReplaceData("Демо-набор")) return;
        try
        {
            await _session.ImportDemoAsync();
            ErrorCard.Visibility = Visibility.Collapsed;
            LoadAll();
        }
        catch (Exception ex)
        {
            var errs = _session.LastImportErrors;
            ErrorCard.Visibility = Visibility.Visible;
            ErrorList.ItemsSource = (errs.Count > 0 ? errs : new[] { ex.Message }).Take(8).ToList();
            LoadAll(clearErrorCard: false);
        }
    }

    // --- Ручной ввод строк нагрузки ---
    private int GridDays() =>
        int.TryParse(DaysBox.Text, out int days) && days > 0
            ? days : _session.Data?.DaysCount ?? 5;

    private int GridSlots() =>
        int.TryParse(SlotsBox.Text, out int slots) && slots > 0
            ? slots : _session.Data?.SlotsPerDay ?? 8;

    private IReadOnlyList<string> KnownClasses() => _session.LoadRows
        .Select(r => r.ClassName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();

    private IReadOnlyList<string> KnownSubjects() => _session.LoadRows
        .Select(r => r.SubjectName)
        .Concat(_session.Data?.Subjects.Select(s => s.Name) ?? [])
        .Concat(_extraMatrixSubjects)
        .Concat(CanonicalSubjects.All)
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();

    private IReadOnlyList<string> KnownTeachers() => _session.LoadRows
        .SelectMany(r => new[] { r.TeacherName, r.SplitTeacherBName })
        .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!)
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();

    private IReadOnlyList<string> KnownRooms() => _session.LoadRows
        .Select(r => r.RoomName)
        .Concat(_session.Data?.Rooms.Select(r => r.Name) ?? [])
        .Concat(_roomRows.Select(r => r.Name))
        .Concat(_session.Flex.Rooms.Select(r => r.RoomName))
        .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!)
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();

    private LoadRowWindow OpenRowDialog(LoadRow? existing)
    {
        int? InheritShift(string cls) =>
            _session.LoadRows
                .Where(r => string.Equals(r.ClassName, cls, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Shift ?? 1)
                .Cast<int?>()
                .FirstOrDefault();
        return new LoadRowWindow(existing,
            KnownClasses(), KnownSubjects(), KnownTeachers(), KnownRooms(),
            inheritShift: InheritShift)
        {
            Owner = Window.GetWindow(this),
        };
    }

    private async Task<bool> ApplyManualRows(List<LoadRow> rows, int days, int slots)
    {
        try
        {
            await _session.SetManualRowsAsync(rows, days, slots);
            ErrorCard.Visibility = Visibility.Collapsed;
            LoadAll();
            return true;
        }
        catch (Exception ex)
        {
            var errs = _session.LastImportErrors;
            ErrorCard.Visibility = Visibility.Visible;
            ErrorList.ItemsSource = (errs.Count > 0 ? errs : new[] { ex.Message }).Take(8).ToList();
            LoadAll(clearErrorCard: false);
            return false;
        }
    }

    private async void OnAddClick(object sender, RoutedEventArgs e)
    {
        ClearStatus();
        var dlg = OpenRowDialog(null);
        if (dlg.ShowDialog() != true || dlg.Result is null) return;
        var rows = _session.LoadRows.Concat([dlg.Result]).ToList();
        await ApplyManualRows(rows, GridDays(), GridSlots());
    }

    private async void OnEditClick(object sender, RoutedEventArgs e)
    {
        ClearStatus();
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

    private void OnLoadGridDoubleClick(object sender, MouseButtonEventArgs e) => OnEditClick(sender, e);

    private void OnLoadGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            OnDeleteClick(sender, e);
            e.Handled = true;
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        ClearStatus();
        var selected = LoadGrid.SelectedItems.OfType<LoadRowVm>()
            .OrderByDescending(r => r.SourceIndex).ToList();
        if (selected.Count == 0)
        {
            ErrorCard.Visibility = Visibility.Visible;
            ErrorList.ItemsSource = new[] { "Выберите строку в таблице, затем «Удалить»." };
            return;
        }
        string question = selected.Count == 1
            ? $"Удалить строку «{_session.LoadRows[selected[0].SourceIndex].ClassName} — " +
              $"{_session.LoadRows[selected[0].SourceIndex].SubjectName} " +
              $"({_session.LoadRows[selected[0].SourceIndex].TeacherName})»?"
            : $"Удалить выбранные строки нагрузки ({selected.Count})?";
        if (MessageBox.Show(Window.GetWindow(this), question,
                "Удаление строки", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var rows = _session.LoadRows.ToList();
        foreach (var s in selected)
            rows.RemoveAt(s.SourceIndex);
        await ApplyManualRows(rows, GridDays(), GridSlots());
    }

    private async void OnGridApplyClick(object sender, RoutedEventArgs e)
    {
        ClearStatus();
        if (!_session.HasData)
        {
            ErrorCard.Visibility = Visibility.Visible;
            ErrorList.ItemsSource = new[] { "Нет данных: откройте «Мастер школы», загрузите Excel или добавьте первую строку." };
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

    // --- Матрица часов (Предмет × Класс) ---
    private List<string> MatrixSubjectList() => _session.LoadRows
        .Select(r => r.SubjectName)
        .Concat(_extraMatrixSubjects)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(s => s)
        .ToList();

    private void BuildHoursMatrix()
    {
        HoursMatrixGrid.Children.Clear();
        HoursMatrixGrid.RowDefinitions.Clear();
        HoursMatrixGrid.ColumnDefinitions.Clear();

        MatrixTeacherBox.ItemsSource = KnownTeachers();
        MatrixRoomBox.ItemsSource = KnownRooms();
        MatrixTeacherBBox.ItemsSource = KnownTeachers();
        MatrixNewSubjectBox.ItemsSource = CanonicalSubjects.All;

        var d = _session.Data;
        var classes = d is null
            ? new List<ClassRowVm>()
            : _classRows.OrderBy(c => c.Grade).ThenBy(c => c.Name).ToList();
        var subjects = MatrixSubjectList();
        if (classes.Count == 0 || subjects.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "Пока нет классов для матрицы. Создайте школу через «Мастер школы» или добавьте первый класс во вкладке «Классы».",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4),
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "BTextSoft");
            HoursMatrixGrid.Children.Add(empty);
            return;
        }

        HoursMatrixGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        foreach (var _ in classes)
            HoursMatrixGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });

        // Заголовок
        HoursMatrixGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddMatrixHeaderCell(0, 0, "Предмет");
        for (int ci = 0; ci < classes.Count; ci++)
        {
            var c = classes[ci];
            string label = c.ShiftLabel == "2-я" ? $"{c.Name}\n2-я" : c.Name;
            AddMatrixHeaderCell(0, ci + 1, label);
        }

        // Строки предметов
        for (int si = 0; si < subjects.Count; si++)
        {
            string subj = subjects[si];
            int row = si + 1;
            HoursMatrixGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var subLbl = new TextBlock
            {
                Text = subj,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 2, 6, 2),
                ToolTip = subj,
            };
            subLbl.SetResourceReference(TextBlock.ForegroundProperty, "BText");
            Grid.SetRow(subLbl, row);
            Grid.SetColumn(subLbl, 0);
            HoursMatrixGrid.Children.Add(subLbl);

            for (int ci = 0; ci < classes.Count; ci++)
            {
                string cls = classes[ci].Name;
                var matching = _session.LoadRows
                    .Where(r => string.Equals(r.ClassName, cls, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(r.SubjectName, subj, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                int hours = matching.Sum(x => x.HoursPerWeek);
                bool selected = _matrixSelectedCell is { } sel &&
                    string.Equals(sel.Subject, subj, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(sel.Class, cls, StringComparison.OrdinalIgnoreCase);

                string text = hours == 0 ? "·"
                    : matching.Count > 1 ? $"{hours}*"
                    : matching[0].SplitSubgroups ? $"{hours} A/B"
                    : hours.ToString();

                var btn = new Button
                {
                    Content = text,
                    Margin = new Thickness(1.5),
                    Padding = new Thickness(2, 4, 2, 4),
                    MinHeight = 28,
                    FontSize = 12,
                    FontWeight = hours > 0 ? FontWeights.SemiBold : FontWeights.Normal,
                    BorderThickness = new Thickness(1),
                    Tag = (subj, cls),
                    ToolTip = matching.Count == 0
                        ? $"{cls} · {subj}: нет нагрузки (клик для добавления)"
                        : $"{cls} · {subj}: {hours} ч/нед ({matching[0].TeacherName})",
                };
                btn.SetResourceReference(Control.BackgroundProperty,
                    selected ? "BAccentSoft" : hours > 0 ? "BSubtle" : "BCard");
                btn.SetResourceReference(Control.BorderBrushProperty,
                    selected ? "BAccentBorder" : "BBorder");
                btn.SetResourceReference(Control.ForegroundProperty,
                    selected ? "BAccentText" : hours > 0 ? "BText" : "BTextSoft");
                btn.Click += OnMatrixCellClick;
                Grid.SetRow(btn, row);
                Grid.SetColumn(btn, ci + 1);
                HoursMatrixGrid.Children.Add(btn);
            }
        }

        // Итоговая строка
        int totalRow = subjects.Count + 1;
        HoursMatrixGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var totLbl = new TextBlock
        {
            Text = "Итого / лимит",
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Margin = new Thickness(4, 6, 6, 2),
        };
        totLbl.SetResourceReference(TextBlock.ForegroundProperty, "BText");
        Grid.SetRow(totLbl, totalRow);
        Grid.SetColumn(totLbl, 0);
        HoursMatrixGrid.Children.Add(totLbl);

        int days = d?.DaysCount ?? 5;
        for (int ci = 0; ci < classes.Count; ci++)
        {
            var c = classes[ci];
            int totalH = _session.LoadRows
                .Where(r => string.Equals(r.ClassName, c.Name, StringComparison.OrdinalIgnoreCase))
                .Sum(r => r.HoursPerWeek);
            int maxCap = days * c.MaxLessonsPerDay;
            bool over = totalH > maxCap;

            var badge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(1.5, 4, 1.5, 2),
                Padding = new Thickness(2, 3, 2, 3),
                BorderThickness = new Thickness(1),
                ToolTip = $"Недельная нагрузка {totalH} ч при макс. вместимости {maxCap} ч ({days}×{c.MaxLessonsPerDay})",
            };
            badge.SetResourceReference(Border.BackgroundProperty, over ? "BBadBg" : "BCard");
            badge.SetResourceReference(Border.BorderBrushProperty, over ? "BBad" : "BBorder");

            var tb = new TextBlock
            {
                Text = $"{totalH}/{maxCap}",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, over ? "BBad" : "BTextSoft");
            badge.Child = tb;
            Grid.SetRow(badge, totalRow);
            Grid.SetColumn(badge, ci + 1);
            HoursMatrixGrid.Children.Add(badge);
        }
    }

    private void AddMatrixHeaderCell(int row, int col, string text)
    {
        var hdr = new Border
        {
            Padding = new Thickness(4, 5, 4, 5),
            Margin = new Thickness(1.5),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
        };
        hdr.SetResourceReference(Border.BackgroundProperty, "BSubtle");
        hdr.SetResourceReference(Border.BorderBrushProperty, "BBorder");
        var tb = new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = col == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Center,
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "BText");
        hdr.Child = tb;
        Grid.SetRow(hdr, row);
        Grid.SetColumn(hdr, col);
        HoursMatrixGrid.Children.Add(hdr);
    }

    private void OnMatrixCellClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ValueTuple<string, string> key }) return;
        SelectMatrixCell(key.Item1, key.Item2);
    }

    private void SelectMatrixCell(string subject, string className)
    {
        _matrixSelectedCell = (subject, className);
        var matches = _session.LoadRows
            .Where(r => string.Equals(r.ClassName, className, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(r.SubjectName, subject, StringComparison.OrdinalIgnoreCase))
            .ToList();

        MatrixCellLabel.Text = matches.Count > 1
            ? $"{className} · {subject} (строка 1 из {matches.Count})"
            : $"{className} · {subject}";

        if (matches.Count > 0)
        {
            var first = matches[0];
            MatrixHoursBox.Text = first.HoursPerWeek.ToString();
            MatrixTeacherBox.Text = first.TeacherName;
            MatrixRoomBox.Text = first.RoomName ?? "";
            MatrixSplitBox.IsChecked = first.SplitSubgroups;
            MatrixTeacherBBox.Text = first.SplitTeacherBName ?? "";
            MatrixTeacherBBox.Visibility = first.SplitSubgroups ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            MatrixHoursBox.Text = "2";
            string? suggestedTeacher = _session.LoadRows
                .FirstOrDefault(r => string.Equals(r.SubjectName, subject, StringComparison.OrdinalIgnoreCase))
                ?.TeacherName;
            MatrixTeacherBox.Text = suggestedTeacher ?? $"Учитель ({subject})";
            MatrixRoomBox.Text = "";
            MatrixSplitBox.IsChecked = false;
            MatrixTeacherBBox.Text = "";
            MatrixTeacherBBox.Visibility = Visibility.Collapsed;
        }
        BuildHoursMatrix();
    }

    private void OnMatrixDecClick(object sender, RoutedEventArgs e)
    {
        int cur = int.TryParse(MatrixHoursBox.Text, out int v) ? v : 0;
        MatrixHoursBox.Text = Math.Max(0, cur - 1).ToString();
    }

    private void OnMatrixIncClick(object sender, RoutedEventArgs e)
    {
        int cur = int.TryParse(MatrixHoursBox.Text, out int v) ? v : 0;
        MatrixHoursBox.Text = Math.Min(20, cur + 1).ToString();
    }

    private void OnMatrixSplitChanged(object sender, RoutedEventArgs e) =>
        MatrixTeacherBBox.Visibility = MatrixSplitBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void OnMatrixAddSubjectClick(object sender, RoutedEventArgs e)
    {
        string subj = (MatrixNewSubjectBox.Text ?? "").Trim();
        if (subj.Length == 0) { SayApplyError("Введите название нового предмета для матрицы."); return; }
        if (!_extraMatrixSubjects.Any(x => string.Equals(x, subj, StringComparison.OrdinalIgnoreCase)))
            _extraMatrixSubjects.Add(subj);
        MatrixNewSubjectBox.Text = "";
        BuildHoursMatrix();
        SayStatus($"Предмет «{subj}» добавлен в матрицу — кликните любую ячейку в его строке.");
    }

    private async void OnMatrixApplyClick(object sender, RoutedEventArgs e)
    {
        ClearStatus();
        if (_matrixSelectedCell is not { } cell)
        {
            SayApplyError("Сначала кликните ячейку (предмет × класс) в матрице.");
            return;
        }
        if (!int.TryParse(MatrixHoursBox.Text, out int hours) || hours < 0 || hours > 20)
        {
            SayApplyError("Часов в неделю должно быть число 0..20.");
            return;
        }

        string cls = cell.Class;
        string subj = cell.Subject;
        var rows = _session.LoadRows.ToList();
        int firstIdx = rows.FindIndex(r =>
            string.Equals(r.ClassName, cls, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.SubjectName, subj, StringComparison.OrdinalIgnoreCase));

        if (hours == 0)
        {
            int removed = rows.RemoveAll(r =>
                string.Equals(r.ClassName, cls, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.SubjectName, subj, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                SayStatus($"Для {cls} по предмету «{subj}» уже 0 часов.");
                return;
            }
            if (!await ApplyManualRows(rows, GridDays(), GridSlots())) return;
            SayStatus($"Предмет «{subj}» удалён из нагрузки класса {cls}.");
            return;
        }

        string teacher = (MatrixTeacherBox.Text ?? "").Trim();
        if (teacher.Length == 0) { SayApplyError("Укажите учителя для ячейки."); return; }
        bool split = MatrixSplitBox.IsChecked == true;
        string? teacherB = split ? (MatrixTeacherBBox.Text ?? "").Trim() : null;
        if (split && string.IsNullOrWhiteSpace(teacherB))
        { SayApplyError("При делении на подгруппы A/B укажите второго учителя."); return; }
        if (split && string.Equals(teacher, teacherB, StringComparison.OrdinalIgnoreCase))
        { SayApplyError("Учителя подгрупп A и B не могут совпадать."); return; }
        string? room = string.IsNullOrWhiteSpace(MatrixRoomBox.Text) ? null : MatrixRoomBox.Text.Trim();

        int? inheritedShift = rows
            .Where(r => string.Equals(r.ClassName, cls, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Shift)
            .FirstOrDefault(s => s == 2);

        if (firstIdx >= 0)
        {
            var cur = rows[firstIdx];
            rows[firstIdx] = cur with
            {
                HoursPerWeek = hours,
                TeacherName = teacher,
                SplitSubgroups = split,
                SplitTeacherBName = teacherB,
                RoomName = room,
            };
        }
        else
        {
            rows.Add(new LoadRow(cls, subj, hours, teacher, split, teacherB, room,
                null, null, null, inheritedShift));
        }
        if (!await ApplyManualRows(rows, GridDays(), GridSlots())) return;
        SelectMatrixCell(subj, cls);
        SayStatus($"Сохранено: {cls} · {subj} = {hours} ч/нед ({teacher}).");
    }

    // --- Классы: быстрое создание ---
    private async void OnAddClassClick(object sender, RoutedEventArgs e)
    {
        ClearStatus();
        string clsName = NewClassNameBox.Text.Trim();
        if (clsName.Length == 0) { SayApplyError("Укажите название нового класса (например, 5В)."); return; }
        if (_classRows.Any(c => string.Equals(c.Name, clsName, StringComparison.OrdinalIgnoreCase)))
        { SayApplyError($"Класс «{clsName}» уже есть в списке."); return; }
        if (!int.TryParse(NewClassGradeBox.Text, out int grade) || grade < 1 || grade > 11)
        { SayApplyError("Параллель нового класса — число 1..11."); return; }
        int shift = NewClassShiftBox.SelectedIndex == 1 ? 2 : 1;
        bool usePreset = NewClassPresetBox.IsChecked == true;

        var rows = _session.LoadRows.ToList();
        var specs = usePreset
            ? SchoolSetupPresets.StandardCurriculumHours(grade)
            : new[] { new StarterSubjectSpec("Математика", 4) };

        int idx = 1;
        foreach (var spec in specs)
        {
            string? defaultTeacher = rows
                .FirstOrDefault(r => string.Equals(r.SubjectName, spec.SubjectName, StringComparison.OrdinalIgnoreCase))
                ?.TeacherName;
            string teacher = defaultTeacher ?? $"Учитель {idx} ({spec.SubjectName})";
            string? teacherB = spec.SplitSubgroups
                ? rows.Where(r => string.Equals(r.SubjectName, spec.SubjectName, StringComparison.OrdinalIgnoreCase))
                      .SelectMany(r => new[] { r.TeacherName, r.SplitTeacherBName })
                      .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t) &&
                                           !string.Equals(t, teacher, StringComparison.OrdinalIgnoreCase))
                  ?? $"{teacher} (гр. Б)"
                : null;
            rows.Add(new LoadRow(clsName, spec.SubjectName, spec.HoursPerWeek, teacher,
                spec.SplitSubgroups, teacherB, spec.RoomName, null, null, null, shift == 2 ? 2 : null));
            idx++;
        }

        var classes = _session.Flex.Classes.ToList();
        classes.RemoveAll(c => string.Equals(c.ClassName, clsName, StringComparison.OrdinalIgnoreCase));
        classes.Add(new ClassConfigRow(clsName, null, grade, 25));
        var limits = (_session.Flex.ClassLimits ?? []).ToList();
        limits.RemoveAll(l => string.Equals(l.ClassName, clsName, StringComparison.OrdinalIgnoreCase));
        limits.Add(new ClassLimitRow(clsName, SchoolSetupPresets.StarterClassDailyLimit(grade)));

        var flex = _session.Flex with { Classes = classes, ClassLimits = limits };
        int days = GridDays();
        int slots = Math.Max(GridSlots(), shift == 2 ? 12 : GridSlots());

        try
        {
            await _session.SaveManualSetupAsync(rows, flex, days, slots);
            NewClassNameBox.Text = "";
            ErrorCard.Visibility = Visibility.Collapsed;
            LoadAll();
            SayStatus($"Добавлен класс «{clsName}» ({grade}-я параллель, {shift}-я смена, строк: {specs.Count}).");
        }
        catch (Exception ex)
        {
            var errs = _session.LastImportErrors;
            ErrorCard.Visibility = Visibility.Visible;
            ErrorList.ItemsSource = (errs.Count > 0 ? errs : new[] { ex.Message }).Take(8).ToList();
        }
    }

    // --- Учителя: переименование и визуальная недоступность ---
    private void OnTeacherGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TeachersGrid.SelectedItem is not TeacherRowVm vm) return;
        _selectedTeacher = vm.Name;
        BuildTeacherScheduleGrid();
    }

    private void BuildTeacherScheduleGrid()
    {
        TeacherDaysOffPanel.Children.Clear();
        TeacherScheduleGrid.Children.Clear();
        TeacherScheduleGrid.RowDefinitions.Clear();
        TeacherScheduleGrid.ColumnDefinitions.Clear();

        if (_selectedTeacher is null || !_session.HasData)
        {
            SelectedTeacherTitle.Text = "Выберите учителя в списке";
            TeacherUnavailNote.Text = "";
            return;
        }

        SelectedTeacherTitle.Text = _selectedTeacher;
        int days = _session.Data?.DaysCount ?? 5;
        int slots = _session.Data?.SlotsPerDay ?? 8;

        if (!_workingDaysOff.TryGetValue(_selectedTeacher, out var daysOff))
            daysOff = _workingDaysOff[_selectedTeacher] = [];
        if (!_workingUnavailSlots.TryGetValue(_selectedTeacher, out var unavail))
            unavail = _workingUnavailSlots[_selectedTeacher] = [];

        for (int d = 0; d < Math.Min(days, ShortDays.Length); d++)
        {
            int dayIdx = d;
            bool isOff = daysOff.Contains(dayIdx);
            var dayBtn = new Button
            {
                Content = ShortDays[dayIdx],
                MinWidth = 38,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 4, 0),
                BorderThickness = new Thickness(1),
                FontSize = 12,
                FontWeight = isOff ? FontWeights.SemiBold : FontWeights.Normal,
                ToolTip = isOff ? $"{WeekDays[dayIdx]}: методический/выходной день (нажмите, чтобы открыть)"
                    : $"{WeekDays[dayIdx]}: рабочий день (нажмите, чтобы закрыть целиком)",
            };
            dayBtn.SetResourceReference(Control.BackgroundProperty, isOff ? "BBadBg" : "BSubtle");
            dayBtn.SetResourceReference(Control.BorderBrushProperty, isOff ? "BBad" : "BBorder");
            dayBtn.SetResourceReference(Control.ForegroundProperty, isOff ? "BBad" : "BText");
            dayBtn.Click += (_, _) =>
            {
                ToggleTeacherDayOff(dayIdx);
            };
            TeacherDaysOffPanel.Children.Add(dayBtn);
        }

        TeacherScheduleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        for (int d = 0; d < days; d++)
            TeacherScheduleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 42 });

        TeacherScheduleGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var corner = new TextBlock { Text = "Ур.", FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        corner.SetResourceReference(TextBlock.ForegroundProperty, "BTextSoft");
        Grid.SetRow(corner, 0);
        Grid.SetColumn(corner, 0);
        TeacherScheduleGrid.Children.Add(corner);

        for (int d = 0; d < days; d++)
        {
            var hdr = new TextBlock
            {
                Text = d < ShortDays.Length ? ShortDays[d] : $"Д{d + 1}",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 3),
            };
            hdr.SetResourceReference(TextBlock.ForegroundProperty, daysOff.Contains(d) ? "BBad" : "BText");
            Grid.SetRow(hdr, 0);
            Grid.SetColumn(hdr, d + 1);
            TeacherScheduleGrid.Children.Add(hdr);
        }

        for (int s = 1; s <= slots; s++)
        {
            int slotIdx = s;
            TeacherScheduleGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var rowHdr = new TextBlock
            {
                Text = slotIdx.ToString(),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            rowHdr.SetResourceReference(TextBlock.ForegroundProperty, "BTextSoft");
            Grid.SetRow(rowHdr, slotIdx);
            Grid.SetColumn(rowHdr, 0);
            TeacherScheduleGrid.Children.Add(rowHdr);

            for (int d = 0; d < days; d++)
            {
                int dayIdx = d;
                bool dayClosed = daysOff.Contains(dayIdx);
                bool forbidden = dayClosed || unavail.Contains((dayIdx, slotIdx));

                var cellBtn = new Button
                {
                    Content = dayClosed ? "Вых" : forbidden ? "×" : "",
                    MinHeight = 22,
                    Margin = new Thickness(1.5),
                    Padding = new Thickness(0),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    BorderThickness = new Thickness(1),
                    IsEnabled = !dayClosed,
                    ToolTip = dayClosed
                        ? $"{ShortDays[dayIdx]}: закрыт весь день"
                        : forbidden
                            ? $"{ShortDays[dayIdx]}, {slotIdx} урок: запрещён (клик — разрешить)"
                            : $"{ShortDays[dayIdx]}, {slotIdx} урок: разрешён (клик — запретить)",
                };
                cellBtn.SetResourceReference(Control.BackgroundProperty, forbidden ? "BBadBg" : "BCard");
                cellBtn.SetResourceReference(Control.BorderBrushProperty, forbidden ? "BBad" : "BBorder");
                cellBtn.SetResourceReference(Control.ForegroundProperty, forbidden ? "BBad" : "BTextSoft");
                cellBtn.Click += (_, _) => ToggleTeacherSlot(dayIdx, slotIdx);
                Grid.SetRow(cellBtn, slotIdx);
                Grid.SetColumn(cellBtn, dayIdx + 1);
                TeacherScheduleGrid.Children.Add(cellBtn);
            }
        }

        string summary = FormatUnavailSummary(_selectedTeacher);
        TeacherUnavailNote.Text = summary == None
            ? "Ограничений нет (доступен на все уроки)."
            : $"Ограничения: {summary}";
    }

    private void ToggleTeacherDayOff(int dayIdx)
    {
        if (_selectedTeacher is null) return;
        if (!_workingDaysOff.TryGetValue(_selectedTeacher, out var daysOff))
            daysOff = _workingDaysOff[_selectedTeacher] = [];
        if (!daysOff.Add(dayIdx)) daysOff.Remove(dayIdx);
        _editedUnavailTeachers.Add(_selectedTeacher);
        UpdateTeacherSummaryRow(_selectedTeacher);
        BuildTeacherScheduleGrid();
    }

    private void ToggleTeacherSlot(int dayIdx, int slotIdx)
    {
        if (_selectedTeacher is null) return;
        if (!_workingUnavailSlots.TryGetValue(_selectedTeacher, out var slots))
            slots = _workingUnavailSlots[_selectedTeacher] = [];
        var key = (dayIdx, slotIdx);
        if (!slots.Add(key)) slots.Remove(key);
        _editedUnavailTeachers.Add(_selectedTeacher);
        UpdateTeacherSummaryRow(_selectedTeacher);
        BuildTeacherScheduleGrid();
    }

    private void OnClearTeacherUnavailClick(object sender, RoutedEventArgs e)
    {
        if (_selectedTeacher is null) return;
        _workingDaysOff[_selectedTeacher] = [];
        _workingUnavailSlots[_selectedTeacher] = [];
        _editedUnavailTeachers.Add(_selectedTeacher);
        UpdateTeacherSummaryRow(_selectedTeacher);
        BuildTeacherScheduleGrid();
    }

    private void UpdateTeacherSummaryRow(string teacherName)
    {
        var vm = _teacherRows.FirstOrDefault(x => string.Equals(x.Name, teacherName, StringComparison.OrdinalIgnoreCase));
        if (vm is not null)
            vm.UnavailSummary = FormatUnavailSummary(teacherName);
    }

    private async void OnRenameTeacherClick(object sender, RoutedEventArgs e)
    {
        ClearStatus();
        string? oldName = (TeachersGrid.SelectedItem as TeacherRowVm)?.Name ?? _selectedTeacher;
        if (string.IsNullOrWhiteSpace(oldName))
        {
            SayApplyError("Выберите учителя в таблице слева, затем введите новое ФИО.");
            return;
        }
        string newName = RenameTeacherBox.Text.Trim();
        if (newName.Length == 0)
        {
            SayApplyError("Введите новое ФИО учителя.");
            return;
        }
        try
        {
            var (rows, flex) = SchoolSetupPresets.RenameTeacher(_session.LoadRows, _session.Flex, oldName, newName);
            await _session.SaveManualSetupAsync(rows, flex, GridDays(), GridSlots());
            RenameTeacherBox.Text = "";
            _selectedTeacher = newName;
            ErrorCard.Visibility = Visibility.Collapsed;
            LoadAll();
            SayStatus($"Учитель «{oldName}» переименован в «{newName}» по всей нагрузке и настройкам.");
        }
        catch (Exception ex)
        {
            SayApplyError(ex.Message);
        }
    }

    // --- Предметы и Кабинеты ---
    private void OnApplySubjectPresetClick(object sender, RoutedEventArgs e)
    {
        ClearStatus();
        int updated = 0;
        foreach (var row in _subjectRows)
        {
            if (SchoolPresets.SubjectDifficulties.TryGetValue(row.Name, out int diff) && row.Difficulty != diff)
            {
                row.Difficulty = diff;
                updated++;
            }
        }
        SubjectsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        SubjectsGrid.Items.Refresh();
        SayStatus($"Подставлена сложность РБ ({updated} измен.). Нажмите «Сохранить изменения» слева внизу.");
    }

    private void OnAddRoomClick(object sender, RoutedEventArgs e)
    {
        ClearStatus();
        string name = NewRoomBox.Text.Trim();
        if (name.Length == 0) { SayApplyError("Введите название или номер нового кабинета."); return; }
        if (_roomRows.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
        { SayApplyError($"Кабинет «{name}» уже есть в списке."); return; }
        var created = new RoomRowVm { Name = name };
        _roomRows.Add(created);
        RoomsGrid.SelectedItem = created;
        NewRoomBox.Text = "";
        SayStatus($"Кабинет «{name}» добавлен (доступен при выборе). Нажмите «Сохранить изменения» для записи в базу.");
    }

    // --- Единое сохранение всех вкладок ---
    private void CommitAllGrids()
    {
        ClassesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        TeachersGrid.CommitEdit(DataGridEditingUnit.Row, true);
        AssignGrid.CommitEdit(DataGridEditingUnit.Row, true);
        SubjectsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        RoomsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        NormsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        OverridesGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private async void OnSaveAllClick(object sender, RoutedEventArgs e)
    {
        ClearStatus();
        CommitAllGrids();

        foreach (var r in _classRows)
        {
            if (r.Grade < 0 || r.Grade > 12) { SayApplyError($"Класс «{r.Name}»: параллель — 0..12."); return; }
            if (r.StudentCount <= 0) { SayApplyError($"Класс «{r.Name}»: учеников должно быть больше 0."); return; }
            if (r.MaxLessonsPerDay < 1 || r.MaxLessonsPerDay > 10)
            { SayApplyError($"Класс «{r.Name}»: макс/день — 1..10."); return; }
        }
        var classRows = _classRows.Select(r => new ClassConfigRow(r.Name,
            r.ClassTeacher == None ? null : r.ClassTeacher, r.Grade, r.StudentCount)).ToList();
        var classLimits = _classRows.Select(r => new ClassLimitRow(r.Name, r.MaxLessonsPerDay)).ToList();

        foreach (var t in _teacherRows)
        {
            if (t.MaxLessonsPerDay < 1 || t.MaxLessonsPerDay > 12)
            { SayApplyError($"Учитель «{t.Name}»: макс/день — 1..12."); return; }
        }
        var teacherLimits = _teacherRows.Select(t => new TeacherLimitRow(t.Name, t.MaxLessonsPerDay)).ToList();

        var assignRows = new List<TeacherAssignRow>();
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
            assignRows.Add(new TeacherAssignRow(r.Teacher, r.Subject,
                r.Scope == "Параллель" ? AssignmentScope.Parallel : AssignmentScope.Class,
                r.Class == None ? null : r.Class, grade));
        }

        foreach (var r in _subjectRows)
        {
            if (r.Difficulty < 1 || r.Difficulty > 10)
            { SayApplyError($"Предмет «{r.Name}»: сложность — 1..10."); return; }
            if (r.MaxPerDay < 1 || r.MaxPerDay > 6)
            { SayApplyError($"Предмет «{r.Name}»: макс/день — 1..6."); return; }
        }
        var subjectRows = _subjectRows.Select(r => new SubjectDifficultyRow(r.Name, r.Difficulty)).ToList();
        var subjectLimits = _subjectRows.Select(r => new SubjectLimitRow(r.Name, r.MaxPerDay)).ToList();

        var roomRows = new List<RoomConfigRow>();
        foreach (var r in _roomRows)
        {
            if (r.MaxGroups < 1) { SayApplyError($"Кабинет «{r.Name}»: максимум групп — от 1."); return; }
            if (r.DesiredGroups < 0 || r.DesiredGroups > r.MaxGroups)
            { SayApplyError($"Кабинет «{r.Name}»: желательно — 0 (авто) или 1..{r.MaxGroups}."); return; }
            bool manual = r.Mode == RoomModes[1];
            bool only = r.Mode == RoomModes[2];
            if (only && (string.IsNullOrWhiteSpace(r.OnlySubject) || r.OnlySubject == None))
            { SayApplyError($"Кабинет «{r.Name}»: режим «Только предмет» требует предмет."); return; }
            roomRows.Add(new RoomConfigRow(r.Name, manual, only ? r.OnlySubject : null,
                r.MaxGroups, r.DesiredGroups, r.CountSubgroupAsGroup));
        }

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

        bool commonEnabled = CommonEnabledBox.IsChecked == true;
        int commonDay = CommonDayBox.SelectedIndex >= 0 ? CommonDayBox.SelectedIndex : 3;
        if (!int.TryParse(CommonSlotBox.Text, out int commonSlot) || commonSlot < 1)
        { SayApplyError("Общий урок: номер урока — число от 1."); return; }
        if (!int.TryParse(CommonSlot2Box.Text, out int commonSlot2) || commonSlot2 < 0)
        { SayApplyError("Общий урок: урок 2-й смены — число от 0 (0 = нет второй группы)."); return; }
        var commonRow = new CommonLessonRow(commonEnabled, commonDay, commonSlot,
            (CommonGradesBox.Text ?? "").Trim(), CommonOwnRoomsBox.IsChecked == true, commonSlot2);

        int days = _session.Data?.DaysCount ?? GridDays();
        int slots = _session.Data?.SlotsPerDay ?? GridSlots();

        var teacherDaysOff = _workingDaysOff
            .SelectMany(kv => kv.Value
                .Where(day => day >= 0 && day < days)
                .OrderBy(day => day)
                .Select(day => new TeacherDayOffRow(kv.Key, day)))
            .ToList();

        var teacherUnavailSlots = _workingUnavailSlots
            .SelectMany(kv => kv.Value
                .Where(u => u.Day >= 0 && u.Day < days && u.Slot >= 1 && u.Slot <= slots)
                .OrderBy(u => u.Day).ThenBy(u => u.Slot)
                .Select(u => new TeacherUnavailSlotRow(kv.Key, u.Day, u.Slot)))
            .ToList();

        var mode = AssignModeFromIndex(AssignModeBox.SelectedIndex);
        var nextFlex = _session.Flex with
        {
            Classes = classRows,
            ClassLimits = classLimits,
            TeacherLimits = teacherLimits,
            SubjectLimits = subjectLimits,
            TeacherDaysOff = teacherDaysOff,
            TeacherUnavailSlots = teacherUnavailSlots,
            Assignments = assignRows,
            Settings = _session.Flex.Settings with { AssignMode = mode },
            SubjectDifficulty = subjectRows,
            Rooms = roomRows,
            HourNorms = norms,
            HourOverrides = ovs,
            CommonLesson = commonRow,
        };

        IReadOnlyList<LoadRow> updatedRows = _session.LoadRows;
        bool rowsChanged = false;
        if (_session.HasData)
        {
            var working = _session.LoadRows.ToList();
            if (_editedUnavailTeachers.Count > 0)
            {
                for (int i = 0; i < working.Count; i++)
                {
                    if (_editedUnavailTeachers.Contains(working[i].TeacherName.Trim()) &&
                        (working[i].UnavailDays is not null || working[i].UnavailSlots is not null))
                    {
                        working[i] = working[i] with { UnavailDays = null, UnavailSlots = null };
                        rowsChanged = true;
                    }
                }
            }

            IReadOnlyList<LoadRow> shifted = working;
            foreach (var c in _classRows)
            {
                int targetShift = c.ShiftLabel == "2-я" ? 2 : 1;
                int currentShift = shifted
                    .Where(r => string.Equals(r.ClassName, c.Name, StringComparison.OrdinalIgnoreCase))
                    .Select(r => r.Shift ?? 1)
                    .FirstOrDefault(1);
                if (currentShift != targetShift)
                {
                    shifted = SchoolSetupPresets.ApplyClassShift(shifted, c.Name, targetShift);
                    rowsChanged = true;
                }
            }
            updatedRows = shifted;
        }

        if (!await ConfirmCapRaisesAsync())
        {
            SayApplyError("Отменено — дневные лимиты не изменены.");
            return;
        }

        bool needs12Slots = _session.HasData && _classRows.Any(c => c.ShiftLabel == "2-я") && slots < 12;
        int nextSlots = needs12Slots ? 12 : slots;

        try
        {
            if (_session.HasData && (rowsChanged || nextSlots != slots))
                await _session.SaveManualSetupAsync(updatedRows, nextFlex, days, nextSlots);
            else
                await _session.ApplyFlexAsync(nextFlex);
            ErrorCard.Visibility = Visibility.Collapsed;
            LoadAll();
            SayStatus(needs12Slots
                ? "Изменения сохранены (сетка расширена до 12 уроков для 2-й смены)."
                : "Изменения всех вкладок сохранены в базу.");
        }
        catch (Exception ex)
        {
            var errs = _session.LastImportErrors;
            ErrorCard.Visibility = Visibility.Visible;
            ErrorList.ItemsSource = (errs.Count > 0 ? errs : new[] { ex.Message }).Take(8).ToList();
            LoadAll(clearErrorCard: false);
        }
    }

    private async Task<bool> ConfirmCapRaisesAsync()
    {
        var data = _session.Data;
        if (data is null) return true;
        var raises = new List<string>();
        foreach (var r in _classRows)
        {
            var cur = data.Classes.FirstOrDefault(c => c.Id == r.Id ||
                string.Equals(c.Name, r.Name, StringComparison.OrdinalIgnoreCase));
            if (cur is null) continue;
            int schoolCap = SchoolSetupPresets.StarterClassDailyLimit(cur.Grade);
            if (r.MaxLessonsPerDay > schoolCap && r.MaxLessonsPerDay > cur.MaxLessonsPerDay)
                raises.Add($"{r.Name}: {cur.MaxLessonsPerDay}→{r.MaxLessonsPerDay}/день (лимит школы {schoolCap})");
        }
        foreach (var t in _teacherRows)
        {
            var cur = data.Teachers.FirstOrDefault(x =>
                string.Equals(x.Name, t.Name, StringComparison.OrdinalIgnoreCase));
            if (cur is not null && t.MaxLessonsPerDay > 6 && t.MaxLessonsPerDay > cur.MaxLessonsPerDay)
                raises.Add($"{t.Name}: {cur.MaxLessonsPerDay}→{t.MaxLessonsPerDay}/день");
        }
        if (raises.Count == 0) return true;
        string details = raises.Count > 5
            ? string.Join("; ", raises.Take(5)) + $" и ещё {raises.Count - 5}."
            : string.Join("; ", raises) + ".";
        return await _session.ConfirmDangerousAsync(
            Window.GetWindow(this),
            "cap-raise",
            "Повышение дневных лимитов",
            "Дневные лимиты выше школьного правила: нагрузка сверх лимита школы. Снижение лимитов — без предупреждения. " +
            "Сверх школьного лимита: " + details);
    }

    private async Task ApplyFlexSlice(Func<FlexDataset, FlexDataset> patch)
    {
        try
        {
            await _session.ApplyFlexAsync(patch(_session.Flex));
            ErrorCard.Visibility = Visibility.Collapsed;
            LoadAll();
        }
        catch (Exception ex)
        {
            var errs = _session.LastImportErrors;
            ErrorCard.Visibility = Visibility.Visible;
            ErrorList.ItemsSource = (errs.Count > 0 ? errs : new[] { ex.Message }).Take(8).ToList();
            LoadAll(clearErrorCard: false);
        }
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
}
