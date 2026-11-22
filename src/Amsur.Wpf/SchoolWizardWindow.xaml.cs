using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Amsur.Application;
using Amsur.Domain;

namespace Amsur.Wpf;

public sealed class WizardGradeRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private int _classCount;
    private string _letters = "АБВГДЕ";
    private string _shiftLabel = "1-я";

    public int Grade { get; init; }
    public string GradeLabel => $"{Grade} класс";

    public int ClassCount
    {
        get => _classCount;
        set
        {
            int clamped = Math.Clamp(value, 0, 6);
            if (_classCount == clamped) return;
            _classCount = clamped;
            Notify(nameof(ClassCount));
            Notify(nameof(Preview));
        }
    }

    public string Letters
    {
        get => _letters;
        set
        {
            string clean = (value ?? "").Trim().ToUpperInvariant();
            if (_letters == clean) return;
            _letters = clean;
            Notify(nameof(Letters));
            Notify(nameof(Preview));
        }
    }

    public string ShiftLabel
    {
        get => _shiftLabel;
        set
        {
            string norm = value == "2-я" ? "2-я" : "1-я";
            if (_shiftLabel == norm) return;
            _shiftLabel = norm;
            Notify(nameof(ShiftLabel));
        }
    }

    public string Preview => ClassCount <= 0
        ? "—"
        : string.Join(", ", ResolveLetters().Select(l => $"{Grade}{l}"));

    public IReadOnlyList<string> ResolveLetters()
    {
        if (ClassCount <= 0) return [];
        const string alphabet = "АБВГДЕЖЗИК";
        string src = string.Concat(_letters.Where(c => !char.IsWhiteSpace(c) && c != ','));
        var list = new List<string>(ClassCount);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (char c in src)
        {
            if (list.Count >= ClassCount) break;
            string s = c.ToString();
            if (seen.Add(s)) list.Add(s);
        }
        foreach (char c in alphabet)
        {
            if (list.Count >= ClassCount) break;
            string s = c.ToString();
            if (seen.Add(s)) list.Add(s);
        }
        return list;
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class SchoolWizardWindow : Window
{
    private static readonly string[] Shifts = ["1-я", "2-я"];
    private readonly ObservableCollection<WizardGradeRow> _rows = [];
    private bool _suppressPreview;

    public IReadOnlyList<LoadRow> ResultRows { get; private set; } = [];
    public FlexDataset ResultFlex { get; private set; } = FlexDataset.Empty;
    public int ResultDays { get; private set; } = 5;
    public int ResultSlots { get; private set; } = 8;

    public SchoolWizardWindow()
    {
        InitializeComponent();
        ShiftCol.ItemsSource = Shifts;
        for (int g = 1; g <= 11; g++)
        {
            var row = new WizardGradeRow { Grade = g, ClassCount = g is >= 5 and <= 9 ? 2 : 0 };
            row.PropertyChanged += (_, _) => UpdatePreview();
            _rows.Add(row);
        }
        GradesGrid.ItemsSource = _rows;
        UpdatePreview();
    }

    private void ApplyPreset(Func<int, int> countForGrade, Func<int, string>? shiftForGrade = null)
    {
        _suppressPreview = true;
        try
        {
            foreach (var r in _rows)
            {
                r.ClassCount = countForGrade(r.Grade);
                r.ShiftLabel = shiftForGrade?.Invoke(r.Grade) ?? "1-я";
            }
            if (_rows.Any(r => r.ClassCount > 0 && r.ShiftLabel == "2-я"))
                SlotsBox.SelectedIndex = 2; // 12 (2 смены)
        }
        finally
        {
            _suppressPreview = false;
        }
        UpdatePreview();
    }

    private void OnPresetPrimaryClick(object sender, RoutedEventArgs e) =>
        ApplyPreset(g => g <= 4 ? 2 : 0);

    private void OnPresetBasicClick(object sender, RoutedEventArgs e) =>
        ApplyPreset(g => g is >= 5 and <= 9 ? 2 : 0);

    private void OnPresetFullClick(object sender, RoutedEventArgs e) =>
        ApplyPreset(g => g <= 9 ? 2 : 1, g => g is >= 5 and <= 7 ? "2-я" : "1-я");

    private void OnPresetDemoClick(object sender, RoutedEventArgs e) =>
        ApplyPreset(g => g is >= 6 and <= 8 ? 2 : 0);

    private void OnClearClick(object sender, RoutedEventArgs e) =>
        ApplyPreset(_ => 0);

    private void OnGridEdited(object? sender, DataGridCellEditEndingEventArgs e) =>
        Dispatcher.BeginInvoke(UpdatePreview);

    private void OnOptionChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();
    private void OnOptionCheckChanged(object sender, RoutedEventArgs e) => UpdatePreview();

    private (List<ParallelSpec> Specs, int Days, int Slots) ReadSpecs()
    {
        var specs = _rows
            .Where(r => r.ClassCount > 0)
            .Select(r => new ParallelSpec(r.Grade, r.ResolveLetters(), r.ShiftLabel == "2-я" ? 2 : 1))
            .ToList();
        int days = DaysBox?.SelectedIndex == 1 ? 6 : 5;
        int slots = SlotsBox?.SelectedIndex switch
        {
            0 => 7,
            2 => 12,
            _ => 8,
        };
        if (specs.Any(s => s.Shift == 2) && slots < 12)
            slots = 12;
        return (specs, days, slots);
    }

    private void UpdatePreview()
    {
        if (_suppressPreview || !IsInitialized || PreviewText is null) return;
        var (specs, days, slots) = ReadSpecs();
        if (specs.Count == 0)
        {
            PreviewText.Text = "Выберите хотя бы одну параллель (кол-во классов > 0).";
            CreateBtn.IsEnabled = false;
            return;
        }
        var (rows, flex) = SchoolSetupPresets.BuildStarterSchool(
            specs,
            SubjectsBox.IsChecked == true,
            RoomsBox.IsChecked == true);
        int teachers = rows.SelectMany(r => new[] { r.TeacherName, r.SplitTeacherBName })
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int totalHours = rows.Sum(r => r.HoursPerWeek);
        PreviewText.Text = $"Будет создано: {flex.Classes.Count} классов · {rows.Count} строк нагрузки " +
            $"({totalHours} ч/нед) · {teachers} учителей · сетка {days} дн. × {slots} ур.";
        CreateBtn.IsEnabled = true;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnCreateClick(object sender, RoutedEventArgs e)
    {
        var (specs, days, slots) = ReadSpecs();
        if (specs.Count == 0)
        {
            ErrorText.Text = "Укажите количество классов больше 0 хотя бы для одной параллели.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        var (rows, flex) = SchoolSetupPresets.BuildStarterSchool(
            specs,
            SubjectsBox.IsChecked == true,
            RoomsBox.IsChecked == true);
        ResultRows = rows;
        ResultFlex = flex;
        ResultDays = days;
        ResultSlots = slots;
        DialogResult = true;
        Close();
    }
}
