using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Amsur.Application;
using Amsur.Scheduling.Core;
using Microsoft.Win32;

namespace Amsur.Wpf;

public sealed record ScheduleRowVm(
    Guid OccurrenceId, string ClassName, string SubjectName, string TeacherName,
    int Day, int Slot, string RoomName, int DayIndex, Guid? RoomId);

public partial class ScheduleWindow : Window
{
    private static readonly string[] DayNames =
        ["Понедельник", "Вторник", "Среда", "Четверг", "Пятница", "Суббота", "Воскресенье"];

    private readonly AppSession _session;
    private SchedulingProblem? _problem;
    private ActiveSchedule? _active;
    private EditPreview? _preview;
    private ObservableCollection<ScheduleRowVm> _allRows = [];
    private ScheduleRowVm? _selectedRow;

    public ScheduleWindow(AppSession session)
    {
        _session = session;
        InitializeComponent();
        SearchBox.ToolTip = "Поиск по предмету, учителю, классу или кабинету (фильтр на экране)";
        _ = ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            _preview = null;
            _selectedRow = null;
            VerdictText.Text = "";
            StyleVerdict("");
            SelectedInfoText.Text = "Выберите урок в сетке или таблице";
            _active = await _session.GetActiveAsync();
            if (_active is null)
            {
                StatusText.Text = "Нет активного расписания — сгенерируйте варианты и нажмите «Принять расписание».";
                Grid.ItemsSource = null;
                MatrixScroll.Visibility = Visibility.Collapsed;
                CountText.Text = "";
                SetEditEnabled(false);
                return;
            }
            _problem = _session.BuildProblem();
            var occById = _problem.Occurrences.ToDictionary(o => o.Id);
            var rows = new ObservableCollection<ScheduleRowVm>();
            foreach (var p in _active.Placements
                         .OrderBy(p => PickName(occById, p.OccurrenceId, o => _problem.Classes[o.ClassId].Name))
                         .ThenBy(p => p.DayIndex).ThenBy(p => p.SlotIndex))
            {
                if (!occById.TryGetValue(p.OccurrenceId, out var occ)) continue; // чужой год — пропускаем
                rows.Add(new ScheduleRowVm(occ.Id,
                    _problem.Classes[occ.ClassId].Name,
                    _problem.Subjects[occ.SubjectId].Name,
                    _problem.Teachers[occ.TeacherId].Name,
                    p.DayIndex + 1, p.SlotIndex,
                    p.RoomId.HasValue && _problem.Rooms.TryGetValue(p.RoomId.Value, out var r) ? r.Name : "—",
                    p.DayIndex, p.RoomId));
            }
            _allRows = rows;
            StatusText.Text = $"Активна версия {_active.Number} ({_active.Reason}). Уроков: {rows.Count}.";
            FillNameBox();
            ApplyViewFilter();
            SetEditEnabled(true);
            _ = LoadQualityAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка: " + ex.Message;
            SetEditEnabled(false);
        }
    }

    private async Task LoadQualityAsync()
    {
        try
        {
            var q = await _session.GetActiveQualityAsync();
            if (q is null) { QualityText.Text = ""; return; }
            QualityText.Text = $"Качество: {q.Label}" +
                (q.Improvements.Count == 0 ? "" : " · Улучшить: " + string.Join("; ", q.Improvements.Take(3)));
        }
        catch { QualityText.Text = ""; }
    }

    // UI-only: список имён зависит от выбранного вида (классы/учителя/кабинеты).
    private void FillNameBox()
    {
        if (_problem is null) return;
        int mode = ViewBox.SelectedIndex;
        object? prev = NameBox.SelectedItem;
        List<string> items = mode switch
        {
            1 => _problem.Classes.Values.OrderBy(c => c.Name).Select(c => c.Name).ToList(),
            2 => _problem.Teachers.Values.OrderBy(t => t.Name).Select(t => t.Name).ToList(),
            3 => _allRows.Select(r => r.RoomName).Where(n => n != "—")
                    .Distinct().OrderBy(n => n).ToList(),
            _ => _problem.Classes.Values.OrderBy(c => c.Name).Select(c => c.Name)
                    .Concat(_problem.Teachers.Values.OrderBy(t => t.Name).Select(t => t.Name)).ToList(),
        };
        if (mode == 3 && items.Count == 0)
            items = ["(кабинеты не указаны)"];
        NameBox.ItemsSource = items;
        if (prev is string s && items.Contains(s))
            NameBox.SelectedItem = s;
        else if (items.Count > 0 && mode > 0)
            NameBox.SelectedIndex = -1; // фильтр применяется только после выбора
    }

    // UI-only проекция: фильтр по виду + поисковая строка, без смены контрактов.
    private List<ScheduleRowVm> GetFilteredRows()
    {
        string? name = NameBox.SelectedItem as string;
        int mode = ViewBox.SelectedIndex;
        string q = (SearchBox.Text ?? "").Trim().ToLowerInvariant();
        return _allRows.Where(r =>
            (mode switch
            {
                1 => string.IsNullOrEmpty(name) || r.ClassName == name,
                2 => string.IsNullOrEmpty(name) || r.TeacherName == name,
                3 => string.IsNullOrEmpty(name) || r.RoomName == name,
                _ => string.IsNullOrEmpty(name)
                    || r.ClassName == name || r.TeacherName == name || r.RoomName == name,
            }) &&
            (string.IsNullOrEmpty(q)
                || r.SubjectName.ToLowerInvariant().Contains(q)
                || r.TeacherName.ToLowerInvariant().Contains(q)
                || r.ClassName.ToLowerInvariant().Contains(q)
                || r.RoomName.ToLowerInvariant().Contains(q))).ToList();
    }

    private void ApplyViewFilter()
    {
        if (_problem is null) return;
        var rows = GetFilteredRows();
        Grid.ItemsSource = new ObservableCollection<ScheduleRowVm>(rows);
        string? name = NameBox.SelectedItem as string;
        int mode = ViewBox.SelectedIndex;
        string scope = mode switch
        {
            1 => string.IsNullOrEmpty(name) ? "все классы" : $"класс {name}",
            2 => string.IsNullOrEmpty(name) ? "все учителя" : $"учитель {name}",
            3 => string.IsNullOrEmpty(name) ? "все кабинеты" : $"кабинет {name}",
            _ => "всё расписание",
        };
        CountText.Text = $"Показано {rows.Count} из {_allRows.Count} · {scope}";

        // Матрица — только для конкретного класса/учителя/кабинета и компактной сетки.
        // «Всё расписание» остаётся таблицей (матрица по всей школе нечитаема).
        bool wantMatrix = mode > 0 && !string.IsNullOrEmpty(name)
            && _problem.DaysCount <= 6 && _problem.SlotsPerDay <= 9;
        if (wantMatrix)
        {
            Grid.Visibility = Visibility.Collapsed;
            MatrixScroll.Visibility = Visibility.Visible;
            BuildMatrix(rows);
        }
        else
        {
            MatrixScroll.Visibility = Visibility.Collapsed;
            Grid.Visibility = Visibility.Visible;
        }
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildMatrix(List<ScheduleRowVm> rows)
    {
        MatrixGrid.Children.Clear();
        MatrixGrid.RowDefinitions.Clear();
        MatrixGrid.ColumnDefinitions.Clear();
        if (_problem is null) return;
        int days = _problem.DaysCount;
        int slots = _problem.SlotsPerDay;

        for (int c = 0; c <= days; c++)
            MatrixGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = c == 0 ? new GridLength(110) : new GridLength(1, GridUnitType.Star),
                MinWidth = c == 0 ? 110 : 150,
            });
        for (int r = 0; r <= slots; r++)
            MatrixGrid.RowDefinitions.Add(new RowDefinition
            {
                Height = r == 0 ? GridLength.Auto : new GridLength(76),
            });

        var byCell = rows.ToDictionary(r => (r.DayIndex, r.Slot), r => r);
        var accent = Br("BAccent", Brushes.Indigo);
        var accentSoft = Br("BAccentSoft", Brushes.Lavender);
        var card = Br("BCard", Brushes.White);
        var subtle = Br("BSubtle", Brushes.WhiteSmoke);
        var border = Br("BBorder", Brushes.LightGray);
        var textSoft = Br("BTextSoft", Brushes.Gray);
        var textMuted = Br("BTextMuted", Brushes.DarkGray);

        // Угол + заголовки дней (жёсткие, Stitch: день + число уроков).
        var corner = new Border
        {
            Background = subtle, BorderBrush = border, BorderThickness = new Thickness(0, 0, 1, 1),
            Padding = new Thickness(8, 6, 8, 6),
            Child = new TextBlock
            {
                Text = "УРОК / ДЕНЬ", FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = textSoft,
            },
        };
        System.Windows.Controls.Grid.SetRow(corner, 0); System.Windows.Controls.Grid.SetColumn(corner, 0);
        MatrixGrid.Children.Add(corner);

        for (int d = 0; d < days; d++)
        {
            string dayName = d < DayNames.Length ? DayNames[d] : $"День {d + 1}";
            int count = rows.Count(r => r.DayIndex == d);
            var hb = new Border
            {
                Background = subtle, BorderBrush = border, BorderThickness = new Thickness(0, 0, 1, 1),
                Padding = new Thickness(8, 6, 8, 6),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = dayName, FontSize = 13, FontWeight = FontWeights.SemiBold },
                        new TextBlock { Text = $"{count} уроков", FontSize = 11, Foreground = textSoft },
                    },
                },
            };
            System.Windows.Controls.Grid.SetRow(hb, 0); System.Windows.Controls.Grid.SetColumn(hb, d + 1);
            MatrixGrid.Children.Add(hb);
        }

        for (int s = 0; s < slots; s++)
        {
            var sb = new Border
            {
                Background = subtle, BorderBrush = border, BorderThickness = new Thickness(0, 0, 1, 1),
                Padding = new Thickness(8, 6, 8, 6),
                Child = new TextBlock
                {
                    Text = $"{s + 1} урок", FontSize = 13, FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            System.Windows.Controls.Grid.SetRow(sb, s + 1); System.Windows.Controls.Grid.SetColumn(sb, 0);
            MatrixGrid.Children.Add(sb);

            for (int d = 0; d < days; d++)
            {
                byCell.TryGetValue((d, s), out var row);
                bool isSelected = row is not null && _selectedRow is not null &&
                    row.OccurrenceId == _selectedRow.OccurrenceId;
                Border cell;
                if (row is null)
                {
                    cell = new Border
                    {
                        Background = card, BorderBrush = border, BorderThickness = new Thickness(0, 0, 1, 1),
                        Padding = new Thickness(8), Opacity = 0.75,
                        Child = new TextBlock
                        {
                            Text = "— нет урока —", FontSize = 12, Foreground = textMuted,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    };
                }
                else
                {
                    var chip = new Border
                    {
                        Background = accentSoft, Margin = new Thickness(0, 4, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 2, 8, 2),
                        Child = new TextBlock
                        {
                            Text = $"каб. {row.RoomName}", FontSize = 11,
                            Foreground = accent,
                        },
                    };
                    if (TryFindResource("Badge") is Style badgeStyle) chip.Style = badgeStyle;
                    var inner = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = row.SubjectName, FontSize = 12, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
                            new TextBlock { Text = row.TeacherName, FontSize = 11, Foreground = textSoft, Margin = new Thickness(0, 2, 0, 0) },
                            chip,
                        },
                    };
                    cell = new Border
                    {
                        Background = isSelected ? accentSoft : card,
                        BorderBrush = isSelected ? accent : border,
                        BorderThickness = isSelected ? new Thickness(2) : new Thickness(0, 0, 1, 1),
                        CornerRadius = isSelected ? new CornerRadius(8) : new CornerRadius(0),
                        Padding = new Thickness(8), Cursor = Cursors.Hand, Tag = row,
                    };
                    cell.Child = inner;
                    cell.MouseLeftButtonUp += OnMatrixCellClick;
                    cell.ToolTip = $"{row.ClassName} · {row.SubjectName} · {row.TeacherName} · каб. {row.RoomName}";
                }
                System.Windows.Controls.Grid.SetRow(cell, s + 1); System.Windows.Controls.Grid.SetColumn(cell, d + 1);
                MatrixGrid.Children.Add(cell);
            }
        }
    }

    private void OnMatrixCellClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is ScheduleRowVm row)
        {
            _selectedRow = row;
            Grid.SelectedItem = null;
            DayBox.Text = row.Day.ToString();
            SlotBox.Text = row.Slot.ToString();
            SelectedInfoText.Text = $"{row.ClassName} · {row.SubjectName} · {row.TeacherName} · каб. {row.RoomName} (день {row.Day}, урок {row.Slot})";
            BuildMatrixSelection();
        }
    }

    private void BuildMatrixSelection()
    {
        // Лёгкое обновление кольца без полной перефильтрации: пересобираем матрицу.
        if (MatrixScroll.Visibility == Visibility.Visible && _problem is not null)
            BuildMatrix(GetFilteredRows());
    }

    // Мягкая стилизация вердикта по префиксу (текст приходит из Preview как есть).
    private void StyleVerdict(string text)
    {
        // Без ресурсов App (STA-тест) — только текст, без стилизации.
        if (TryFindResource("BSubtle") is not Brush subtleRes) return;
        string t = (text ?? "").ToLowerInvariant();
        Brush bg = subtleRes;
        Brush bd = (Brush)FindResource("BBorder");
        Brush fg = (Brush)FindResource("BText");
        if (t.Length == 0) { /* нейтрально */ }
        else if (t.Contains("запрещ") || t.Contains("нельзя") || t.Contains("жёстк") ||
                 t.Contains("конфликт") || t.Contains("ошибка") || t.Contains("недопуст"))
        {
            bg = (Brush)FindResource("BBadBg"); bd = (Brush)FindResource("BBad"); fg = (Brush)FindResource("BBad");
        }
        else if (t.Contains("ухудш") || t.Contains("замечан") || t.Contains("штраф") ||
                 t.Contains("окно") || t.Contains("допуст"))
        {
            bg = (Brush)FindResource("BWarnBg"); bd = (Brush)FindResource("BWarn"); fg = (Brush)FindResource("BWarn");
        }
        else if (t.Contains("можно") || t.Contains("разреш") || t.Contains("оптимал") ||
                 t.Contains("свободен") || t.Contains("успеш") || t.Contains("выгружено") ||
                 t.Contains("сохран") || t.Contains("принят"))
        {
            bg = (Brush)FindResource("BGoodBg"); bd = (Brush)FindResource("BGood"); fg = (Brush)FindResource("BGood");
        }
        VerdictBorder.Background = bg;
        VerdictBorder.BorderBrush = bd;
        VerdictText.Foreground = fg;
    }

    private void OnViewChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return; // событие при парсинге XAML — контролы ещё не готовы
        FillNameBox();
        ApplyViewFilter();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyViewFilter();
    }

    private static string PickName(Dictionary<Guid, Amsur.Domain.LessonOccurrence> occById,
        Guid occId, Func<Amsur.Domain.LessonOccurrence, string> pick) =>
        occById.TryGetValue(occId, out var o) ? pick(o) : "";

    // TryFindResource + fallback: окно обязано конструироваться и без ресурсов App (STA-тест).
    private Brush Br(string key, Brush fallback) =>
        TryFindResource(key) as Brush ?? fallback;

    private void SetEditEnabled(bool on)
    {
        PreviewBtn.IsEnabled = on;
        CommitBtn.IsEnabled = on;
        ExportBtn.IsEnabled = on;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Grid.SelectedItem is ScheduleRowVm row)
        {
            _selectedRow = row;
            DayBox.Text = row.Day.ToString();
            SlotBox.Text = row.Slot.ToString();
            SelectedInfoText.Text = $"{row.ClassName} · {row.SubjectName} · {row.TeacherName} · каб. {row.RoomName} (день {row.Day}, урок {row.Slot})";
            BuildMatrixSelection();
        }
    }

    private void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var row = GetSelected();
            int day = ParseDay(), slot = ParseSlot();
            _preview = _session.EditService.Preview(_problem!, _active!.Placements,
                new CandidateMove(row.OccurrenceId, day, slot, row.RoomId));
            VerdictText.Text = _preview.VerdictText;
            StyleVerdict(_preview.VerdictText);
        }
        catch (Exception ex)
        {
            VerdictText.Text = "Ошибка: " + ex.Message;
            StyleVerdict(VerdictText.Text);
            _preview = null;
        }
    }

    private async void OnCommitClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_preview is null)
            {
                VerdictText.Text = "Сначала нажмите «Проверить ход».";
                StyleVerdict(VerdictText.Text);
                return;
            }
            var outcome = await _session.EditService.CommitAsync(
                _session.AcademicYearId, _problem!, _preview);
            VerdictText.Text = outcome.Message;
            StyleVerdict(outcome.Message);
            if (outcome.Committed) await ReloadAsync();
        }
        catch (Exception ex)
        {
            VerdictText.Text = "Ошибка: " + ex.Message;
            StyleVerdict(VerdictText.Text);
        }
    }

    // P0-6: экспорт — через отдельное окно (Excel only, PDF «Скоро»).
    private void OnExportClick(object sender, RoutedEventArgs e) =>
        new ExportWindow(_session) { Owner = this }.ShowDialog();

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await ReloadAsync();

    private ScheduleRowVm GetSelected() =>
        _selectedRow ?? Grid.SelectedItem as ScheduleRowVm
        ?? throw new InvalidOperationException("Выберите урок в таблице.");

    private int ParseDay()
    {
        if (!int.TryParse(DayBox.Text, out int day) || day < 1 || day > _problem!.DaysCount)
            throw new InvalidOperationException($"День — число от 1 до {_problem!.DaysCount}.");
        return day - 1;
    }

    private int ParseSlot()
    {
        if (!int.TryParse(SlotBox.Text, out int slot) || slot < 1 || slot > _problem!.SlotsPerDay)
            throw new InvalidOperationException($"Урок — число от 1 до {_problem!.SlotsPerDay}.");
        return slot;
    }
}
