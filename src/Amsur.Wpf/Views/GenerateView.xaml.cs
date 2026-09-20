using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Amsur.Application;

namespace Amsur.Wpf.Views;

// P-D3: генерация как view (логика из GenerateWindow 1-в-1 + жизненный цикл).
// Старт — на Loaded (Task.Run, UI свободен), стоп — на Unloaded (было: Closed).
// Финал — Session.LastQuality (дашборд подхватит при навигации).
public partial class GenerateView : UserControl
{
    private readonly GenerateViewModel _vm;
    private readonly GenerationOrchestrator? _orch;
    private readonly AppSession? _session;
    private bool _started;

    public GenerateView(GenerateViewModel vm, GenerationOrchestrator? orch = null, AppSession? session = null)
    {
        _vm = vm;
        _orch = orch;
        _session = session;
        DataContext = vm;
        InitializeComponent();
        BuildModeStrip();
        vm.PropertyChanged += OnVmChanged;
        RefreshDiagnostics();
        Chart.Render(vm.Trajectory);
        Loaded += (_, _) => StartOnce();
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
        // Сюда доходим только из StartOnce при живых orch+session.
        var orch = _orch!;
        var session = _session!;
        try
        {
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
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => MessageBox.Show(
                "Генерация прервана ошибкой: " + ex.Message,
                "АМСУР", MessageBoxButton.OK, MessageBoxImage.Error));
        }
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GenerateViewModel.Diagnostics))
            Dispatcher.InvokeAsync(RefreshDiagnostics);
        if (e.PropertyName == nameof(GenerateViewModel.Trajectory))
            Dispatcher.InvokeAsync(() => Chart.Render(_vm.Trajectory));
    }

    private void BuildModeStrip()
    {
        ModeStrip.Children.Clear();
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
            };
            if (TryFindResource("Card") is Style cardStyle) card.Style = cardStyle;
            var sp = new System.Windows.Controls.StackPanel();
            sp.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = m.Name, FontSize = 12, FontWeight = FontWeights.SemiBold,
            });
            sp.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = limit, FontSize = 11,
                Foreground = TryFindResource("BTextSoft") as Brush,
                Margin = new Thickness(0, 2, 0, 0),
            });
            card.Child = sp;
            ModeStrip.Children.Add(card);
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
