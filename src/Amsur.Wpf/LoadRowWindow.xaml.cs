using System.Windows;
using Amsur.Application;

namespace Amsur.Wpf;

// P3: диалог ручного ввода строки нагрузки. Имена — свободный ввод:
// неизвестное имя честно создаст сущность (как Excel-импортёр).
// Валидация до закрытия; ShowDialog() == true — Result готов.
public partial class LoadRowWindow : Window
{
    public LoadRow? Result { get; private set; }

    public LoadRowWindow(
        LoadRow? existing = null,
        IReadOnlyList<string>? knownClasses = null,
        IReadOnlyList<string>? knownSubjects = null,
        IReadOnlyList<string>? knownTeachers = null,
        IReadOnlyList<string>? knownRooms = null,
        // S10 (наследование смены): для новой строки класса со сменой — её смена.
        Func<string, int?>? inheritShift = null)
    {
        _inheritShift = inheritShift;
        _existing = existing;
        InitializeComponent();
        if (knownClasses is { Count: > 0 })
            ClassKnown.Text = "Уже есть: " + string.Join(", ", knownClasses.Take(12));
        if (knownSubjects is { Count: > 0 })
            SubjectKnown.Text = "Уже есть: " + string.Join(", ", knownSubjects.Take(12));
        if (knownTeachers is { Count: > 0 })
            TeacherKnown.Text = "Уже есть: " + string.Join(", ", knownTeachers.Take(12));
        if (knownRooms is { Count: > 0 })
            RoomKnown.Text = "Уже есть: " + string.Join(", ", knownRooms.Take(12));
        if (existing is not null)
        {
            ClassBox.Text = existing.ClassName;
            SubjectBox.Text = existing.SubjectName;
            HoursBox.Text = existing.HoursPerWeek.ToString();
            TeacherBox.Text = existing.TeacherName;
            RoomBox.Text = existing.RoomName ?? "";
            SplitBox.IsChecked = existing.SplitSubgroups;
            TeacherBBox.Text = existing.SplitTeacherBName ?? "";
            TeacherBBox.IsEnabled = existing.SplitSubgroups;
            // Ручной carry-over: пара не редактируется (backlog, Excel-first),
            // но сохраняется в Result без изменений.
            ShiftBox.Text = existing.Shift?.ToString() ?? "";
        }
    }

    private readonly Func<string, int?>? _inheritShift;
    private readonly LoadRow? _existing;

    private void OnSplitToggled(object sender, RoutedEventArgs e) =>
        TeacherBBox.IsEnabled = SplitBox.IsChecked == true;

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        string cls = (ClassBox.Text ?? "").Trim();
        string subj = (SubjectBox.Text ?? "").Trim();
        string teacher = (TeacherBox.Text ?? "").Trim();
        string room = (RoomBox.Text ?? "").Trim();
        string teacherB = (TeacherBBox.Text ?? "").Trim();
        bool split = SplitBox.IsChecked == true;
        string shiftRaw = (ShiftBox.Text ?? "").Trim();

        string? err = null;
        int? shift = null;
        if (cls.Length == 0) err = "Укажите класс.";
        else if (subj.Length == 0) err = "Укажите предмет.";
        else if (teacher.Length == 0) err = "Укажите учителя.";
        else if (!int.TryParse((HoursBox.Text ?? "").Trim(), out int hours) || hours < 1 || hours > 50)
            err = "Часов в неделю — целое число 1..50.";
        else if (split && teacherB.Length == 0) err = "Для деления нужен второй учитель (подгруппа B).";
        else if (split && string.Equals(teacherB, teacher, StringComparison.OrdinalIgnoreCase))
            err = "Учителя A и B должны различаться.";
        else if (shiftRaw.Length > 0 && shiftRaw is not ("1" or "2"))
            err = "Смена — пусто (= 1-я) или 2 (вторая, уроки №6–12).";
        if (err is not null)
        {
            ErrorText.Text = err;
            return;
        }
        // Наследование смены: пустое поле + известный класс со сменой → смена класса.
        shift = shiftRaw switch { "1" => 1, "2" => 2, _ => _inheritShift?.Invoke(cls) };

        Result = new LoadRow(cls, subj, int.Parse(HoursBox.Text.Trim()), teacher, split,
            split ? teacherB : null, room.Length == 0 ? null : room,
            _existing?.UnavailDays, _existing?.UnavailSlots, _existing?.PairName, shift);
        DialogResult = true;
        Close();
    }
}
