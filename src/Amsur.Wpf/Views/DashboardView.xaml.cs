using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Amsur.Application;
using Amsur.Scheduling.Core;
using Microsoft.Win32;

namespace Amsur.Wpf.Views;

// P-D0: дашборд как View внутри оболочки MainWindow (контент переехал 1-в-1).
// Навигация — через Navigator (оболочка); RefreshChrome — колбэк оболочки
// (бейдж школы + футер остались в MainWindow).
public partial class DashboardView : UserControl
{
    private AppSession Session => ((App)System.Windows.Application.Current).Session;
    private bool _suppressModeEvent;
    private readonly Dictionary<string, Ellipse> _modeDots = [];

    public IViewNavigator? Navigator { get; set; }
    public Action? ChromeRefresh { get; set; }

    private static string Glyph(int code) => new((char)code, 1);

    private static readonly Dictionary<string, string> ModeIcon = new()
    {
        // C#-строки НЕ парсят XML-сущности вида &#xE768; — глифы задаём кодом
        // Segoe MDL2 Assets, иначе рисуются tofu-квадраты (баг найден на D0-скрине).
        ["QUICK"] = Glyph(0xE768),
        ["STANDARD"] = Glyph(0xE735),
        ["MAXIMUM"] = Glyph(0xE8A5),
        // Топ-качество было без меты — карточка рисовалась пустой (баг D-скрина).
        ["TOP"] = Glyph(0xE734),
        ["EXPERT"] = Glyph(0xE713),
    };

    public DashboardView()
    {
        InitializeComponent();
        BuildModeCards();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        RefreshAll();
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
        if (System.Windows.Application.Current is App app)
            app.ThemeChanged -= OnAppThemeChanged;
    }

    private void OnAppThemeChanged(string _)
    {
        RefreshQuality();
    }

    private static void Bind(FrameworkElement el, DependencyProperty prop, string key)
    {
        if (el.TryFindResource(key) is not null)
            el.SetResourceReference(prop, key);
    }

