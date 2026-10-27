using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Amsur.Application;

namespace Amsur.Wpf.Views;

// P-D3: генерация как view (логика из GenerateWindow 1-в-1 + жизненный цикл).
// Старт — на Loaded (Task.Run, UI свободен), стоп — на Unloaded (было: Closed).
// Финал — Session.LastQuality (дашборд подхватит при навигации).
public partial class GenerateView : UserControl
{
    private GenerateViewModel _vm;
    private GenerationOrchestrator? _orch;
    private readonly AppSession? _session;
    private bool _started;
    private bool _running;
    private readonly Dictionary<string, System.Windows.Controls.Border> _modeCards = [];
    private string? _selectedMode;

    public GenerateView(GenerateViewModel vm, GenerationOrchestrator? orch = null, AppSession? session = null)
    {
        _vm = vm;
        _orch = orch;
        _session = session;
        DataContext = vm;
        InitializeComponent();
        BuildModeStrip();
        RestartButton.IsEnabled = !_vm.CanStop;
        vm.PropertyChanged += OnVmChanged;
        RefreshDiagnostics();
        RefreshDraft();
        Chart.Render(vm.Trajectory);
        Loaded += (_, _) => StartOnce();
        Loaded += (_, _) => ApplyModeHighlight(); // режим мог смениться на другом экране
        Unloaded += (_, _) => _vm.RequestStop();
    }

    private void StartOnce()
    {
        if (_started) return;
        _started = true;
        // XAML-тесты конструируют view без оркестратора — стартовать нечего.
        if (_orch is null || _session is null) return;
        _ = RunGuardedAsync();
    }

