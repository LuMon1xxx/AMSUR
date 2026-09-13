using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Amsur.Application;
using Amsur.Scheduling.Core;
using Microsoft.Win32;

namespace Amsur.Wpf;

// Этап F: экран настроек качества (промт §§7–9). Логика уровней — в
// QualitySettingsEditor (Application, тестируется); здесь только построение
// строк и чтение контролов. Сущности школы правятся в сессии (до перезапуска),
// веса CUSTOM — persist в SQLite.
public partial class SettingsWindow : Window
{
    private readonly AppSession _session;
    private QualitySettingsEditor _editor;
    private string _baseProfile = "STANDARD";
    private readonly List<(Guid Id, TextBox Box, int Min, int Max, string Label)> _entityBoxes = [];
    private readonly List<(string Code, TextBox Box)> _expertBoxes = [];

    public SettingsWindow(AppSession session)
    {
        _session = session;
        InitializeComponent();
        var cur = session.QualityRules.ProfileName;
        _baseProfile = cur == "CUSTOM" ? "STANDARD" : cur;
        _editor = QualitySettingsEditor.FromRules(session.QualityRules);
        CheckPresetRadio();
        SavedNameText.Text = session.CustomProfileName is null ? "" : $"Сохранён: {session.CustomProfileName}";
        BuildQualityPanel();
        BuildEntityPanels();
        BuildExpertPanel();
        RefreshSectionCounts();
        MarkClean();
    }

    private void CheckPresetRadio()
    {
        var cur = _session.QualityRules.ProfileName;
        PresetStandard.IsChecked = cur == "STANDARD";
        PresetStudent.IsChecked = cur == "STUDENT_FRIENDLY";
        PresetTeacher.IsChecked = cur == "TEACHER_FRIENDLY";
        PresetCustom.IsChecked = cur == "CUSTOM";
    }

    // P0-5: честные счётчики секций (из Session.Data, иначе 0 — без выдумок).
    private void RefreshSectionCounts()
    {
        var d = _session.Data;
        SetSectionText(0, $"Качество ({_editor.Options.Count})");
        SetSectionText(1, $"Предметы ({d?.Subjects.Count ?? 0})");
        SetSectionText(2, $"Классы ({d?.Classes.Count ?? 0})");
        SetSectionText(3, $"Учителя ({d?.Teachers.Count ?? 0})");
        SetSectionText(4, "Экспертный режим");
    }

    private void SetSectionText(int index, string text)
    {
        if (SectionList.Items[index] is ListBoxItem item)
            item.Content = text;
    }

