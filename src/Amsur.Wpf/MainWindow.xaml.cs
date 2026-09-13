using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Amsur.Application;
using Amsur.Scheduling.Core;
using Microsoft.Win32;

namespace Amsur.Wpf;

public partial class MainWindow : Window
{
    private AppSession Session => ((App)System.Windows.Application.Current).Session;
    private bool _suppressModeEvent;

    private static readonly string[] Steps = ["Данные", "Профиль", "Генерация", "Проверка", "Excel"];

    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKey;
        BuildModeCards();
        RefreshAll();
    }

    // P0-1: честные хоткеи (только навигация/запуск, без новых контрактов).
    private void OnPreviewKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F5) { OnGenerateClick(sender, e); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.F1) { OnHelpClick(sender, e); e.Handled = true; }
    }

    private void BuildModeCards()
    {
        ModeCards.Children.Clear();
        var cardStyle = (Style)FindResource("RadioCard");
        foreach (var m in GenerateModes.All)
        {
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(new TextBlock
            {
                Text = m.Name, FontSize = 13, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (m.Code == "STANDARD")
                title.Children.Add(new Border
                {
                    Style = (Style)FindResource("Badge"),
                    Background = (Brush)FindResource("BAccentSoft"),
                    Margin = new Thickness(6, 0, 0, 0),
                    Child = new TextBlock
                    {
                        Text = "Выбор АМСУР", FontSize = 10,
                        Foreground = (Brush)FindResource("BAccent"),
                    },
                });
            var content = new StackPanel { MaxWidth = 280 };
            content.Children.Add(title);
            content.Children.Add(new TextBlock
            {
                Text = ModeHint(m.Code), FontSize = 12,
                Foreground = (Brush)FindResource("BTextSoft"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
                MaxWidth = 280,
            });
            var rb = new RadioButton
            {
                Content = content, GroupName = "GenMode", Tag = m.Code,
                Style = cardStyle, Margin = new Thickness(0, 0, 0, 8),
                ToolTip = m.Description,
            };
            rb.Checked += OnModeChecked;
            ModeCards.Children.Add(rb);
        }
    }

    // P0-1: подпись лимита — честная производная BudgetSeconds (S5: без выдуманных ~20с/~2мин).
    private static string ModeHint(string code)
    {
        var mode = GenerateModes.ByCode(code);
        string limit = mode.BudgetSeconds >= 60
            ? $"лимит ~{mode.BudgetSeconds / 60:0} мин"
            : $"лимит ~{mode.BudgetSeconds:0} сек";
        return (code switch
        {
            "QUICK" => "Проверка / быстрый результат",
            "STANDARD" => "Рекомендуется для большинства школ",
            "MAXIMUM" => "Больше времени на лучший вариант",
            "EXPERT" => "Полный контроль параметров",
            _ => "",
        }) + $" · {limit}";
    }

    private void OnModeChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressModeEvent) return;
        if (sender is RadioButton rb && rb.Tag is string code)
        {
            Session.SetGenerateMode(code);
            if (code == "EXPERT") OnSettingsClick(sender, e);
        }
    }

    // --- Состояния A/B/C/D (§7) ---
    private void RefreshAll()
    {
        SyncModeCards();
        var s = Session.Summary;
        bool hasData = Session.HasData;
        bool noData = !hasData;

        StateNoData.Visibility = noData ? Visibility.Visible : Visibility.Collapsed;
        StateReady.Visibility = Visibility.Collapsed;
        StateDone.Visibility = Visibility.Collapsed;

        SubText.Text = noData
            ? "Загрузите данные школы, чтобы начать."
            : "Данные готовы. Проверьте режим и создавайте расписание.";

        if (s is null)
        {
            DataStateText.Text = "Не загружены";
            DataDetailText.Text = "";
            SchoolCardText.Text = "Данные не загружены";
            SchoolCardDetail.Text = "Шаблон Excel → заполнить → загрузить.";
            MiniDataText.Text = "Не загружены";
        }
        else
        {
            DataStateText.Text = "Загружены и проверены";
            DataDetailText.Text =
                $"{s.Classes} классов · {s.Teachers} учителей · {s.Lessons} уроков в неделю · {s.Days} дн. × {s.Slots} ур.";
            ReadySummaryText.Text = DataDetailText.Text +
                $" · Профиль «{ProfileShort()}» · Режим «{Session.GenerateMode.Name}»";
            SchoolCardText.Text = DataDetailText.Text;
            SchoolCardDetail.Text = $"Профиль «{ProfileShort()}» · Режим «{Session.GenerateMode.Name}»";
            MiniDataText.Text = $"{s.Classes} классов · {s.Teachers} учителей";
        }
        ProfileNameText.Text = ProfileShort();
        ProfileVersionText.Text = $"Каталог v{RuleCatalog.Version}";
        MiniProfileText.Text = ProfileShort();
        HeroGenerateBtn.IsEnabled = hasData;
        GenerateBtn.IsEnabled = hasData;
        BuildStepper(hasData ? 1 : 0);
        RefreshQuality();
        _ = RefreshActiveAsync(hasData);
    }

    private string ProfileShort() =>
        Session.CustomProfileName ?? QualityHints.ProfileName(Session.QualityRules.ProfileName);

    private void BuildStepper(int reached)
    {
        Stepper.Children.Clear();
        for (int i = 0; i < Steps.Length; i++)
        {
            var tb = new TextBlock
            {
                Text = $"{i + 1}. {Steps[i]}",
                FontSize = 12,
                Margin = new Thickness(0, 0, 4, 0),
                Foreground = i <= reached
                    ? (Brush)FindResource("BAccent")
                    : (Brush)FindResource("BTextSoft"),
                FontWeight = i == reached ? FontWeights.Bold : FontWeights.Normal,
                Opacity = i <= reached ? 1 : 0.7,
            };
            Stepper.Children.Add(tb);
            if (i < Steps.Length - 1)
                Stepper.Children.Add(new TextBlock
                {
                    Text = "→", FontSize = 12, Margin = new Thickness(0, 0, 4, 0),
                    Foreground = (Brush)FindResource("BTextSoft"), Opacity = 0.6,
                });
        }
    }

    private async Task RefreshActiveAsync(bool hasData)
    {
        try
        {
            var active = hasData ? await Session.GetActiveAsync() : null;
            if (active is null)
            {
                if (hasData) StateReady.Visibility = Visibility.Visible;
                return;
            }
            StateDone.Visibility = Visibility.Visible;
            DoneSummaryText.Text = $"Версия {active.Number} · {active.Reason}";
            MiniLastText.Text = $"Версия {active.Number}";
            if (Session.LastQuality is null)
            {
                var q = await Session.GetActiveQualityAsync();
                if (q is not null) { Session.LastQuality = q; RefreshQuality(); }
            }
            var lq = Session.LastQuality;
            DoneQualityBadge.Text = lq?.Label ?? "—";
            BuildStepper(4);
        }
        catch { /* dashboard не падает из-за превью */ }
    }

    private void RefreshQuality()
    {
        var q = Session.LastQuality;
        if (q is null)
        {
            QualityLabelText.Text = "—";
            QualityList.ItemsSource = new[] { "Появится после генерации." };
            QualityDetailsBtn.IsEnabled = false;
            return;
        }
        QualityLabelText.Text = q.Label;
        QualityList.ItemsSource = q.Improvements;
        QualityDetailsBtn.IsEnabled = Session.LastQualityLines is { Count: > 0 };
    }

    private void SyncModeCards()
    {
        _suppressModeEvent = true;
        try
        {
            foreach (var child in ModeCards.Children)
                if (child is RadioButton rb && rb.Tag is string code)
                    rb.IsChecked = code == Session.GenerateMode.Code;
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

    private void OnGenerateClick(object sender, RoutedEventArgs e)
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
            await orch.RunAsync(mode.Seeds);
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

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(Session) { Owner = this };
        dlg.ShowDialog();
        RefreshAll();
    }

    private void OnScheduleClick(object sender, RoutedEventArgs e) =>
        new ScheduleWindow(Session).Show();

    // P0-6: экспорт — через отдельное окно (Excel only, PDF «Скоро»).
    private void OnExportClick(object sender, RoutedEventArgs e) =>
        new ExportWindow(Session) { Owner = this }.ShowDialog();

    private void OnDataInfoClick(object sender, RoutedEventArgs e)
    {
        if (!Session.HasData) { ShowErrors(["Данные не загружены."]); return; }
        new SchoolDataWindow(Session) { Owner = this }.ShowDialog();
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
        new QualityDetailsWindow(Session.LastQuality?.Label ?? "Качество", lines) { Owner = this }.ShowDialog();
    }

    private void OnHelpClick(object sender, RoutedEventArgs e) =>
        new HelpWindow { Owner = this }.ShowDialog();

    private static void ShowError(Exception ex) =>
        MessageBox.Show(HumanError(ex.Message), "АМСУР", MessageBoxButton.OK, MessageBoxImage.Warning);
}