    private async Task RunGuardedAsync()
    {
        if (_running) return;
        _running = true;
        _vm.CanStop = true; // перезапуск: «Остановить» снова активна
        // Сюда доходим только из StartOnce при живых orch+session.
        var orch = _orch!;
        var session = _session!;
        try
        {
            // Новый запуск отменяет старый черновик: карточка не должна переживать прогон.
            _vm.HasDraft = false;
            _vm.DraftText = "";
            _vm.DraftExportable = false;
            await Task.Run(() => orch.RunAsync(session.GenerateMode.Seeds));
            await Dispatcher.InvokeAsync(() =>
            {
                var best = _vm.Top5.Best;
                if (best?.Candidate is not null)
                {
                    session.LastQuality = QualityRating.FromBreakdown(best.Candidate.Breakdown);
                    session.LastQualityLines = best.QualityLines.ToList();
                }
            });
            // Черновик-фолбэк: строгий поиск не дал ни одного варианта — не оставляем
            // пользователя ни с чем, показываем лучшее доступное (16.10.2026).
            if (_vm.Top5.Best?.Candidate is null && session.DraftFallbackEnabled)
            {
                int seed = session.GenerateMode.Seeds.Count > 0 ? session.GenerateMode.Seeds[0] : 11;
                await Task.Run(() => orch.BuildDraft(session.BuildDraftOptions(seed)));
            }
        }
        catch (Exception ex)
        {
            _vm.CanStop = false; // расчёт не идёт — «Остановить» не должна висеть активной
            await Dispatcher.InvokeAsync(() => MessageBox.Show(
                "Генерация прервана ошибкой: " + ex.Message,
                "АМСУР", MessageBoxButton.OK, MessageBoxImage.Error));
        }
        finally
        {
            _running = false;
            // Расчёт + черновик закончены — перезапуск снова доступен.
            // Через Dispatcher: finally может выполниться не на UI-потоке.
            _ = Dispatcher.InvokeAsync(() => RestartButton.IsEnabled = !_vm.CanStop);
        }
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GenerateViewModel.Diagnostics))
            Dispatcher.InvokeAsync(RefreshDiagnostics);
        if (e.PropertyName == nameof(GenerateViewModel.CanStop))
            Dispatcher.InvokeAsync(() => RestartButton.IsEnabled = !_vm.CanStop && !_running);
        if (e.PropertyName == nameof(GenerateViewModel.Trajectory))
            Dispatcher.InvokeAsync(() => Chart.Render(_vm.Trajectory));
        if (e.PropertyName is nameof(GenerateViewModel.HasDraft)
            or nameof(GenerateViewModel.DraftText)
            or nameof(GenerateViewModel.DraftExportable))
            Dispatcher.InvokeAsync(RefreshDraft);
    }

    // Черновик: честная карточка «идеал не найден, вот лучшее» + выгрузка.
    // Строится в code-behind (в проекте нет value-конвертеров) — видно только при HasDraft.
    private void RefreshDraft()
    {
        DraftPanel.Children.Clear();
        if (!_vm.HasDraft) return;

        var card = new System.Windows.Controls.Border { Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 0) };
        if (TryFindResource("Card") is Style cardStyle) card.Style = cardStyle;
        var sp = new System.Windows.Controls.StackPanel();

        var head = new System.Windows.Controls.StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Черновик: идеальное расписание не найдено",
            FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        });
        var badge = new System.Windows.Controls.Border { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        if (TryFindResource("Badge") is Style badgeStyle) badge.Style = badgeStyle;
        badge.Background = TryFindResource("BAccentSoft") as Brush;
        badge.Child = new System.Windows.Controls.TextBlock
        {
            Text = "не для приёмки", FontSize = 11,
            Foreground = TryFindResource("BAccent") as Brush,
        };
        head.Children.Add(badge);
        sp.Children.Add(head);

        sp.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Это не принятое расписание: строгий поиск не нашёл полного варианта, " +
                "показано лучшее, что удалось собрать. Нарушения перечислены ниже, " +
                "такое расписание можно только выгрузить и доделать руками.",
            FontSize = 12, Foreground = TryFindResource("BTextSoft") as Brush,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
        });
        sp.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = _vm.DraftText, FontSize = 13, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });

        var btn = new System.Windows.Controls.Button
        {
            Content = "Выгрузить черновик в Excel",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 10, 0, 0),
            IsEnabled = _vm.DraftExportable,
        };
        if (TryFindResource("BtnSecondary") is Style btnStyle) btn.Style = btnStyle;
        btn.Click += OnExportDraftClick;
        sp.Children.Add(btn);
        if (!_vm.DraftExportable)
            sp.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Выгрузка недоступна: в черновике есть нарушения, которые школа не разрешала. " +
                    "Разрешить их можно в «Настройках» → «Если идеал не найден».",
                FontSize = 12, Foreground = TryFindResource("CBad") as Brush,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            });

        card.Child = sp;
        DraftPanel.Children.Add(card);
    }

    private async void OnExportDraftClick(object sender, RoutedEventArgs e)
    {
        if (_orch?.LastProblem is null || _orch.LastDraft is null || _session is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Excel (*.xlsx)|*.xlsx", FileName = "черновик.xlsx",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            await _session.ExportDraftAsync(dlg.FileName, _orch.LastProblem, _orch.LastDraft);
            MessageBox.Show($"Черновик выгружен: {dlg.FileName}", "АМСУР",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Черновик не выгружен: " + ex.Message, "АМСУР",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BuildModeStrip()
    {
        ModeStrip.Children.Clear();
        _modeCards.Clear();
        foreach (var m in GenerateModes.All)
        {
            string limit = m.BudgetSeconds >= 60
                ? $"лимит ~{m.BudgetSeconds / 60:0} мин"
                : $"лимит ~{m.BudgetSeconds:0} сек";
            var card = new System.Windows.Controls.Border
            {
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = m.Description,
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1),
            };
            if (TryFindResource("Card") is Style cardStyle) card.Style = cardStyle;
            var sp = new System.Windows.Controls.StackPanel();
            sp.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = m.Name, FontSize = 12, FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            sp.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = limit, FontSize = 11,
                Foreground = TryFindResource("BTextSoft") as Brush,
                Margin = new Thickness(0, 2, 0, 0),
            });
            card.Child = sp;
            // Клик по карточке — смена режима без возврата на главную.
            card.MouseLeftButtonUp += (_, _) => SelectMode(m.Code);
            _modeCards[m.Code] = card;
            ModeStrip.Children.Add(card);
        }
        ApplyModeHighlight();
    }

    // Выбранный режим: рамка BAccent + мягкий фон (остальные — обычная рамка).
    private void SelectMode(string code)
    {
        _selectedMode = code;
        _session?.SetGenerateMode(code);
        ApplyModeHighlight();
    }

    private void ApplyModeHighlight()
    {
        string? current = _selectedMode ?? _session?.GenerateMode.Code;
        foreach (var (code, card) in _modeCards)
        {
            bool on = code == current;
            card.BorderBrush = (TryFindResource(on ? "BAccent" : "BBorder") as Brush) ?? Brushes.Transparent;
            card.BorderThickness = new Thickness(on ? 2 : 1);
            card.Background = (TryFindResource(on ? "BAccentSoft" : "BCard") as Brush)
                ?? (on ? Brushes.WhiteSmoke : Brushes.Transparent);
        }
    }

    private void RefreshDiagnostics()
    {
        var d = _vm.Diagnostics;
        DiagSeed.Text = $"seed: {d.Seed}";
        DiagWorkers.Text = $"workers: {d.Workers}";
        DiagPhase.Text = $"Phase A/B: {d.PhaseAB}";
        DiagSolver.Text = $"solver status: {d.SolverStatus}";
        DiagElapsed.Text = $"elapsed: {d.Elapsed}";
        DiagObjective.Text = $"objective: {d.Objective}";
        DiagValidation.Text = $"validation: {d.Validation}";
        DiagCounts.Text = $"candidates: {d.CandidateCounts}";
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => _vm.RequestStop();

    // Перезапуск на этом же экране: новый прогон в выбранном режиме.
    // Бюджет сида и grind-lite фиксируются в момент создания оркестратора
    // (GenerateHost.CreateView), поэтому для нового режима берём новый
    // оркестратор на той же сессии и подменяем VM у текущего view.
    private async void OnRestartClick(object sender, RoutedEventArgs e)
    {
        if (_vm.CanStop) return; // идёт расчёт — сначала остановите
        if (_session?.Data is null) return;
        // Расчёт уже остановлен, но черновик ещё дописывается: дождёмся конца.
        for (int i = 0; i < 100 && _running; i++) await Task.Delay(50);
        if (_running) return;
        if (_orch is null)
        {
            await RunGuardedAsync(); // тестовый режим без оркестратора
            return;
        }

        var (discarded, orch) = GenerateHost.CreateView(
            _session.Data.ToProblemInput(), _session, _session.GenerateMode.Name);
        _ = discarded; // нужен только оркестратор нового режима; view не показываем
        var vm = orch.ViewModel;
        _vm.PropertyChanged -= OnVmChanged;
        _orch = orch;
        _vm = vm;
        DataContext = vm;
        vm.PropertyChanged += OnVmChanged;
        Chart.Render(vm.Trajectory);
        RefreshDiagnostics();
        RefreshDraft();
        RestartButton.IsEnabled = !vm.CanStop;
        await RunGuardedAsync();
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b && b.Tag is CandidateCardModel card)
            _vm.RequestOpen(card);
    }

    private void OnAcceptClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b && b.Tag is CandidateCardModel card)
            _vm.RequestAccept(card);
    }
}
