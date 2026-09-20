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

    private static readonly Dictionary<string, (string Icon, string Time)> ModeMeta = new()
    {
        // C#-строки НЕ парсят XML-сущности вида &#xE768; — глифы задаём кодом
        // Segoe MDL2 Assets, иначе рисуются tofu-квадраты (баг найден на D0-скрине).
        ["QUICK"] = (Glyph(0xE768), "~3 сек"),
        ["STANDARD"] = (Glyph(0xE735), "~40 сек"),
        ["MAXIMUM"] = (Glyph(0xE8A5), "~3 мин"),
        ["EXPERT"] = (Glyph(0xE713), "Ручной"),
    };

    public DashboardView()
    {
        InitializeComponent();
        BuildModeCards();
        RefreshAll();
    }

    private void BuildModeCards()
    {
        ModeCards.Children.Clear();
        _modeDots.Clear();
        var cardStyle = (Style)FindResource("RadioCard");
        foreach (var m in GenerateModes.All)
        {
            var meta = ModeMeta.TryGetValue(m.Code, out var mm) ? mm : (Icon: Glyph(0xE713), Time: "");
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new Ellipse
            {
                Width = 16, Height = 16, StrokeThickness = 2,
                Stroke = (Brush)FindResource("BTextMuted"), Fill = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(dot, 0);
            grid.Children.Add(dot);
            _modeDots[m.Code] = dot;

            var icon = new Border
            {
                Width = 36, Height = 36, CornerRadius = new CornerRadius(18),
                Background = (Brush)FindResource("BAccentSoft"),
                Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = meta.Icon, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16,
                    Foreground = (Brush)FindResource("BAccent"),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            Grid.SetColumn(icon, 1);
            grid.Children.Add(icon);

            var texts = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(new TextBlock
            {
                Text = m.Name, FontSize = 13, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (m.Code == "STANDARD")
                titleRow.Children.Add(new Border
                {
                    Style = (Style)FindResource("Badge"),
                    Background = (Brush)FindResource("BAccent"),
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = "Рекомендуем", FontSize = 10, Foreground = Brushes.White,
                    },
                });
            texts.Children.Add(titleRow);
            texts.Children.Add(new TextBlock
            {
                Text = ModeHint(m.Code), FontSize = 12,
                Foreground = (Brush)FindResource("BTextSoft"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
            });
            Grid.SetColumn(texts, 2);
            grid.Children.Add(texts);

            grid.Children.Add(new TextBlock
            {
                Text = meta.Time, FontSize = 11,
                Foreground = (Brush)FindResource("BTextMuted"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            Grid.SetColumn(grid.Children[^1], 3);

            var rb = new RadioButton
            {
                Content = grid, GroupName = "GenMode", Tag = m.Code,
                Style = cardStyle, Margin = new Thickness(0, 0, 0, 8),
                ToolTip = m.Description,
            };
            rb.Checked += OnModeChecked;
            ModeCards.Children.Add(rb);
        }
    }

    private static string ModeHint(string code) => code switch
    {
        "QUICK" => "Проверка / быстрый результат",
        "STANDARD" => "Рекомендуется для большинства школ",
        "MAXIMUM" => "Больше времени на лучший вариант",
        "EXPERT" => "Полный контроль параметров",
        _ => "",
    };

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

        SubText.Text = hasData
            ? "Данные готовы. Проверьте режим и создавайте расписание."
            : "Загрузите данные школы, чтобы начать.";

        if (s is null)
        {
            SchoolCardText.Text = "Данные не загружены";
            SchoolCardDetail.Text = "Шаблон Excel → заполнить → загрузить.";
            DataDetailText.Text = "";
            MiniDataText.Text = "Шаблон Excel → заполнить → загрузить.";
            MiniDataStatus.Text = "Не загружены";
            MiniDataStatus.Foreground = (Brush)FindResource("BTextMuted");
            MiniDataDot.Fill = (Brush)FindResource("BTextMuted");
        }
        else
        {
            string summary = $"{s.Classes} классов · {s.Teachers} учителей";
            SchoolCardText.Text = summary;
            SchoolCardDetail.Text = $"{s.Lessons} уроков в неделю";
            DataDetailText.Text = $"{s.Days} дн. × {s.Slots} ур. · " +
                (Session.DataSource == "manual" ? "Ручной ввод" : "Excel-импорт");
            MiniDataText.Text = summary;
            MiniDataStatus.Text = "Загружены";
            MiniDataStatus.Foreground = (Brush)FindResource("BGood");
            MiniDataDot.Fill = (Brush)FindResource("BGood");
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
        // Ввод сетки (дни/уроки) нужен только на шаге загрузки.
        NextStepInputs.Visibility = !hasData ? Visibility.Visible : Visibility.Collapsed;
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
            NextStepText.Text = "Проверьте режим справа и нажмите кнопку. Гуфо всё посчитает сам.";
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
                circle.Background = (Brush)FindResource("BAccent");
                num.Foreground = System.Windows.Media.Brushes.White;
                num.Text = "✓";
                break;
            case StepState.Current:
                circle.Background = (Brush)FindResource("BAccent");
                num.Foreground = System.Windows.Media.Brushes.White;
                if (num.Text == "✓") num.Text = (string)num.Tag;
                break;
            default:
                circle.Background = (Brush)FindResource("BSubtle");
                num.Foreground = (Brush)FindResource("BTextSoft");
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
                MiniLastStatus.Foreground = (Brush)FindResource("BTextMuted");
                MiniLastDot.Fill = (Brush)FindResource("BTextMuted");
                MiniLastText.Text = "Пока нет";
                return;
            }
            MiniLastStatus.Text = $"Версия {active.Number}";
            MiniLastStatus.Foreground = (Brush)FindResource("BGood");
            MiniLastDot.Fill = (Brush)FindResource("BGood");
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

    private sealed record QualityRow(string Name, string Count, Brush Dot);

    private void RefreshQuality()
    {
        var q = Session.LastQuality;
        if (q is null)
        {
            QualityLabelText.Text = "—";
            QualityLabelText.Foreground = (Brush)FindResource("BTextMuted");
            QualityBar.Value = 0;
            QualityList.ItemsSource = new[]
            {
                new QualityRow("Появится после генерации.", "", Brushes.Transparent),
            };
            QualityDetailsBtn.IsEnabled = false;
            return;
        }
        QualityLabelText.Text = q.Label;
        var palette = new[] { "BWarn", "BGood", "BAccent" };
        QualityLabelText.Foreground = (Brush)FindResource(q.Level switch
        {
            0 => "BGood",
            1 => "BWarn",
            _ => "BBad",
        });
        QualityBar.Value = q.Level switch { 0 => 1.0, 1 => 0.66, _ => 0.33 };
        QualityBar.Foreground = QualityLabelText.Foreground;
        var rows = new List<QualityRow>();
        for (int i = 0; i < q.Improvements.Count; i++)
        {
            var parts = q.Improvements[i].Split(": ");
            rows.Add(new QualityRow(
                parts[0],
                parts.Length > 1 ? parts[^1] : "",
                (Brush)FindResource(palette[i % palette.Length])));
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
                    dot.Fill = on ? (Brush)FindResource("BAccent") : Brushes.Transparent;
                    dot.Stroke = (Brush)FindResource(on ? "BAccent" : "BTextMuted");
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

    public void OnGenerateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Session.HasData)
            {
                ShowErrors(["Сначала загрузите данные школы — кнопка «Загрузить Excel» выше."]);
                return;
            }
            var mode = Session.GenerateMode;
            var input = Session.Data!.ToProblemInput();
            var (win, orch) = GenerateHost.Create(input,
                perSeedBudgetSeconds: mode.BudgetSeconds, numWorkers: 1,
                dbPath: Session.DbPath, rules: Session.QualityRules,
                modeName: mode.Name);
            win.Show();
            win.Closed += (_, _) => orch.ViewModel.RequestStop();
            _ = RunGuardedAsync(orch);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task RunGuardedAsync(GenerationOrchestrator orch)
    {
        try
        {
            var mode = Session.GenerateMode;
            await Task.Run(() => orch.RunAsync(mode.Seeds));
            var best = orch.ViewModel.Top5.Best;
            if (best?.Candidate is not null)
            {
                Session.LastQuality = QualityRating.FromBreakdown(best.Candidate.Breakdown);
                Session.LastQualityLines = best.QualityLines.ToList();
            }
            RefreshAll();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Генерация прервана ошибкой: " + ex.Message,
                "АМСУР", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
