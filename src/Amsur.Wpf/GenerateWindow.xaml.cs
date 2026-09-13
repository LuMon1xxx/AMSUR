using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Amsur.Application;

namespace Amsur.Wpf;

// Тонкий view (§11): никакой логики, только биндинг к Application-VM.
// Открыть → Editor (подключается вызывающим через OpenRequested),
// Принять → только лучший (CanAccept), через AcceptRequested.
public partial class GenerateWindow : Window
{
    private readonly GenerateViewModel _vm;

    public GenerateWindow(GenerateViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        BuildModeStrip();
        vm.PropertyChanged += OnVmChanged;
        RefreshDiagnostics();
        Chart.Render(vm.Trajectory);
    }

    // E11: события потока приходят из solver-потока — прямые касания UI
    // маршалим в UI-поток (биндинги CLR-свойств WPF маршалит сам).
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GenerateViewModel.Diagnostics))
            Dispatcher.InvokeAsync(RefreshDiagnostics);
        if (e.PropertyName == nameof(GenerateViewModel.Trajectory))
            Dispatcher.InvokeAsync(() => Chart.Render(_vm.Trajectory));
    }

    // P0-4: режимы — только справка по честным лимитам (BudgetSeconds).
    // Выбор режима — на главной до запуска; здесь ничего не переключаем.
    // TryFindResource: окно обязано конструироваться и без ресурсов App (STA-тест).
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