    private void BuildModeCards()
    {
        ModeCards.Children.Clear();
        _modeDots.Clear();
        var cardStyle = (Style)FindResource("RadioCard");
        foreach (var m in GenerateModes.All)
        {
            string iconCode = ModeIcon.TryGetValue(m.Code, out var ic) ? ic : Glyph(0xE713);
            var ui = UiFormat.ModeDisplay(m.Code);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new Ellipse
            {
                Width = 16, Height = 16, StrokeThickness = 2,
                Fill = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Bind(dot, Shape.StrokeProperty, "BTextMuted");
            Grid.SetColumn(dot, 0);
            grid.Children.Add(dot);
            _modeDots[m.Code] = dot;

            var iconGlyph = new TextBlock
            {
                Text = iconCode, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            Bind(iconGlyph, TextBlock.ForegroundProperty, "BAccentText");

            var icon = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15),
                Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = iconGlyph,
            };
            Bind(icon, Border.BackgroundProperty, "BAccentSoft");
            Grid.SetColumn(icon, 1);
            grid.Children.Add(icon);

            var texts = new StackPanel { Margin = new Thickness(9, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            var titleRow = new WrapPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(new TextBlock
            {
                Text = ui.Title,
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (ui.IsRecommended)
            {
                var recText = new TextBlock { Text = "Рекомендуем", FontSize = 10, FontWeight = FontWeights.SemiBold };
                Bind(recText, TextBlock.ForegroundProperty, "BAccentForeground");
                var recBadge = new Border
                {
                    Style = TryFindResource("Badge") as Style,
                    Margin = new Thickness(6, 1, 0, 1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = recText,
                };
                Bind(recBadge, Border.BackgroundProperty, "BAccent");
                titleRow.Children.Add(recBadge);
            }
            texts.Children.Add(titleRow);
            var hintBlock = new TextBlock
            {
                Text = ui.Hint, FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0),
            };
            Bind(hintBlock, TextBlock.ForegroundProperty, "BTextSoft");
            texts.Children.Add(hintBlock);
            Grid.SetColumn(texts, 2);
            grid.Children.Add(texts);

            var timeBlock = new TextBlock
            {
                Text = ui.TimeLabel, FontSize = 11,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, 2, 0, 0),
            };
            Bind(timeBlock, TextBlock.ForegroundProperty, "BTextMuted");
            grid.Children.Add(timeBlock);
            Grid.SetColumn(timeBlock, 3);

            var rb = new RadioButton
            {
                Content = grid, GroupName = "GenMode", Tag = m.Code,
                Style = cardStyle, Padding = new Thickness(11, 7, 11, 7), Margin = new Thickness(0, 0, 0, 5),
                ToolTip = m.Description,
            };
            rb.Checked += OnModeChecked;
            ModeCards.Children.Add(rb);
        }
    }

    private void OnModeChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressModeEvent) return;
        if (sender is RadioButton rb && rb.Tag is string code)
        {
            Session.SetGenerateMode(code);
            SyncModeCards();
            if (code == "EXPERT") OnSettingsClick(sender, e);
        }
    }

    public void RefreshAll()
    {
        if (!Session.IsReady)
        {
            SubText.Text = "Загрузка данных…";
            return;
        }
        SyncModeCards();
        var s = Session.Summary;
        bool hasData = Session.HasData;

        // Дублирующий баннер «следующий шаг» нужен только до загрузки данных.
        NextStepCard.Visibility = hasData ? Visibility.Collapsed : Visibility.Visible;

        SubText.Text = hasData
            ? "Данные готовы. Проверьте режим и создавайте расписание."
            : "Загрузите данные школы, чтобы начать.";

        if (s is null)
        {
            SchoolCardText.Text = "Данные не загружены";
            SchoolCardDetail.Text = "Шаблон Excel → заполнить → загрузить.";
            DataDetailText.Text = "";
            MiniDataStatus.Text = "Не загружены";
            Bind(MiniDataStatus, TextBlock.ForegroundProperty, "BTextMuted");
            Bind(MiniDataDot, Shape.FillProperty, "BTextMuted");
        }
        else
        {
            string summary = $"{s.Classes} классов · {s.Teachers} учителей";
            SchoolCardText.Text = summary;
            SchoolCardDetail.Text = $"{s.Lessons} уроков в неделю";
            DataDetailText.Text = $"{s.Days} дн. × {s.Slots} ур. · " +
                (Session.DataSource == "manual" ? "Ручной ввод"
                    : Session.DataSource == "demo" ? "Демо-данные" : "Excel-импорт");
            MiniDataStatus.Text = "Загружены";
            Bind(MiniDataStatus, TextBlock.ForegroundProperty, "BGood");
            Bind(MiniDataDot, Shape.FillProperty, "BGood");
        }
        ProfileNameText.Text = ProfileShort();
        ProfileVersionText.Text = Session.CustomProfileName is not null
            ? "Наш профиль"
            : Session.QualityRules.ProfileName == "STANDARD" ? "Рекомендуемый" : "Пресет";
        MiniProfileText.Text = ProfileShort();
        HeroGenerateBtn.IsEnabled = hasData;
        RefreshStepsStage1(hasData);
        _ = RefreshStepsStage2Async(hasData);
        RefreshQuality();
        ChromeRefresh?.Invoke();
        _ = RefreshActiveAsync(hasData);
    }

    private void RefreshStepsStage1(bool hasData)
    {
        PaintStep(Step1Circle, Step1Num, hasData ? StepState.Done : StepState.Current);
        PaintStep(Step2Circle, Step2Num, hasData ? StepState.Done : StepState.Todo);
    }

    private async Task RefreshStepsStage2Async(bool hasData)
    {
        bool hasActive = false, hasQuality = false;
        try
        {
            var active = hasData ? await Session.GetActiveAsync() : null;
            hasActive = active is not null;
            hasQuality = hasActive && Session.LastQuality is not null;
        }
        catch { }
        PaintStep(Step3Circle, Step3Num,
            hasActive ? StepState.Done : hasData ? StepState.Current : StepState.Todo);
        PaintStep(Step4Circle, Step4Num,
            hasQuality ? StepState.Done : hasActive ? StepState.Current : StepState.Todo);
        PaintStep(Step5Circle, Step5Num, hasActive ? StepState.Current : StepState.Todo);
        PaintNextStep(hasData, hasActive, hasQuality);
    }

    // P-D1: умный следующий шаг — одно действие вместо пяти.
    private enum NextAction { Import, Generate, Schedule, Export }
    private NextAction _next = NextAction.Import;

    private void PaintNextStep(bool hasData, bool hasActive, bool hasQuality)
    {
        // Верхний баннер нужен только на шаге 1 (пока данных нет). После импорта
        // единственная кнопка запуска — HeroGenerateBtn, дублирующий баннер скрыт.
        NextStepCard.Visibility = hasData ? Visibility.Collapsed : Visibility.Visible;
        // Ввод сетки (дни/уроки) нужен только на шаге загрузки.
        NextStepInputs.Visibility = !hasData ? Visibility.Visible : Visibility.Collapsed;
        DemoBtn.Visibility = !hasData ? Visibility.Visible : Visibility.Collapsed;
        if (!hasData)
        {
            _next = NextAction.Import;
            NextStepTitle.Text = "Загрузите данные школы";
            NextStepText.Text = "Шаблон Excel → заполнить → загрузить. Это займёт несколько минут.";
            NextStepBtn.Content = "Загрузить Excel";
        }
        else if (!hasActive)
        {
            _next = NextAction.Generate;
            NextStepTitle.Text = "Всё готово — создавайте расписание";
            NextStepText.Text = "Проверьте режим справа и нажмите кнопку. Расчёт выполнится автоматически.";
            NextStepBtn.Content = "Сгенерировать";
        }
        else if (!hasQuality)
        {
            _next = NextAction.Schedule;
            NextStepTitle.Text = "Проверьте результат";
            NextStepText.Text = "Откройте сетку: дни, уроки, учителя и кабинеты.";
            NextStepBtn.Content = "Перейти к сетке";
        }
        else
        {
            _next = NextAction.Export;
            NextStepTitle.Text = "Расписание готово — можно выгружать";
            NextStepText.Text = "Заберите Excel для школы или посмотрите сетку ещё раз.";
            NextStepBtn.Content = "Выгрузить";
        }
    }

    private void OnNextStepClick(object sender, RoutedEventArgs e)
    {
        switch (_next)
        {
            case NextAction.Import: OnImportClick(sender, e); break;
            case NextAction.Generate: OnGenerateClick(sender, e); break;
            case NextAction.Schedule: OnScheduleClick(sender, e); break;
            case NextAction.Export: OnExportClick(sender, e); break;
        }
    }

    private enum StepState { Todo, Current, Done }

    private void PaintStep(System.Windows.Controls.Border circle,
        System.Windows.Controls.TextBlock num, StepState state)
    {
        switch (state)
        {
            case StepState.Done:
                Bind(circle, Border.BackgroundProperty, "BAccent");
                Bind(num, TextBlock.ForegroundProperty, "BAccentForeground");
                num.Text = "✓";
                break;
            case StepState.Current:
                Bind(circle, Border.BackgroundProperty, "BAccent");
                Bind(num, TextBlock.ForegroundProperty, "BAccentForeground");
                if (num.Text == "✓") num.Text = (string)num.Tag;
                break;
            default:
                Bind(circle, Border.BackgroundProperty, "BSubtle");
                Bind(num, TextBlock.ForegroundProperty, "BTextSoft");
                if (num.Text == "✓") num.Text = (string)num.Tag;
                break;
        }
    }

    private string ProfileShort() =>
        Session.CustomProfileName ?? QualityHints.ProfileName(Session.QualityRules.ProfileName);

    private async Task RefreshActiveAsync(bool hasData)
    {
        try
        {
            var active = hasData ? await Session.GetActiveAsync() : null;
            if (active is null)
            {
                MiniLastStatus.Text = "Не создано";
                Bind(MiniLastStatus, TextBlock.ForegroundProperty, "BTextMuted");
                Bind(MiniLastDot, Shape.FillProperty, "BTextMuted");
                MiniLastText.Text = "Пока нет";
                return;
            }
            MiniLastStatus.Text = $"Версия {active.Number}";
            Bind(MiniLastStatus, TextBlock.ForegroundProperty, "BGood");
            Bind(MiniLastDot, Shape.FillProperty, "BGood");
            MiniLastText.Text = active.Reason;
            if (Session.LastQuality is null)
            {
                var q = await Session.GetActiveQualityAsync();
                if (q is not null) { Session.LastQuality = q; RefreshQuality(); }
            }
            ChromeRefresh?.Invoke();
        }
        catch { }
    }

    private sealed record QualityRow(string Name, string Count, Brush Dot)
    {
        public override string ToString() => $"{Name} {Count}".Trim();
    }

    private void RefreshQuality()
    {
        var q = Session.LastQuality;
        if (q is null)
        {
            QualityLabelText.Text = "—";
            Bind(QualityLabelText, TextBlock.ForegroundProperty, "BTextMuted");
            QualityBar.Value = 0;
            QualityList.ItemsSource = new[]
            {
                new QualityRow("Появится после генерации.", "", Brushes.Transparent),
            };
            QualityDetailsBtn.IsEnabled = false;
            return;
        }
        QualityLabelText.Text = q.Label;
        var palette = new[] { "BWarn", "BAccentText", "BTextSoft" };
        string levelKey = q.Level switch
        {
            0 => "BGood",
            1 => "BWarn",
            _ => "BBad",
        };
        Bind(QualityLabelText, TextBlock.ForegroundProperty, levelKey);
        QualityBar.Value = q.Level switch { 0 => 1.0, 1 => 0.66, _ => 0.33 };
        Bind(QualityBar, Control.ForegroundProperty, levelKey);
        var rows = new List<QualityRow>();
        for (int i = 0; i < q.Improvements.Count; i++)
        {
            var parts = q.Improvements[i].Split(": ");
            var dotBrush = TryFindResource(palette[i % palette.Length]) as Brush ?? Brushes.Gray;
            rows.Add(new QualityRow(
                parts[0],
                parts.Length > 1 ? parts[^1] : "",
                dotBrush));
        }
        QualityList.ItemsSource = rows;
        QualityDetailsBtn.IsEnabled = Session.LastQualityLines is { Count: > 0 };
    }

    private void SyncModeCards()
    {
        _suppressModeEvent = true;
        try
        {
            foreach (var child in ModeCards.Children)
            {
                if (child is not RadioButton rb || rb.Tag is not string code) continue;
                bool on = code == Session.GenerateMode.Code;
                rb.IsChecked = on;
                if (_modeDots.TryGetValue(code, out var dot))
                {
                    if (on)
                        Bind(dot, Shape.FillProperty, "BAccent");
                    else
                        dot.Fill = Brushes.Transparent;
                    Bind(dot, Shape.StrokeProperty, on ? "BAccent" : "BTextMuted");
                }
            }
        }
        finally { _suppressModeEvent = false; }
    }

    private void ShowErrors(IReadOnlyList<string> errors)
    {
        if (errors.Count == 0) { ErrorCard.Visibility = Visibility.Collapsed; return; }
        ErrorCard.Visibility = Visibility.Visible;
        ErrorList.ItemsSource = errors.Take(8).ToList();
    }

    private void OnTemplateClick(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", FileName = "нагрузка.xlsx" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            using var fs = File.Create(dlg.FileName);
            SchoolDataImporter.ExportTemplate(fs);
            ShowErrors([]);
        }
        catch (Exception ex) { ShowError(ex); }
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
            await Session.ImportLoadAsync(rows, days, slots);
            ShowErrors([]);
            RefreshAll();
        }
        catch (Exception ex)
        {
            var errs = Session.LastImportErrors;
            ShowErrors(errs.Count > 0
                ? HumanImportErrors(errs)
                : new[] { HumanError(ex.Message) });
            RefreshAll();
        }
    }

    private static IReadOnlyList<string> HumanImportErrors(IReadOnlyList<string> raw) =>
        raw.Take(8).Select(HumanError).ToList();

    private static string HumanError(string raw)
    {
        if (raw.Contains("empty domain", StringComparison.OrdinalIgnoreCase))
            return "Учителю некуда поставить уроки: проверьте дни, смены и недоступность.";
        if (raw.Contains("no allowed placements", StringComparison.OrdinalIgnoreCase))
            return "Для занятия нет подходящего времени: дни/смены/недоступность конфликтуют.";
        if (raw.Contains("unknown", StringComparison.OrdinalIgnoreCase))
            return "В нагрузке есть ссылка на неизвестный класс, предмет или учителя: " + raw;
        return raw;
    }

    private async void OnDemoClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await Session.ImportDemoAsync();
            ShowErrors([]);
            RefreshAll();
        }
        catch (Exception ex)
        {
            ShowErrors([HumanError(ex.Message)]);
            RefreshAll();
        }
    }

    public void OnGenerateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Session.HasData)
            {
                ShowErrors(["Сначала загрузите данные школы — кнопка «Загрузить Excel» выше."]);
                return;
            }
            // P-D3: генерация — внутри оболочки (view + оркестратор), без popup.
            Navigator?.OpenGenerate(Session.Data!.ToProblemInput(true));
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void OnAdviceClose(object sender, RoutedEventArgs e) =>
        AdviceCard.Visibility = Visibility.Collapsed;

    private void OnSettingsClick(object sender, RoutedEventArgs e) =>
        Navigator?.NavigateTo("Settings");

    private void OnScheduleClick(object sender, RoutedEventArgs e) =>
        Navigator?.NavigateTo("Schedule");

    private void OnExportClick(object sender, RoutedEventArgs e) =>
        Navigator?.NavigateTo("Export");

    private void OnDataInfoClick(object sender, RoutedEventArgs e)
    {
        if (!Session.HasData) { ShowErrors(["Данные не загружены."]); return; }
        Navigator?.NavigateTo("Data");
    }

    private void OnQualityDetailsClick(object sender, RoutedEventArgs e)
    {
        var lines = Session.LastQualityLines;
        if (lines is null || lines.Count == 0)
        {
            MessageBox.Show("Пока нет оценки — сгенерируйте расписание.",
                "АМСУР", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new QualityDetailsWindow(Session.LastQuality?.Label ?? "Качество", lines)
        {
            Owner = System.Windows.Window.GetWindow(this)
        }.ShowDialog();
    }

    private void OnHelpClick(object sender, RoutedEventArgs e) =>
        Navigator?.NavigateTo("Help");

    private static void ShowError(Exception ex) =>
        MessageBox.Show(HumanError(ex.Message), "АМСУР", MessageBoxButton.OK, MessageBoxImage.Warning);
}
