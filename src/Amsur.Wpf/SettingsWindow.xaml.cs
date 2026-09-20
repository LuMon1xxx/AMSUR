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
    private readonly HashSet<string> _confirmedDangerous = new(StringComparer.Ordinal);
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
        // B2: уже ослабленные в активном CUSTOM — считаем подтверждёнными
        // (повторный попап при движении слайдера не нужен).
        foreach (var c in session.QualityRules.RelaxedStrict)
            _confirmedDangerous.Add(c);
        CheckPresetRadio();
        SavedNameText.Text = session.CustomProfileName is null ? "" : $"Сохранён: {session.CustomProfileName}";
        BuildQualityPanel();
        BuildEntityPanels();
        BuildExpertPanel();
        BuildWarningsPanel();
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
        SetSectionText(5, "Предупреждения");
    }

    private void SetSectionText(int index, string text)
    {
        if (SectionList.Items[index] is ListBoxItem item)
            item.Content = text;
    }

    // --- Качество ---
    private CheckBox? _gradePrioBox;
    private TextBox? _w11Box, _w9Box, _wOtherBox, _heavyBox;

    private void BuildQualityPanel()
    {
        QualityPanel.Children.Clear();
        QualityPanel.Children.Add(BuildGradePriorityCard());
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
                    // B2: опасные — бейдж «СТРОГОЕ (по шаблону)», но настраиваются через подтверждение.
                    Text = o.IsStrict ? "СТРОГОЕ (по шаблону)" : "ПОЖЕЛАНИЕ", FontSize = 11,
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
                // B2: опасным — тумблер «Разрешить как пожелание» (слайдер disabled пока off).
                bool isDanger = o.IsStrict;
                bool relaxed = isDanger &&
                    (_confirmedDangerous.Contains(o.Code) || o.Weight != o.DefaultWeight);
                if (isDanger)
                {
                    var toggle = new CheckBox
                    {
                        Content = "Разрешить как пожелание",
                        IsChecked = relaxed,
                        Margin = new Thickness(0, 8, 0, 0), FontSize = 13,
                        Tag = o.Code,
                    };
                    toggle.Checked += OnDangerToggleChanged;
                    toggle.Unchecked += OnDangerToggleChanged;
                    sp.Children.Add(toggle);
                }
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                var slider = new Slider
                {
                    Minimum = 0, Maximum = 4, Value = o.Level, Width = 220,
                    TickFrequency = 1, IsSnapToTickEnabled = true,
                    VerticalAlignment = VerticalAlignment.Center, Tag = o.Code,
                    IsEnabled = !isDanger || relaxed,
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
                // B2: честная подпись: вес + дефолт + «шаблон не так делает» для опасных.
                var rangeCaption = new TextBlock
                {
                    Text = DangerCaption(o),
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

    // B2: подпись опасной карточки: «Вес X · дефолт Y · шаблон не так делает».
    private static string DangerCaption(QualityOption o)
    {
        string base_ = $"Вес {o.Weight} · диапазон {o.Min}..{o.Max} · дефолт {o.DefaultWeight}";
        if (RuleCatalog.NeedsConfirmation(o.Code)) base_ += " · требует сверки с нормами";
        if (o.IsStrict) base_ += " · шаблон не так делает";
        return base_;
    }

    private static string DangerousConsequence(string code) => code switch
    {
        "student-gap" => "У учеников появятся окна посреди дня (пустые уроки). " +
            "Варианты с окнами станут допустимыми (со штрафом в оценке).",
        "student-late-start" => "Классы смогут начинать позже 2-го урока. " +
            "Дни «со второго урока» станут допустимыми (со штрафом в оценке).",
        "teacher-maxperday" => "Учителя смогут вести больше уроков в день, чем их лимит. " +
            "Перегрузка станет допустимой (с предупреждением).",
        "class-maxperday" => "У классов сможет быть больше уроков в день, чем норма " +
            "(включая норму 1-х классов). Перегруз станет допустимым (с предупреждением).",
        "teacher-overload" => "Дневной лимит поднимется ТОЛЬКО у тех учителей, чья недельная " +
            "нагрузка не влезает в норму. Это признание нехватки штата — будет видно в отчётах.",
        "cap-raise" => "Дневные лимиты сверх СанПиН-норм: нагрузка сверх нормы. " +
            "Снижение лимитов — без предупреждения.",
        _ when code.StartsWith("sanpin-", StringComparison.Ordinal) =>
            "Норма СанПиН будет ослаблена. Веса не проверены по НПА — сверьте с завучем и нормами.",
        _ => "Строгое правило будет ослаблено.",
    };

    // B2: тумблер опасного — через DangerConfirm (глобал-офф = без попапа).
    // Отмена → тумблер возвращается; Продолжить → слайдер включается.
    private async void OnDangerToggleChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box || box.Tag is not string code) return;
        var o = _editor.Options.FirstOrDefault(x => x.Code == code);
        if (o is null) return;
        if (box.IsChecked == true)
        {
            bool ok = await _session.ConfirmDangerousAsync(
                this, code, o.Title, DangerousConsequence(code));
            if (!ok)
            {
                box.IsChecked = false; // попап «Отмена» → не применилось
                return;
            }
            _confirmedDangerous.Add(code);
            if (_rows.TryGetValue(code, out var r)) r.S.IsEnabled = true;
            MarkDirty();
        }
        else
        {
            _confirmedDangerous.Remove(code);
            try { _editor.SetWeight(code, o.DefaultWeight, confirmed: true); }
            catch { /* дефолт всегда в диапазоне */ }
            if (_rows.TryGetValue(code, out var r))
            {
                r.S.IsEnabled = false;
                r.S.Value = o.Level;
                r.L.Text = o.LevelName;
                r.N.Text = $"({o.Weight})";
                r.R.Text = DangerCaption(o);
            }
            RefreshExpertBoxes();
            RefreshJsonPreview();
            MarkDirty();
        }
        RefreshJsonPreview();
    }

    // P4/R8+R6: приоритет выпускных и порог тяжести (FlexSettings, не веса каталога).
    private Border BuildGradePriorityCard()
    {
        var st = _session.Flex.Settings;
        var card = new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 8) };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = "Приоритет выпускных", FontSize = 15, FontWeight = FontWeights.SemiBold,
        });
        sp.Children.Add(new TextBlock
        {
            Text = "Мягкие штрафы 11-х и 9-х классов умножаются на вес — генератор бережёт их первыми. Выключено = все равны.",
            FontSize = 12, Foreground = new SolidColorBrush((Color)FindResource("CTextSoft")),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
        });
        _gradePrioBox = new CheckBox
        {
            Content = "Учитывать приоритет выпускных", IsChecked = st.GradePriorityEnabled,
            Margin = new Thickness(0, 8, 0, 0), FontSize = 13,
        };
        _gradePrioBox.Checked += (_, _) => MarkDirty();
        _gradePrioBox.Unchecked += (_, _) => MarkDirty();
        sp.Children.Add(_gradePrioBox);
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        for (int i = 0; i < 4; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string[] labels = ["Вес 11-х классов:", "Вес 9-х классов:", "Вес остальных:", "Порог тяжести (1..10):"];
        string[] vals = [st.W11.ToString(), st.W9.ToString(), st.WOther.ToString(), st.IsHeavyThreshold.ToString()];
        var boxes = new TextBox[4];
        for (int i = 0; i < 4; i++)
        {
            var lb = new TextBlock
            {
                Text = labels[i], FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetRow(lb, i); Grid.SetColumn(lb, 0);
            grid.Children.Add(lb);
            var tb = new TextBox { Text = vals[i], Margin = new Thickness(0, 3, 0, 3) };
            tb.TextChanged += (_, _) => MarkDirty();
            Grid.SetRow(tb, i); Grid.SetColumn(tb, 1);
            grid.Children.Add(tb);
            boxes[i] = tb;
        }
        _w11Box = boxes[0]; _w9Box = boxes[1]; _wOtherBox = boxes[2]; _heavyBox = boxes[3];
        sp.Children.Add(grid);
        var apply = new Button
        {
            Content = "Применить приоритет", Style = (Style)FindResource("BtnSecondary"),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0),
        };
        apply.Click += OnGradePriorityApplyClick;
        sp.Children.Add(apply);
        card.Child = sp;
        return card;
    }

    private async void OnGradePriorityApplyClick(object sender, RoutedEventArgs e)
    {
        if (_w11Box is null || _w9Box is null || _wOtherBox is null || _heavyBox is null) return;
        if (!int.TryParse(_w11Box.Text, out int w11) || w11 < 1 || w11 > 5 ||
            !int.TryParse(_w9Box.Text, out int w9) || w9 < 1 || w9 > 5 ||
            !int.TryParse(_wOtherBox.Text, out int wo) || wo < 1 || wo > 5 ||
            !int.TryParse(_heavyBox.Text, out int th) || th < 1 || th > 10)
        {
            Say("Веса — числа 1..5, порог тяжести — 1..10.", true);
            return;
        }
        try
        {
            var st = _session.Flex.Settings with
            {
                GradePriorityEnabled = _gradePrioBox?.IsChecked == true,
                W11 = w11, W9 = w9, WOther = wo, IsHeavyThreshold = th,
            };
            await _session.ApplyFlexAsync(_session.Flex with { Settings = st });
            MarkClean();
            Say("Приоритет применён — действует на следующие генерации.", false);
        }
        catch (Exception ex)
        {
            var errs = _session.LastImportErrors;
            Say(errs.Count > 0 ? string.Join("; ", errs.Take(3)) : ex.Message, true);
        }
    }

    // --- B1: глобальные предупреждения об опасных изменениях ---
    private void BuildWarningsPanel()
    {
        WarningsPanel.Children.Clear();
        var card = new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 8) };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = "Предупреждения", FontSize = 15, FontWeight = FontWeights.SemiBold,
        });
        sp.Children.Add(new TextBlock
        {
            Text = "Перед ослаблением строгих правил и другими опасными изменениями программа спрашивает подтверждение. Шаблонная школа так не делает.",
            FontSize = 12, Foreground = new SolidColorBrush((Color)FindResource("CTextSoft")),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
        });
        var box = new CheckBox
        {
            Content = "Показывать предупреждения об опасных изменениях",
            IsChecked = _session.ConfirmDangerous, Margin = new Thickness(0, 8, 0, 0), FontSize = 13,
        };
        box.Checked += async (_, _) => await SetWarningsAsync(true);
        box.Unchecked += async (_, _) => await SetWarningsAsync(false);
        sp.Children.Add(box);
        var back = new Button
        {
            Content = "Вернуть предупреждения", Style = (Style)FindResource("BtnSecondary"),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0),
        };
        back.Click += async (_, _) =>
        {
            await _session.SetConfirmDangerousAsync(true);
            BuildWarningsPanel();
            Say("Предупреждения возвращены — опасные изменения снова спрашивают.", false);
        };
        sp.Children.Add(back);
        card.Child = sp;
        WarningsPanel.Children.Add(card);
    }

    private async Task SetWarningsAsync(bool value)
    {
        await _session.SetConfirmDangerousAsync(value);
        Say(value ? "Предупреждения включены." : "Предупреждения выключены — опасные изменения без спроса.", false);
    }

    private void RegisterRow(string code, Slider s, TextBlock l, TextBlock n, TextBlock r) =>
        _rows[code] = (s, l, n, r);

    private readonly Dictionary<string, (Slider S, TextBlock L, TextBlock N, TextBlock R)> _rows = [];

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is not Slider sl || sl.Tag is not string code) return;
        var o0 = _editor.Options.First(x => x.Code == code);
        // B2: слайдер опасного включён только после подтверждения (тумблер),
        // поэтому здесь confirmed = уже подтверждён. Выключенный слайдер не двигается.
        bool confirmed = !o0.IsStrict || _confirmedDangerous.Contains(code);
        try { _editor.SetLevel(code, (int)sl.Value, confirmed); }
        catch (InvalidOperationException ex) { Say(ex.Message, true); return; }
        if (_rows.TryGetValue(code, out var r))
        {
            var o = _editor.Options.First(x => x.Code == code);
            r.L.Text = o.LevelName;
            r.N.Text = $"({o.Weight})";
            r.R.Text = DangerCaption(o);
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
        ClassesPanel.Children.Add(SectionHint("Дневная норма класса. Начало дня: позже 2-го урока начинать нельзя (строгое по шаблону — ослабляется в «Качестве» через подтверждение)."));
        foreach (var c in data.Classes.OrderBy(x => x.Name))
            AddEntityRow(ClassesPanel, $"{c.Name} (параллель {c.Grade})", $"класс {c.Name}", c.Id,
                c.MaxLessonsPerDay, 1, 10, v => c.MaxLessonsPerDay = v);
        TeachersPanel.Children.Add(SectionHint("Дневной лимит учителя. Пожелания по конкретным дням/часам — следующая версия (зафиксировано в backlog)."));
        BuildOverloadCard();
        foreach (var t in data.Teachers.OrderBy(x => x.Name).Take(200))
            AddEntityRow(TeachersPanel, t.Name, $"учитель {t.Name}", t.Id,
                t.MaxLessonsPerDay, 1, 12, v => t.MaxLessonsPerDay = v);
        if (data.Teachers.Count > 200)
            TeachersPanel.Children.Add(SectionHint($"…и ещё {data.Teachers.Count - 200} (список обрезан для скорости)."));
    }

    // --- A1: перегрузка учителей (движок+персист D-39, проводка в UI) ---
    private CheckBox? _overloadBox;
    private Slider? _overloadCapSlider;
    private TextBlock? _overloadCapText;
    private TextBlock? _overloadStatus;

    private void BuildOverloadCard()
    {
        var st = _session.Flex.Settings;
        var card = new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 8) };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = "Перегрузка учителей", FontSize = 15, FontWeight = FontWeights.SemiBold,
        });
        sp.Children.Add(new TextBlock
        {
            Text = "Если учителям не хватает слотов (нехватка штата), движок поднимет дневной лимит " +
                "ТОЛЬКО тем, чья недельная нагрузка не влезает в норму. Остальных не трогает. " +
                "Действует на следующие генерации.",
            FontSize = 12, Foreground = new SolidColorBrush((Color)FindResource("CTextSoft")),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
        });
        _overloadBox = new CheckBox
        {
            Content = "Разрешить перегрузку", IsChecked = st.AllowTeacherOverload,
            Margin = new Thickness(0, 8, 0, 0), FontSize = 13,
        };
        _overloadBox.Checked += (_, _) => MarkDirty();
        _overloadBox.Unchecked += (_, _) => MarkDirty();
        sp.Children.Add(_overloadBox);
        var capRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        capRow.Children.Add(new TextBlock
        {
            Text = "Потолок, уроков в день:", FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
        });
        _overloadCapSlider = new Slider
        {
            Minimum = 7, Maximum = 14, Value = Math.Clamp(st.TeacherOverloadCap, 7, 14),
            Width = 200, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            IsSnapToTickEnabled = true, TickFrequency = 1,
        };
        _overloadCapSlider.ValueChanged += (_, _) =>
        {
            if (_overloadCapText is not null)
                _overloadCapText.Text = ((int)_overloadCapSlider.Value).ToString();
            MarkDirty();
        };
        capRow.Children.Add(_overloadCapSlider);
        _overloadCapText = new TextBlock
        {
            Text = Math.Clamp(st.TeacherOverloadCap, 7, 14).ToString(), FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
        };
        capRow.Children.Add(_overloadCapText);
        sp.Children.Add(capRow);
        var apply = new Button
        {
            Content = "Применить перегрузку", Style = (Style)FindResource("BtnSecondary"),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0),
        };
        apply.Click += OnOverloadApplyClick;
        sp.Children.Add(apply);
        _overloadStatus = new TextBlock
        {
            Text = OverloadStatusText(), FontSize = 12,
            Foreground = new SolidColorBrush((Color)FindResource("CTextSoft")),
            Margin = new Thickness(0, 6, 0, 0),
        };
        sp.Children.Add(_overloadStatus);
        card.Child = sp;
        TeachersPanel.Children.Add(card);
    }

    private string OverloadStatusText()
    {
        var st = _session.Flex.Settings;
        return st.AllowTeacherOverload
            ? $"Сейчас: перегрузка разрешена, потолок {st.TeacherOverloadCap}/день."
            : "Сейчас: перегрузка запрещена (норма 6/день для всех).";
    }

    private async void OnOverloadApplyClick(object sender, RoutedEventArgs e)
    {
        bool want = _overloadBox?.IsChecked == true;
        int cap = Math.Clamp((int)(_overloadCapSlider?.Value ?? 9), 7, 14);
        var cur = _session.Flex.Settings;
        bool raising = want && (!cur.AllowTeacherOverload || cap > cur.TeacherOverloadCap);
        if (raising)
        {
            bool ok = await _session.ConfirmDangerousAsync(this, "teacher-overload",
                "Перегрузка учителей", DangerousConsequence("teacher-overload") +
                $" Потолок: {cap} уроков в день.");
            if (!ok)
            {
                if (_overloadBox is not null) _overloadBox.IsChecked = cur.AllowTeacherOverload;
                if (_overloadCapSlider is not null) _overloadCapSlider.Value = cur.TeacherOverloadCap;
                Say("Перегрузка не применена.", true);
                return;
            }
        }
        try
        {
            var st = cur with { AllowTeacherOverload = want, TeacherOverloadCap = cap };
            await _session.ApplyFlexAsync(_session.Flex with { Settings = st });
            if (_overloadStatus is not null) _overloadStatus.Text = OverloadStatusText();
            MarkClean();
            Say(want
                ? $"Перегрузка применена (потолок {cap}/день) — действует на следующие генерации."
                : "Перегрузка выключена — все по норме 6/день.", false);
        }
        catch (Exception ex)
        {
            var errs = _session.LastImportErrors;
            Say(errs.Count > 0 ? string.Join("; ", errs.Take(3)) : ex.Message, true);
        }
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
        WarningsPanel.Visibility = i == 5 ? Visibility.Visible : Visibility.Collapsed;
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
            var o0 = _editor.Options.First(x => x.Code == code);
            bool confirmed = !o0.IsStrict || _confirmedDangerous.Contains(code);
            try { _editor.SetWeight(code, w, confirmed); }
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

    // B3: повышение дневных лимитов сверх СанПиН-норм — через подтверждение.
    // Снижение и движение в пределах нормы — молча. Один попап на все строки.
    private async Task<bool> ConfirmCapRaisesAsync()
    {
        var data = _session.Data;
        if (data is null) return true;
        var raises = new List<string>();
        foreach (var (id, box, _, _, what) in _entityBoxes)
        {
            if (!int.TryParse(box.Text, out int v)) continue;
            if (what.StartsWith("учитель ", StringComparison.Ordinal))
            {
                var t = data.Teachers.FirstOrDefault(x => x.Id == id);
                if (t is not null && v > 6 && v > t.MaxLessonsPerDay)
                    raises.Add($"{t.Name}: {t.MaxLessonsPerDay}→{v}/день");
            }
            else if (what.StartsWith("класс ", StringComparison.Ordinal))
            {
                var c = data.Classes.FirstOrDefault(x => x.Id == id);
                if (c is null) continue;
                int norm = c.Grade <= 4 ? 5 : c.Grade <= 6 ? 6 : 7;
                if (v > norm && v > c.MaxLessonsPerDay)
                    raises.Add($"{c.Name}: {c.MaxLessonsPerDay}→{v}/день (норма {norm})");
            }
        }
        if (raises.Count == 0) return true;
        return await _session.ConfirmDangerousAsync(this, "cap-raise", "Повышение дневных лимитов",
            DangerousConsequence("cap-raise") + " Сверх нормы: " +
            string.Join("; ", raises.Take(5)) +
            (raises.Count > 5 ? $" и ещё {raises.Count - 5}." : "."));
    }

    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (!ReadEntityBoxes(out string? err)) { Say(err!, true); return; }
        if (!ReadExpertBoxes()) { Say("В экспертном режиме — только числа из указанных диапазонов.", true); return; }
        if (!await ConfirmCapRaisesAsync()) { Say("Отменено — лимиты не тронуты.", true); return; }
        ApplyEntities();
        // B2: опасные — только с подтверждённым набором (тумблеры выше).
        _session.ApplyOverrides(_editor.GetOverrides(), _confirmedDangerous);
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
            if (!await ConfirmCapRaisesAsync()) { Say("Отменено — лимиты не тронуты.", true); return; }
            ApplyEntities();
            await _session.SaveCustomProfileAsync(name, _baseProfile, _editor.GetOverrides(), _confirmedDangerous);
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

    private async void OnImportJsonClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var ov = JsonSerializer.Deserialize<Dictionary<string, long>>(
                File.ReadAllText(dlg.FileName)) ?? [];
            // B2: файл может содержать опасные — валидация до применения;
            // «требуется подтверждение» → один попап на все опасные файла.
            try
            {
                _ = RuleResolver.Resolve("CUSTOM", ov); // валидация до применения
            }
            catch (InvalidOperationException) when (ov.Keys.Any(RuleCatalog.IsDangerous))
            {
                string first = ov.Keys.First(RuleCatalog.IsDangerous);
                var oh = QualityHints.For(first);
                bool ok = await _session.ConfirmDangerousAsync(
                    this, first, oh.Title,
                    "Файл профиля ослабляет строгие правила. " + DangerousConsequence(first));
                if (!ok) { Say("Импорт отменён — строгие правила не тронуты.", true); return; }
                foreach (var c in ov.Keys.Where(RuleCatalog.IsDangerous))
                    _confirmedDangerous.Add(c);
                _ = RuleResolver.Resolve("CUSTOM", ov, _confirmedDangerous);
            }
            _editor = QualitySettingsEditor.FromRules(EffectiveRuleSet.Default);
            foreach (var (code, w) in ov)
            {
                bool confirmed = _confirmedDangerous.Contains(code);
                _editor.SetWeight(code, w, confirmed);
            }
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
