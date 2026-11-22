using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Amsur.Application;
using Amsur.Scheduling.Core;

namespace Amsur.Wpf.Views;

public sealed record ScheduleRowVm(
    Guid OccurrenceId, string ClassName, string SubjectName, string TeacherName,
    int Day, int Slot, string RoomName, int DayIndex, Guid? RoomId, string Badge = "");

// P-D2: сетка как view (логика из ScheduleWindow 1-в-1 + акценты областей).
// P5: режим «Шахматка по дню», ручная правка кликом и Drag-and-Drop по клеткам.
public partial class ScheduleView : UserControl
{
    public IViewNavigator? Navigator { get; set; }
    private static readonly string[] DayNames =
        ["Понедельник", "Вторник", "Среда", "Четверг", "Пятница", "Суббота", "Воскресенье"];

    // P5: оценка клетки для выбранного урока (клик/DnD). Allowed ==CanCommit превью.
    private sealed record CellEval(bool Allowed, long Delta, string ToolTip);
    // P5: тег клетки матрицы — день (0-based), урок (1-based, как в движке) и занятие, если есть.
    private sealed record MatrixCellTag(int DayIndex, int Slot, ScheduleRowVm? Row);

    private readonly AppSession _session;
    private SchedulingProblem? _problem;
    private ActiveSchedule? _active;
    private EditPreview? _preview;
    private ObservableCollection<ScheduleRowVm> _allRows = [];
    private ScheduleRowVm? _selectedRow;
    private readonly Dictionary<(int Day, int Slot), CellEval> _cellEvals = [];
    private (int Day, int Slot)? _targetCell;
    private bool _suspendViewEvents;
    private bool _defaultViewApplied;
    private Point _dragStart;
    private Border? _dragSource;
    private CancellationTokenSource? _evalCts;

    public ScheduleView(AppSession session)
    {
        _session = session;
        InitializeComponent();
        SearchBox.ToolTip = "Поиск по предмету, учителю, классу или кабинету (фильтр на экране)";
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _ = ReloadAsync();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app)
        {
            app.ThemeChanged -= OnAppThemeChanged;
            app.ThemeChanged += OnAppThemeChanged;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CancelCellEvaluations();
        if (System.Windows.Application.Current is App app)
            app.ThemeChanged -= OnAppThemeChanged;
    }

    private void OnAppThemeChanged(string _)
    {
        StyleVerdict(VerdictText.Text);
        RebuildMatrix();
    }

    private static void Bind(FrameworkElement el, DependencyProperty prop, string key)
    {
        if (el.TryFindResource(key) is not null)
            el.SetResourceReference(prop, key);
    }