    // --- Качество ---
    private void BuildQualityPanel()
    {
        QualityPanel.Children.Clear();
        foreach (var o in _editor.Options)
        {
            var card = new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 8) };
            var sp = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock
            {
                Text = o.Title, FontSize = 15, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            head.Children.Add(new Border
            {
                Style = (Style)FindResource("Badge"),
                Background = o.IsStrict
                    ? new SolidColorBrush((Color)FindResource("CBadBg"))
                    : (Brush)FindResource("BAccentSoft"),
                Margin = new Thickness(8, 0, 0, 0),
                Child = new TextBlock
                {
                    // P0-5: бейдж СТРОГОЕ — только для правил с диапазоном (0,0).
                    Text = o.IsStrict ? "СТРОГОЕ" : "ПОЖЕЛАНИЕ", FontSize = 11,
                    Foreground = o.IsStrict
                        ? new SolidColorBrush((Color)FindResource("CBad"))
                        : new SolidColorBrush((Color)FindResource("CAccent")),
                },
            });
            sp.Children.Add(head);
            var hint = new TextBlock
            {
                Text = o.Hint, FontSize = 12,
                Foreground = new SolidColorBrush((Color)FindResource("CTextSoft")),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
            };
            sp.Children.Add(hint);
            if (o.Tunable)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                var slider = new Slider
                {
                    Minimum = 0, Maximum = 4, Value = o.Level, Width = 220,
                    TickFrequency = 1, IsSnapToTickEnabled = true,
                    VerticalAlignment = VerticalAlignment.Center, Tag = o.Code,
                };
                slider.ValueChanged += OnSliderChanged;
                var level = new TextBlock
                {
                    Text = o.LevelName, FontSize = 13, FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
                    Tag = o.Code + "|label",
                };
                var num = new TextBlock
                {
                    Text = $"({o.Weight})", FontSize = 12,
                    Foreground = new SolidColorBrush((Color)FindResource("CTextSoft")),
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
                    Tag = o.Code + "|num",
                };
                row.Children.Add(slider);
                row.Children.Add(level);
                row.Children.Add(num);
                sp.Children.Add(row);
                // P0-5: честная подпись диапазона из WeightRange + дефолт (без выдуманных %).
                var rangeCaption = new TextBlock
                {
                    Text = $"Вес {o.Weight} · диапазон {o.Min}..{o.Max} · дефолт {o.DefaultWeight}" +
                        (RuleCatalog.NeedsConfirmation(o.Code) ? " · требует сверки с нормами" : ""),
                    FontSize = 11,
                    Foreground = new SolidColorBrush((Color)FindResource("CTextSoft")),
                    Margin = new Thickness(0, 4, 0, 0),
                    Tag = o.Code + "|range",
                };
                sp.Children.Add(rangeCaption);
                RegisterRow(o.Code, slider, level, num, rangeCaption);
            }
            else
            {
                sp.Children.Add(new TextBlock
                {
                    Text = o.IsStrict
                        ? "Обязательное правило: нарушение недопустимо. Настройкой не меняется."
                        : "",
                    FontSize = 12,
                    Foreground = new SolidColorBrush((Color)FindResource("CTextSoft")),
                    Margin = new Thickness(0, 6, 0, 0),
                });
            }
            card.Child = sp;
            QualityPanel.Children.Add(card);
        }
    }

    private readonly Dictionary<string, (Slider S, TextBlock L, TextBlock N, TextBlock R)> _rows = [];

    private void RegisterRow(string code, Slider s, TextBlock l, TextBlock n, TextBlock r) =>
        _rows[code] = (s, l, n, r);

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is not Slider sl || sl.Tag is not string code) return;
        _editor.SetLevel(code, (int)sl.Value);
        if (_rows.TryGetValue(code, out var r))
        {
            var o = _editor.Options.First(x => x.Code == code);
            r.L.Text = o.LevelName;
            r.N.Text = $"({o.Weight})";
            r.R.Text = $"Вес {o.Weight} · диапазон {o.Min}..{o.Max} · дефолт {o.DefaultWeight}" +
                (RuleCatalog.NeedsConfirmation(o.Code) ? " · требует сверки с нормами" : "");
        }
        MarkDirty();
        RefreshExpertBoxes();
        RefreshJsonPreview();
    }

    // --- Предметы / Классы / Учителя ---
    private void BuildEntityPanels()
    {
        SubjectsPanel.Children.Clear();
        ClassesPanel.Children.Clear();
        TeachersPanel.Children.Clear();
        _entityBoxes.Clear();
        var data = _session.Data;
        if (data is null)
        {
            SubjectsPanel.Children.Add(EmptyText("Сначала загрузите данные школы — здесь появятся предметы."));
            ClassesPanel.Children.Add(EmptyText("Сначала загрузите данные школы — здесь появятся классы."));
            TeachersPanel.Children.Add(EmptyText("Сначала загрузите данные школы — здесь появятся учителя."));
            return;
        }
        SubjectsPanel.Children.Add(SectionHint("Сколько раз предмет может стоять у класса в день (повторы сверх — пожелание с весом)."));
        foreach (var s in data.Subjects.OrderBy(x => x.Name))
            AddEntityRow(SubjectsPanel, s.Name, $"предмет «{s.Name}»", s.Id,
                s.MaxPerDay, 1, 7, v => s.MaxPerDay = v);
        ClassesPanel.Children.Add(SectionHint("Дневная норма класса. Начало дня: позже 2-го урока начинать нельзя (строгое правило, не меняется)."));
        foreach (var c in data.Classes.OrderBy(x => x.Name))
            AddEntityRow(ClassesPanel, $"{c.Name} (параллель {c.Grade})", $"класс {c.Name}", c.Id,
                c.MaxLessonsPerDay, 1, 10, v => c.MaxLessonsPerDay = v);
        TeachersPanel.Children.Add(SectionHint("Дневной лимит учителя. Пожелания по конкретным дням/часам — следующая версия (зафиксировано в backlog)."));
        foreach (var t in data.Teachers.OrderBy(x => x.Name).Take(200))
            AddEntityRow(TeachersPanel, t.Name, $"учитель {t.Name}", t.Id,
                t.MaxLessonsPerDay, 1, 12, v => t.MaxLessonsPerDay = v);
        if (data.Teachers.Count > 200)
            TeachersPanel.Children.Add(SectionHint($"…и ещё {data.Teachers.Count - 200} (список обрезан для скорости)."));
    }

    private static TextBlock EmptyText(string s) => new()
    {
        Text = s, FontSize = 13,
        Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
        TextWrapping = TextWrapping.Wrap,
    };

    private static TextBlock SectionHint(string s) => new()
    {
        Text = s, FontSize = 12,
        Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
    };

    private void AddEntityRow(StackPanel panel, string title, string what, Guid id,
        int cur, int min, int max, Action<int> apply)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        row.Children.Add(new TextBlock
        {
            Text = title, FontSize = 13, Width = 300, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = title,
        });
        var box = new TextBox { Text = cur.ToString(), Width = 50, Margin = new Thickness(8, 0, 0, 0), Tag = id };
        box.TextChanged += (_, _) => MarkDirty();
        box.Tag = new EntityTag(id, min, max, what, apply);
        row.Children.Add(box);
        panel.Children.Add(row);
        _entityBoxes.Add((id, box, min, max, what));
    }

    private sealed record EntityTag(Guid Id, int Min, int Max, string What, Action<int> Apply);

    // --- Эксперт ---
    private void BuildExpertPanel()
    {
        ExpertPanel.Children.Clear();
        _expertBoxes.Clear();
        ExpertPanel.Children.Add(SectionHint(
            "Точные числа весов. Обычному пользователю не нужны — достаточно слайдеров «Качество». " +
            "СанПиН-параметры помечены «требует сверки с нормами» и здесь не настраиваются."));
        foreach (var o in _editor.Options.Where(x => x.Tunable))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            row.Children.Add(new TextBlock
            {
                Text = $"{o.Title} ({o.Min}..{o.Max}):", FontSize = 13, Width = 300,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var box = new TextBox { Text = o.Weight.ToString(), Width = 60, Tag = o.Code };
            box.TextChanged += (_, _) => MarkDirty();
            row.Children.Add(box);
            ExpertPanel.Children.Add(row);
            _expertBoxes.Add((o.Code, box));
        }
        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var exp = new Button { Content = "Выгрузить профиль (JSON)", Style = (Style)FindResource("BtnSecondary") };
        exp.Click += OnExportJsonClick;
        var imp = new Button
        {
            Content = "Загрузить профиль (JSON)", Style = (Style)FindResource("BtnSecondary"),
            Margin = new Thickness(8, 0, 0, 0),
        };
        imp.Click += OnImportJsonClick;
        btns.Children.Add(exp);
        btns.Children.Add(imp);
        ExpertPanel.Children.Add(btns);
        // P0-5: JSON-превью текущих правок текстом (UI-only, честно: пусто = пресет без правок).
        ExpertPanel.Children.Add(new TextBlock
        {
            Text = "Текущие правки (JSON):", FontSize = 12,
            Foreground = new SolidColorBrush((Color)FindResource("CTextSoft")),
            Margin = new Thickness(0, 12, 0, 4),
        });
        _jsonPreview = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, MaxLines = 8,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"), FontSize = 12,
            MinHeight = 60, TextWrapping = TextWrapping.Wrap,
        };
        ExpertPanel.Children.Add(_jsonPreview);
        RefreshJsonPreview();
    }

    private TextBox? _jsonPreview;

    private void RefreshJsonPreview()
    {
        if (_jsonPreview is null) return;
        var ov = _editor.GetOverrides();
        _jsonPreview.Text = ov.Count == 0
            ? "(нет правок — применён пресет без изменений)"
            : JsonSerializer.Serialize(ov, new JsonSerializerOptions { WriteIndented = true });
    }

    private void RefreshExpertBoxes()
    {
        foreach (var (code, box) in _expertBoxes)
        {
            var o = _editor.Options.First(x => x.Code == code);
            if (box.Text != o.Weight.ToString()) box.Text = o.Weight.ToString();
        }
    }

    // --- Секции / пресеты / кнопки ---
    private void OnSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return; // событие при парсинге XAML
        int i = SectionList.SelectedIndex;
        QualityPanel.Visibility = i == 0 ? Visibility.Visible : Visibility.Collapsed;
        SubjectsPanel.Visibility = i == 1 ? Visibility.Visible : Visibility.Collapsed;
        ClassesPanel.Visibility = i == 2 ? Visibility.Visible : Visibility.Collapsed;
        TeachersPanel.Visibility = i == 3 ? Visibility.Visible : Visibility.Collapsed;
        ExpertPanel.Visibility = i == 4 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPresetChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _baseProfile = PresetStudent.IsChecked == true ? "STUDENT_FRIENDLY"
            : PresetTeacher.IsChecked == true ? "TEACHER_FRIENDLY"
            : "STANDARD";
        _editor = QualitySettingsEditor.FromRules(RuleResolver.Resolve(_baseProfile));
        BuildQualityPanel();
        BuildExpertPanel();
        RefreshJsonPreview();
        MarkDirty();
        Say($"Выбран пресет. Ручные правки сброшены.", false);
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        PresetStandard.IsChecked = true;
        _baseProfile = "STANDARD";
        _editor = QualitySettingsEditor.FromRules(EffectiveRuleSet.Default);
        BuildQualityPanel();
        BuildExpertPanel();
        RefreshJsonPreview();
        MarkDirty();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private bool ReadEntityBoxes(out string? error)
    {
        foreach (var (_, box, min, max, what) in _entityBoxes)
        {
            if (!int.TryParse(box.Text, out int v) || v < min || v > max)
            {
                error = $"Для «{what}» нужно число {min}..{max}.";
                return false;
            }
        }
        error = null;
        return true;
    }

    private bool ReadExpertBoxes()
    {
        foreach (var (code, box) in _expertBoxes)
        {
            if (!long.TryParse(box.Text, out long w)) return false;
            try { _editor.SetWeight(code, w); }
            catch { return false; }
        }
        BuildQualityPanel();
        RefreshJsonPreview();
        return true;
    }

    private void ApplyEntities()
    {
        foreach (var (_, box, _, _, _) in _entityBoxes)
        {
            if (box.Tag is EntityTag tag && int.TryParse(box.Text, out int v))
                tag.Apply(v);
        }
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (!ReadEntityBoxes(out string? err)) { Say(err!, true); return; }
        if (!ReadExpertBoxes()) { Say("В экспертном режиме — только числа из указанных диапазонов.", true); return; }
        ApplyEntities();
        _session.ApplyOverrides(_editor.GetOverrides());
        MarkClean();
        Say(_editor.IsModified
            ? "Применено: настройки будут действовать на следующие генерации (без сохранения)."
            : "Применён пресет без изменений.", false);
        CheckPresetAfterApply();
    }

    private async void OnSaveCustomClick(object sender, RoutedEventArgs e)
    {
        if (!ReadEntityBoxes(out string? err)) { Say(err!, true); return; }
        if (!ReadExpertBoxes()) { Say("В экспертном режиме — только числа из указанных диапазонов.", true); return; }
        string name = CustomNameBox.Text.Trim();
        if (name.Length == 0) { Say("Введите имя профиля.", true); return; }
        try
        {
            ApplyEntities();
            await _session.SaveCustomProfileAsync(name, _baseProfile, _editor.GetOverrides());
            SavedNameText.Text = $"Сохранён: {name}";
            PresetCustom.IsChecked = true;
            MarkClean();
            Say($"Профиль «{name}» сохранён — восстановится при следующем открытии.", false);
        }
        catch (Exception ex) { Say("Не сохранено: " + ex.Message, true); }
    }

    private void CheckPresetAfterApply()
    {
        PresetCustom.IsChecked = _editor.IsModified;
        if (!_editor.IsModified)
        {
            if (_baseProfile == "STUDENT_FRIENDLY") PresetStudent.IsChecked = true;
            else if (_baseProfile == "TEACHER_FRIENDLY") PresetTeacher.IsChecked = true;
            else PresetStandard.IsChecked = true;
        }
    }

    private void OnExportJsonClick(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "профиль.json" };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(
            _editor.GetOverrides(), new JsonSerializerOptions { WriteIndented = true }));
        Say("Профиль выгружен: " + dlg.FileName, false);
    }

    private void OnImportJsonClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var ov = JsonSerializer.Deserialize<Dictionary<string, long>>(
                File.ReadAllText(dlg.FileName)) ?? [];
            _ = RuleResolver.Resolve("CUSTOM", ov); // валидация до применения
            _editor = QualitySettingsEditor.FromRules(EffectiveRuleSet.Default);
            foreach (var (code, w) in ov) _editor.SetWeight(code, w);
            BuildQualityPanel();
            RefreshExpertBoxes();
            MarkDirty();
            Say("Профиль загружен — нажмите «Применить».", false);
        }
        catch (Exception ex) { Say("Файл не принят: " + ex.Message, true); }
    }

    private void MarkDirty()
    {
        DirtyText.Text = "Есть несохранённые изменения";
    }

    private void MarkClean() => DirtyText.Text = "";

    private void Say(string text, bool isError)
    {
        MessageText.Text = text;
        MessageText.Foreground = isError
            ? new SolidColorBrush((Color)FindResource("CBad"))
            : new SolidColorBrush((Color)FindResource("CGood"));
    }
}