    private async Task ReloadAsync()
    {
        try
        {
            ResetEditState();
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
                string badge = "";
                if (string.Equals(_problem.Subjects[occ.SubjectId].Name, "Классный час",
                        StringComparison.OrdinalIgnoreCase))
                    badge = "Классный час";
                else if (p.RoomId.HasValue &&
                    _problem.Rooms.TryGetValue(p.RoomId.Value, out var br))
                    badge = br.IsManualOnly ? "Ручной кабинет"
                        : br.OnlySubjectId.HasValue ? "ONLY" : "";
                rows.Add(new ScheduleRowVm(occ.Id,
                    _problem.Classes[occ.ClassId].Name,
                    _problem.Subjects[occ.SubjectId].Name,
                    _problem.Teachers[occ.TeacherId].Name,
                    p.DayIndex + 1, p.SlotIndex,
                    p.RoomId.HasValue && _problem.Rooms.TryGetValue(p.RoomId.Value, out var r) ? r.Name : "—",
                    p.DayIndex, p.RoomId, badge));
            }
            _allRows = rows;
            StatusText.Text = $"Активна версия {_active.Number} ({_active.Reason}). Уроков: {rows.Count}.";
            // P5: экран открывается сразу сеткой по первому классу — не пустой таблицей.
            if (!_defaultViewApplied)
            {
                _defaultViewApplied = true;
                SetViewMode(1);
            }
            FillNameBox();
            EnsureNameSelection();
            ApplyViewFilter();
            // P5: строки в Grid есть всегда (контракт тестов и «Все уроки»).
            Grid.ItemsSource = new ObservableCollection<ScheduleRowVm>(_allRows);
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

    private void ResetEditState()
    {
        CancelCellEvaluations();
        _preview = null;
        _selectedRow = null;
        _targetCell = null;
        _cellEvals.Clear();
        _dragSource = null;
        VerdictText.Text = "";
        StyleVerdict("");
        SelectedInfoText.Text = "Выберите урок в сетке или таблице";
        CommitBtn.Content = "Сохранить ход";
    }

    // UI-only: список имён зависит от выбранного вида (классы/учителя/кабинеты/дни).
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
            4 => Enumerable.Range(0, Math.Max(_problem.DaysCount, 1))
                    .Select(DayLabel).ToList(),
            _ => _problem.Classes.Values.OrderBy(c => c.Name).Select(c => c.Name)
                    .Concat(_problem.Teachers.Values.OrderBy(t => t.Name).Select(t => t.Name)).ToList(),
        };
        if (mode == 3 && items.Count == 0)
            items = ["(кабинеты не указаны)"];
        bool saved = _suspendViewEvents;
        _suspendViewEvents = true;
        try
        {
            NameBox.ItemsSource = items;
            if (prev is string s && items.Contains(s))
                NameBox.SelectedItem = s;
            else
                NameBox.SelectedIndex = -1; // фильтр применяется только после выбора
        }
        finally { _suspendViewEvents = saved; }
    }

    // P5: в режимах-сетках имя выбирается сразу, иначе завуч видит пустой экран.
    private void EnsureNameSelection()
    {
        int mode = ViewBox.SelectedIndex;
        if (mode is < 1 or > 4 || NameBox.SelectedIndex >= 0 || NameBox.Items.Count == 0) return;
        bool saved = _suspendViewEvents;
        _suspendViewEvents = true;
        try { NameBox.SelectedIndex = 0; }
        finally { _suspendViewEvents = saved; }
    }

    private static string DayLabel(int index) =>
        index >= 0 && index < DayNames.Length ? DayNames[index] : $"День {index + 1}";

    private int SelectedDayIndex()
    {
        int days = _problem?.DaysCount ?? 0;
        int index = NameBox.SelectedIndex;
        return index >= 0 && index < days ? index : 0;
    }

    // UI-only проекция: фильтр по виду + поисковая строка, без смены контрактов.
    private List<ScheduleRowVm> GetFilteredRows()
    {
        string? name = NameBox.SelectedItem as string;
        int mode = ViewBox.SelectedIndex;
        string q = (SearchBox.Text ?? "").Trim().ToLowerInvariant();
        int dayIndex = mode == 4 ? SelectedDayIndex() : -1;
        return _allRows.Where(r =>
            (mode switch
            {
                1 => string.IsNullOrEmpty(name) || r.ClassName == name,
                2 => string.IsNullOrEmpty(name) || r.TeacherName == name,
                3 => string.IsNullOrEmpty(name) || r.RoomName == name,
                4 => r.DayIndex == dayIndex,
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
            4 => $"Шахматка · {DayLabel(SelectedDayIndex())}",
            _ => "всё расписание",
        };
        CountText.Text = $"Показано {rows.Count} из {_allRows.Count} · {scope}";

        // Матрица — для конкретного класса/учителя/кабинета (P5: до 16 уроков, двухсменка)
        // и отдельный режим «Шахматка по дню» для всей школы. «Всё расписание» — таблица.
        bool chess = mode == 4;
        bool wantMatrix = chess || (mode > 0 && !string.IsNullOrEmpty(name)
            && _problem.DaysCount <= 6 && _problem.SlotsPerDay <= 16);
        if (wantMatrix)
        {
            Grid.Visibility = Visibility.Collapsed;
            MatrixScroll.Visibility = Visibility.Visible;
            RefreshCellEvaluations();
            if (chess) BuildChessboard(rows);
            else BuildMatrix(rows, teacherMode: mode == 2);
        }
        else
        {
            MatrixScroll.Visibility = Visibility.Collapsed;
            Grid.Visibility = Visibility.Visible;
        }
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Матрица «уроки × дни» (класс / учитель / кабинет) ----------

    private void BuildMatrix(List<ScheduleRowVm> rows, bool teacherMode = false)
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
                // Auto-высота с запасом (MinHeight=76) исключает обрезание пилюль кабинета и длинных предметов.
                Height = GridLength.Auto,
                MinHeight = r == 0 ? 0 : 76,
            });

        // Сплит-урок (A/B) и вид «кабинет» штатно дают несколько записей на одну клетку —
        // ToDictionary падал бы с ArgumentException на дубле ключа. Храним список:
        // P5: показываем ВСЕ половины клетки (обе подгруппы), а не прячем вторую под «+ ещё N».
        var byCell = ScheduleMatrixCells.Group(rows);
        var accent = Br("BAccent", Brushes.Indigo);
        var accentSoft = Br("BAccentSoft", Brushes.Lavender);
        var card = Br("BCard", Brushes.White);
        var subtle = Br("BSubtle", Brushes.WhiteSmoke);
        var border = Br("BBorder", Brushes.LightGray);
        var text = Br("BText", Brushes.Black);
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
            string dayName = DayLabel(d);
            int count = rows.Count(r => r.DayIndex == d);
            var hb = new Border
            {
                Background = subtle, BorderBrush = border, BorderThickness = new Thickness(0, 0, 1, 1),
                Padding = new Thickness(8, 6, 8, 6),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = dayName, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = text },
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
                    Foreground = text,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            System.Windows.Controls.Grid.SetRow(sb, s + 1); System.Windows.Controls.Grid.SetColumn(sb, 0);
            MatrixGrid.Children.Add(sb);

            for (int d = 0; d < days; d++)
            {
                var cellRows = ScheduleMatrixCells.Cell(byCell, d, s + 1); // слоты 1-based
                var tag = new MatrixCellTag(d, s + 1, cellRows is { Count: > 0 } ? cellRows[0] : null);
                Border cell;
                if (cellRows is null || cellRows.Count == 0)
                {
                    cell = BuildEmptyCell(tag, rows, teacherMode, textMuted);
                }
                else
                {
                    cell = BuildLessonCell(tag, cellRows, accent, accentSoft, text, textSoft);
                }

                var (bg, bd, th) = CellPaint(cell.Tag, card, border, accent, accentSoft);
                cell.Background = bg;
                cell.BorderBrush = bd;
                cell.BorderThickness = th;
                AttachCellHandlers(cell);
                System.Windows.Controls.Grid.SetRow(cell, s + 1); System.Windows.Controls.Grid.SetColumn(cell, d + 1);
                MatrixGrid.Children.Add(cell);
            }
        }
    }

    // Пустая клетка: подсветка хода + (в режиме «По учителю») жёлтый бейдж «Окно».
    private Border BuildEmptyCell(MatrixCellTag tag, List<ScheduleRowVm> rows, bool teacherMode,
        Brush textMuted)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(new TextBlock
        {
            Text = "— нет урока —", FontSize = 12, Foreground = textMuted,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        // Окно учителя: пустая клетка между первым и последним уроком дня.
        if (teacherMode)
        {
            var daySlots = rows.Where(r => r.DayIndex == tag.DayIndex).Select(r => r.Slot).ToList();
            if (daySlots.Count > 1 && tag.Slot > daySlots.Min() && tag.Slot < daySlots.Max())
            {
                var badge = new Border
                {
                    Background = Br("BWarnBg", Brushes.LemonChiffon),
                    CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 2, 8, 2),
                    Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = "Окно", FontSize = 11, FontWeight = FontWeights.SemiBold,
                        Foreground = Br("BWarn", Brushes.DarkGoldenrod),
                    },
                    ToolTip = "Окно: между уроками учителя пустой час",
                };
                panel.Children.Add(badge);
            }
        }

        var cell = new Border
        {
            BorderThickness = new Thickness(0, 0, 1, 1),
            Padding = new Thickness(8), Tag = tag,
            Child = panel,
        };
        cell.Cursor = Cursors.Cross;
        cell.ToolTip = "Свободно — кликните, чтобы поставить выбранный урок сюда";
        if (_cellEvals.TryGetValue((tag.DayIndex, tag.Slot), out var ev))
        {
            panel.Children.Add(new TextBlock
            {
                Text = ev.Allowed ? (ev.Delta > 0 ? $"можно (+{ev.Delta})" : "можно") : "нельзя",
                FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = ev.Allowed ? Br("BGood", Brushes.Green) : Br("BBad", Brushes.IndianRed),
            });
            cell.ToolTip = ev.ToolTip;
        }
        return cell;
    }

    // Занятая клетка: одна запись — предмет/учитель/кабинет; сплит A/B — ОБЕ подгруппы.
    private Border BuildLessonCell(MatrixCellTag tag, List<ScheduleRowVm> cellRows,
        Brush accent, Brush accentSoft, Brush text, Brush textSoft)
    {
        var row = cellRows[0];
        var area = AreaBrush(AreaOf(row.SubjectName));
        UIElement inner;
        if (cellRows.Count == 1)
        {
            var chipText = new TextBlock
            {
                Text = UiFormat.Room(row.RoomName), FontSize = 11,
                Foreground = textSoft,
            };
            var chip = new Border
            {
                Background = Br("BSubtle", Brushes.WhiteSmoke),
                BorderBrush = Br("BBorder", Brushes.LightGray),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 2, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 1, 7, 1),
                Child = chipText,
            };
            inner = new StackPanel
            {
                Margin = new Thickness(8, 0, 0, 0),
                Children =
                {
                    new TextBlock { Text = row.SubjectName, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = text, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = row.TeacherName, FontSize = 12, Foreground = textSoft, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis },
                    chip,
                },
            };
        }
        else
        {
            // P5: сплит/профильная пара — «Предмет · Учитель 1 (каб.) / Учитель 2 (каб.)».
            inner = new StackPanel
            {
                Margin = new Thickness(8, 0, 0, 0),
                Children =
                {
                    new TextBlock
                    {
                        Text = MultiRowText(cellRows), FontSize = 11, Foreground = text,
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            };
        }

        var stripe = new Border
        {
            Background = area, Width = 3, CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 2, 0, 2), VerticalAlignment = VerticalAlignment.Stretch,
        };
        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(stripe, Dock.Left);
        dock.Children.Add(stripe);
        dock.Children.Add(inner);

        var cell = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6), Cursor = Cursors.Hand, Tag = tag,
            Child = dock,
        };
        cell.ToolTip = string.Join("\n", cellRows.Select(r =>
            $"{r.ClassName} · {r.SubjectName} · {r.TeacherName} · {UiFormat.Room(r.RoomName)}" +
            (_cellEvals.TryGetValue((tag.DayIndex, tag.Slot), out var ev0) ? "\n" + ev0.ToolTip : "")));
        return cell;
    }

    // P5: текст клетки со всеми подгруппами (обе половины сплита видны сразу).
    private static string MultiRowText(List<ScheduleRowVm> cellRows)
    {
        static string Room(ScheduleRowVm r)
        {
            string room = UiFormat.Room(r.RoomName);
            return room == "—" ? r.TeacherName : $"{r.TeacherName} ({room})";
        }
        if (cellRows.Select(r => r.SubjectName).Distinct().Count() == 1)
            return $"{cellRows[0].SubjectName} · " + string.Join(" / ", cellRows.Select(Room));
        return string.Join(" / ", cellRows.Select(Room).Select((t, i) =>
            $"{cellRows[i].SubjectName} · {t}"));
    }

    // ---------- «Шахматка по дню (все классы)» ----------

    private void BuildChessboard(List<ScheduleRowVm> dayRows)
    {
        MatrixGrid.Children.Clear();
        MatrixGrid.RowDefinitions.Clear();
        MatrixGrid.ColumnDefinitions.Clear();
        if (_problem is null) return;
        int dayIndex = SelectedDayIndex();
        int slots = _problem.SlotsPerDay;
        var classes = _problem.Classes.Values.Select(c => c.Name).OrderBy(n => n).ToList();
        if (classes.Count == 0) return;

        for (int c = 0; c <= classes.Count; c++)
            MatrixGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = c == 0 ? new GridLength(110) : new GridLength(1, GridUnitType.Star),
                MinWidth = c == 0 ? 110 : 130,
            });
        for (int r = 0; r <= slots; r++)
            MatrixGrid.RowDefinitions.Add(new RowDefinition
            {
                Height = r == 0 ? GridLength.Auto : new GridLength(62),
            });

        var accent = Br("BAccent", Brushes.Indigo);
        var accentSoft = Br("BAccentSoft", Brushes.Lavender);
        var card = Br("BCard", Brushes.White);
        var subtle = Br("BSubtle", Brushes.WhiteSmoke);
        var border = Br("BBorder", Brushes.LightGray);
        var text = Br("BText", Brushes.Black);
        var textSoft = Br("BTextSoft", Brushes.Gray);
        var textMuted = Br("BTextMuted", Brushes.DarkGray);

        var corner = new Border
        {
            Background = subtle, BorderBrush = border, BorderThickness = new Thickness(0, 0, 1, 1),
            Padding = new Thickness(8, 6, 8, 6),
            Child = new TextBlock
            {
                Text = "УРОК / КЛАСС", FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = textSoft,
            },
        };
        System.Windows.Controls.Grid.SetRow(corner, 0); System.Windows.Controls.Grid.SetColumn(corner, 0);
        MatrixGrid.Children.Add(corner);

        for (int c = 0; c < classes.Count; c++)
        {
            string cls = classes[c];
            int count = dayRows.Count(r => r.ClassName == cls);
            var hb = new Border
            {
                Background = subtle, BorderBrush = border, BorderThickness = new Thickness(0, 0, 1, 1),
                Padding = new Thickness(8, 6, 8, 6),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = cls, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = text },
                        new TextBlock { Text = $"{count} уроков", FontSize = 11, Foreground = textSoft },
                    },
                },
            };
            System.Windows.Controls.Grid.SetRow(hb, 0); System.Windows.Controls.Grid.SetColumn(hb, c + 1);
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
                    Foreground = text, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            System.Windows.Controls.Grid.SetRow(sb, s + 1); System.Windows.Controls.Grid.SetColumn(sb, 0);
            MatrixGrid.Children.Add(sb);

            for (int c = 0; c < classes.Count; c++)
            {
                string cls = classes[c];
                var cellRows = dayRows.Where(r => r.ClassName == cls && r.Slot == s + 1).ToList();
                var tag = new MatrixCellTag(dayIndex, s + 1, cellRows.Count > 0 ? cellRows[0] : null);
                Border cell;
                if (cellRows.Count == 0)
                {
                    cell = new Border
                    {
                        BorderBrush = border, BorderThickness = new Thickness(0, 0, 1, 1),
                        Padding = new Thickness(8), Tag = tag, Background = card,
                        Child = new TextBlock
                        {
                            Text = "—", FontSize = 12, Foreground = textMuted,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    };
                    cell.Cursor = Cursors.Cross;
                }
                else
                {
                    var lines = new StackPanel { Margin = new Thickness(6, 2, 4, 2) };
                    foreach (var r in cellRows)
                        lines.Children.Add(new TextBlock
                        {
                            Text = $"{r.SubjectName} / {r.TeacherName} / {UiFormat.Room(r.RoomName)}",
                            FontSize = 11, Foreground = text, TextWrapping = TextWrapping.Wrap,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        });
                    cell = new Border
                    {
                        BorderBrush = border, BorderThickness = new Thickness(0, 0, 1, 1),
                        Padding = new Thickness(2), Cursor = Cursors.Hand, Tag = tag,
                        CornerRadius = new CornerRadius(6), Child = lines,
                    };
                    cell.ToolTip = string.Join("\n", cellRows.Select(r =>
                        $"{r.ClassName} · {r.SubjectName} · {r.TeacherName} · {UiFormat.Room(r.RoomName)}"));
                }

                var (bg, bd, th) = CellPaint(cell.Tag, card, border, accent, accentSoft);
                cell.Background = bg;
                cell.BorderBrush = bd;
                cell.BorderThickness = th;
                AttachCellHandlers(cell);
                System.Windows.Controls.Grid.SetRow(cell, s + 1); System.Windows.Controls.Grid.SetColumn(cell, c + 1);
                MatrixGrid.Children.Add(cell);
            }
        }
    }

    // ---------- Подсветка и правка клеток ----------

    // Приоритет: выбранный урок → клетка-цель → оценка хода (зелёный/жёлтый/красный).
    private (Brush Bg, Brush Border, Thickness Thickness) CellPaint(
        object? tag, Brush card, Brush border, Brush accent, Brush accentSoft)
    {
        var thickness = new Thickness(0, 0, 1, 1);
        if (tag is not MatrixCellTag cell) return (card, border, thickness);
        bool isSelected = _selectedRow is not null && cell.Row is not null &&
            cell.Row.OccurrenceId == _selectedRow.OccurrenceId &&
            cell.DayIndex == _selectedRow.DayIndex && cell.Slot == _selectedRow.Slot;
        bool isTarget = _targetCell is { } t && t.Day == cell.DayIndex && t.Slot == cell.Slot;
        bool hasEval = _cellEvals.TryGetValue((cell.DayIndex, cell.Slot), out var ev);

        if (isSelected)
            return (accentSoft, accent, new Thickness(2));
        // Клетка, куда переносим: акцентная рамка + цвет вердикта.
        if (isTarget && hasEval)
            return (ev!.Allowed ? accentSoft : Br("BBadBg", Brushes.MistyRose), accent, new Thickness(2));
        if (isTarget)
            return (accentSoft, accent, new Thickness(2));
        if (hasEval)
        {
            if (!ev!.Allowed)
                return (Br("BBadBg", Brushes.MistyRose), Br("BBad", Brushes.IndianRed), new Thickness(1));
            return ev.Delta > 0
                ? (Br("BWarnBg", Brushes.LemonChiffon), Br("BWarn", Brushes.DarkGoldenrod), new Thickness(1))
                : (Br("BGoodBg", Brushes.Honeydew), Br("BGood", Brushes.Green), new Thickness(1));
        }
        return (card, border, thickness);
    }

    private void AttachCellHandlers(Border cell)
    {
        cell.AllowDrop = true;
        cell.MouseLeftButtonDown += OnMatrixCellMouseDown;
        cell.MouseMove += OnMatrixCellMouseMove;
        cell.MouseLeftButtonUp += OnMatrixCellClick;
        cell.DragOver += OnMatrixCellDragOver;
        cell.Drop += OnMatrixCellDrop;
    }

    private void CancelCellEvaluations()
    {
        var prev = _evalCts;
        _evalCts = null;
        if (prev is null) return;
        try { prev.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    // P5: для всех клеток открытой матрицы считаем реальный Preview выбранного урока в фоне.
    private void RefreshCellEvaluations()
    {
        CancelCellEvaluations();
        _cellEvals.Clear();
        if (MatrixScroll.Visibility != Visibility.Visible) return;
        if (_selectedRow is not { } row || _problem is not { } problem || _active is not { } active) return;

        var cts = new CancellationTokenSource();
        _evalCts = cts;
        _ = StartCellEvaluationsAsync(row, problem, active.Placements, _session.QualityRules, cts);
    }

    private async Task StartCellEvaluationsAsync(
        ScheduleRowVm row,
        SchedulingProblem problem,
        IReadOnlyList<PlacedLesson> placements,
        EffectiveRuleSet rules,
        CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            var computed = await Task.Run(() =>
            {
                var map = new Dictionary<(int Day, int Slot), CellEval>();
                for (int d = 0; d < problem.DaysCount; d++)
                {
                    for (int s = 1; s <= problem.SlotsPerDay; s++)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            var ev = _session.EditService.Preview(problem, placements,
                                new CandidateMove(row.OccurrenceId, d, s, row.RoomId), rules);
                            map[(d, s)] = new CellEval(ev.CanCommit, ev.SoftDelta, ev.VerdictText);
                        }
                        catch (Exception ex)
                        {
                            map[(d, s)] = new CellEval(false, 0, "Ошибка проверки хода: " + ex.Message);
                        }
                    }
                }
                return map;
            }, token);

            if (token.IsCancellationRequested ||
                _evalCts != cts ||
                _selectedRow?.OccurrenceId != row.OccurrenceId ||
                MatrixScroll.Visibility != Visibility.Visible)
                return;

            _cellEvals.Clear();
            foreach (var kv in computed)
                _cellEvals[kv.Key] = kv.Value;
            RebuildMatrix();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (_evalCts == cts)
            {
                VerdictText.Text = "Ошибка оценки клеток: " + ex.Message;
                StyleVerdict(VerdictText.Text);
            }
        }
        finally
        {
            if (_evalCts == cts) _evalCts = null;
            cts.Dispose();
        }
    }

    private void SelectRow(ScheduleRowVm row)
    {
        _selectedRow = row;
        _targetCell = null;
        _preview = null;
        Grid.SelectedItem = null;
        DayBox.Text = row.Day.ToString();
        SlotBox.Text = row.Slot.ToString();
        CommitBtn.Content = "Сохранить ход";
        CommitBtn.IsEnabled = true;
        SelectedInfoText.Text = $"{row.ClassName} · {row.SubjectName} · {row.TeacherName} · {UiFormat.Room(row.RoomName)} (день {row.Day}, урок {row.Slot})";
        RefreshCellEvaluations();
        RebuildMatrix();
    }

    private void ClearLessonSelection()
    {
        CancelCellEvaluations();
        _selectedRow = null;
        _targetCell = null;
        _preview = null;
        _cellEvals.Clear();
        Grid.SelectedItem = null;
        CommitBtn.Content = "Сохранить ход";
        CommitBtn.IsEnabled = true;
        SelectedInfoText.Text = "Выберите урок в сетке или таблице";
        VerdictText.Text = "";
        StyleVerdict("");
        RebuildMatrix();
    }

    private void RebuildMatrix()
    {
        if (_problem is null || MatrixScroll.Visibility != Visibility.Visible) return;
        if (ViewBox.SelectedIndex == 4) BuildChessboard(GetFilteredRows());
        else BuildMatrix(GetFilteredRows(), teacherMode: ViewBox.SelectedIndex == 2);
    }

    private void OnMatrixCellClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b || b.Tag is not MatrixCellTag cell) return;
        try
        {
            if (_selectedRow is not null)
            {
                bool own = cell.Row is not null &&
                    cell.Row.OccurrenceId == _selectedRow.OccurrenceId &&
                    cell.DayIndex == _selectedRow.DayIndex && cell.Slot == _selectedRow.Slot;
                if (own) { ClearLessonSelection(); return; }
                BeginTargetMove(_selectedRow, cell.DayIndex, cell.Slot);
                if (e.ClickCount >= 2)
                {
                    if (_preview is { CanCommit: true }) _ = CommitPreviewAsync();
                    else if (cell.Row is not null) SelectRow(cell.Row); // цель запрещена — выбираем урок
                }
                return;
            }
            if (cell.Row is not null) SelectRow(cell.Row);
        }
        catch (Exception ex)
        {
            VerdictText.Text = "Ошибка: " + ex.Message;
            StyleVerdict(VerdictText.Text);
        }
    }

    // Клик по другой клетке: подставляем день/урок, гоняем Preview, зажигаем кнопку переноса.
    private void BeginTargetMove(ScheduleRowVm row, int dayIndex, int slot)
    {
        _selectedRow = row;
        _targetCell = (dayIndex, slot);
        DayBox.Text = (dayIndex + 1).ToString();
        SlotBox.Text = slot.ToString();
        _preview = null;
        try
        {
            _preview = _session.EditService.Preview(_problem!, _active!.Placements,
                new CandidateMove(row.OccurrenceId, dayIndex, slot, row.RoomId), _session.QualityRules);
            VerdictText.Text = _preview.VerdictText;
            StyleVerdict(_preview.VerdictText);
        }
        catch (Exception ex)
        {
            VerdictText.Text = "Ошибка: " + ex.Message;
            StyleVerdict(VerdictText.Text);
            _preview = null;
        }
        CommitBtn.Content = "✓ Переместить сюда";
        CommitBtn.IsEnabled = _preview?.CanCommit == true;
        SelectedInfoText.Text = _preview?.CanCommit == true
            ? $"{row.ClassName} · {row.SubjectName} → день {dayIndex + 1}, урок {slot} (двойной клик — сразу)"
            : $"{row.ClassName} · {row.SubjectName} → день {dayIndex + 1}, урок {slot} (нельзя)";
        RebuildMatrix();
    }

    // ---------- Drag-and-Drop ----------

    private void OnMatrixCellMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b) return;
        _dragStart = e.GetPosition(null);
        _dragSource = b.Tag is MatrixCellTag { Row: not null } ? b : null;
    }

    private void OnMatrixCellMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragSource is null) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (_dragSource.Tag is not MatrixCellTag { Row: { } row }) { _dragSource = null; return; }
        var source = _dragSource;
        _dragSource = null;
        if (_selectedRow?.OccurrenceId != row.OccurrenceId) SelectRow(row);
        try { DragDrop.DoDragDrop(source, row.OccurrenceId, DragDropEffects.Move); }
        finally { _dragSource = null; }
    }

    private static void OnMatrixCellDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(Guid)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnMatrixCellDrop(object sender, DragEventArgs e)
    {
        try
        {
            if (sender is not Border b || b.Tag is not MatrixCellTag cell) return;
            if (e.Data.GetData(typeof(Guid)) is not Guid id) return;
            var row = _allRows.FirstOrDefault(r => r.OccurrenceId == id);
            if (row is null || _problem is null || _active is null) return;
            if (cell.DayIndex == row.DayIndex && cell.Slot == row.Slot) return;
            _selectedRow = row;
            RefreshCellEvaluations();
            BeginTargetMove(row, cell.DayIndex, cell.Slot);
            // Drop — сразу выполняем перенос, если ход допустим.
            if (_preview is { CanCommit: true }) _ = CommitPreviewAsync();
        }
        catch (Exception ex)
        {
            VerdictText.Text = "Ошибка: " + ex.Message;
            StyleVerdict(VerdictText.Text);
        }
    }

    // P-D2: акцент карточки по области предмета (Stitch §3.4).
    // UI-only эвристика по имени: точные — индиго, гуманитарные — янтарь,
    // спорт/труд/искусства — зелёный. Порядок важен: «физическая культура»
    // содержит «физик», поэтому спорт проверяется первым.
    private static string AreaOf(string subject)
    {
        string s = subject.ToLowerInvariant();
        if (s.Contains("культур") || s.Contains("труд") || s.Contains("искусств") ||
            s.Contains("музык") || s.Contains("дмп") || s.Contains("физ-ра"))
            return "sport";
        if (s.Contains("матем") || s.Contains("алгебр") || s.Contains("геометр") ||
            s.Contains("физик") || s.Contains("хими") || s.Contains("биолог") ||
            s.Contains("информат") || s.Contains("астрон") || s.Contains("черчен"))
            return "stem";
        return "hum";
    }

    private Brush AreaBrush(string area) => area switch
    {
        "stem" => Br("BAccent", Brushes.Indigo),
        "sport" => Br("BGood", Brushes.Green),
        _ => Br("BWarn", Brushes.DarkGoldenrod),
    };

    // Мягкая стилизация вердикта по префиксу (текст приходит из Preview как есть).
    private void StyleVerdict(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            VerdictBorder.Visibility = Visibility.Collapsed;
            return;
        }
        VerdictBorder.Visibility = Visibility.Visible;
        // Без ресурсов App (STA-тест) — только текст, без стилизации.
        if (TryFindResource("BSubtle") is not Brush) return;
        string t = text.ToLowerInvariant();
        string bgKey = "BSubtle", bdKey = "BBorder", fgKey = "BText";
        if (t.Contains("запрещ") || t.Contains("нельзя") || t.Contains("жёстк") ||
            t.Contains("конфликт") || t.Contains("ошибка") || t.Contains("недопуст"))
        {
            bgKey = "BBadBg"; bdKey = "BBad"; fgKey = "BBad";
        }
        else if (t.Contains("ухудш") || t.Contains("замечан") || t.Contains("штраф") ||
                 t.Contains("окно") || t.Contains("допуст"))
        {
            bgKey = "BWarnBg"; bdKey = "BWarn"; fgKey = "BWarn";
        }
        else if (t.Contains("можно") || t.Contains("разреш") || t.Contains("оптимал") ||
                 t.Contains("свободен") || t.Contains("успеш") || t.Contains("выгружено") ||
                 t.Contains("сохран") || t.Contains("принят"))
        {
            bgKey = "BGoodBg"; bdKey = "BGood"; fgKey = "BGood";
        }
        Bind(VerdictBorder, Border.BackgroundProperty, bgKey);
        Bind(VerdictBorder, Border.BorderBrushProperty, bdKey);
        Bind(VerdictText, TextBlock.ForegroundProperty, fgKey);
    }

    private void SetViewMode(int mode)
    {
        if (ViewBox.SelectedIndex == mode) return;
        bool saved = _suspendViewEvents;
        _suspendViewEvents = true;
        try { ViewBox.SelectedIndex = mode; }
        finally { _suspendViewEvents = saved; }
    }

    private void OnViewChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspendViewEvents) return;
        if (!IsLoaded) return; // событие при парсинге XAML — контролы ещё не готовы
        FillNameBox();
        EnsureNameSelection();
        ApplyViewFilter();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_suspendViewEvents || !IsLoaded) return;
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
            _targetCell = null;
            _preview = null;
            DayBox.Text = row.Day.ToString();
            SlotBox.Text = row.Slot.ToString();
            CommitBtn.Content = "Сохранить ход";
            CommitBtn.IsEnabled = true;
            SelectedInfoText.Text = $"{row.ClassName} · {row.SubjectName} · {row.TeacherName} · {UiFormat.Room(row.RoomName)} (день {row.Day}, урок {row.Slot})";
            RefreshCellEvaluations();
            RebuildMatrix();
        }
    }

    private void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var row = GetSelected();
            int day = ParseDay(), slot = ParseSlot();
            _preview = _session.EditService.Preview(_problem!, _active!.Placements,
                new CandidateMove(row.OccurrenceId, day, slot, row.RoomId), _session.QualityRules);
            _targetCell = (day, slot);
            VerdictText.Text = _preview.VerdictText;
            StyleVerdict(_preview.VerdictText);
            CommitBtn.Content = "✓ Переместить сюда";
            CommitBtn.IsEnabled = _preview.CanCommit;
            RebuildMatrix();
        }
        catch (Exception ex)
        {
            VerdictText.Text = "Ошибка: " + ex.Message;
            StyleVerdict(VerdictText.Text);
            _preview = null;
        }
    }

    private async Task CommitPreviewAsync()
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
                _session.AcademicYearId, _problem!, _preview, _session.QualityRules);
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

    private async void OnCommitClick(object sender, RoutedEventArgs e) => await CommitPreviewAsync();

    // P5: экспорт — навигация внутри оболочки; старое модальное окно — только без навигатора.
    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (Navigator is not null)
        {
            Navigator.NavigateTo("Export");
            return;
        }
        new System.Windows.Window
        {
            Title = "Экспорт и печать",
            Content = new Views.ExportView(_session),
            Owner = System.Windows.Window.GetWindow(this),
            Width = 1100,
            Height = 780,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Style = (Style)FindResource("ChromeWindow"),
        }.ShowDialog();
    }

    // P-SANPIN-UI: проверка активного расписания по нормам СанПиН РБ.
    // Кнопка всегда жива: без активного — понятное сообщение, а не мёртвая кнопка.
    private void OnSanPinClick(object sender, RoutedEventArgs e)
    {
        if (_problem is null || _active is null)
        {
            MessageBox.Show("Нет активного расписания — сгенерируйте варианты и нажмите «Принять расписание».",
                "АМСУР", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var findings = SanPinChecker.Check(_problem, _active.Placements);
        IReadOnlyList<string> lines = findings.Count == 0
            ? ["Нарушений СанПиН не найдено. Проверены: нагрузка в день, 1-е классы, физкультура первым уроком. Цифры норм требуют сверки с НПА."]
            : findings.Select(f => (f.IsError ? "Нарушение: " : "Пожелание: ") + f.Text).ToList();
        new QualityDetailsWindow("СанПиН — проверка расписания", lines) { Owner = System.Windows.Window.GetWindow(this) }.ShowDialog();
    }

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
