using System.ComponentModel;
using System.Runtime.CompilerServices;
using Amsur.Scheduling.Core;

namespace Amsur.Application;

// E4 §5 — карточки Top-5. Пользователю: Soft, жёсткие нарушения, бейдж, различия.
// ЗАПРЕЩЕНО показывать: proxy, seed, worker count, CP-SAT objective, seq numbers.
public sealed class CandidateCardModel
{
    public int Rank { get; init; }
    public string Title => $"Вариант {Rank}";
    public long SoftTotal { get; init; }
    public string SoftText => $"Soft {SoftTotal}";
    public int HardViolations { get; init; }
    public string HardText => HardViolations == 0 ? "0 жёстких нарушений" : $"{HardViolations} жёстких нарушений";
    public string? Badge { get; init; } // "Лучший" | "Отличается на N%" | null
    public IReadOnlyList<string> DifferenceLines { get; init; } = [];
    // E5 — объяснение качества (soft; hard — отдельной строкой HardText выше).
    public string QualitySummary { get; init; } = "";
    public IReadOnlyList<string> QualityLines { get; init; } = [];
    public bool CanAccept { get; init; } // только лучший
    public ScheduleCandidate Candidate { get; init; } = null!;
}

public sealed class Top5PanelModel
{
    public IReadOnlyList<CandidateCardModel> Cards { get; init; } = [];
    public int TotalFound { get; init; }

    // §9: никаких пустых карточек и фальшивых дубликатов.
    public string HeaderText => TotalFound switch
    {
        0 => "Подходящих вариантов пока нет",
        1 => "Найден 1 подходящий вариант",
        2 or 3 or 4 => $"Найдено {TotalFound} подходящих варианта",
        _ => $"Найдено {TotalFound} подходящих вариантов",
    };

    public CandidateCardModel? Best => Cards.Count == 0 ? null : Cards[0];

    // E5: problem даёт имена (классы/учителя) для QualityLines; без него —
    // совместимость E4 (строки качества пустые, итог — Soft total).
    public static Top5PanelModel FromArchive(
        ScheduleCandidateArchive archive, SchedulingProblem? problem = null)
    {
        var members = archive.Members; // уже отсортированы по SoftTotal
        var cards = new List<CandidateCardModel>();
        var best = members.Count == 0 ? null : members[0];
        int occCount = best?.Placements.Count ?? 0;
        for (int i = 0; i < members.Count; i++)
        {
            var m = members[i];
            string? badge = i == 0 ? "Лучший"
                : $"Отличается на {DiversityExplainer.PercentDifferent(best!, m, occCount)}%";
            var lines = i == 0
                ? new List<string> { "Лучшее найденное расписание" }
                : DiversityExplainer.ExplainLines(best!, m);
            cards.Add(new CandidateCardModel
            {
                Rank = i + 1, SoftTotal = m.SoftTotal, HardViolations = 0,
                Badge = badge, DifferenceLines = lines, CanAccept = i == 0, Candidate = m,
                QualitySummary = problem is null ? $"Soft {m.SoftTotal}" : QualityExplainer.QualitySummary(m),
                // Лучший — «почему такая оценка»; остальные — «чем хуже/лучше лучшего».
                QualityLines = problem is null
                    ? []
                    : i == 0
                        ? QualityExplainer.Explain(problem, m)
                        : QualityExplainer.Compare(problem, best!, m),
            });
        }
        return new Top5PanelModel { Cards = cards, TotalFound = members.Count };
    }
}

// E4 §10 — компактная diagnostics-панель (по умолчанию свернута).
public sealed record GenerationDiagnostics(
    string Seed, string Workers, string PhaseAB,
    string SolverStatus, string Elapsed, string Objective,
    string Validation, string CandidateCounts);

// E4 §4 — Generate screen ViewModel (без зависимости на WPF: INPC в Application,
// тонкий XAML-view в Amsur.Wpf только биндится). Без ложного процента 0→100%.
public sealed class GenerateViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title => "Составление расписания";

    private string _statusText = "Подготовка…";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private string _qualityProfileText = "Профиль: Стандарт";
    public string QualityProfileText { get => _qualityProfileText; set => Set(ref _qualityProfileText, value); }

    private string _firstFeasibleText = "Первое решение: —";
    public string FirstFeasibleText { get => _firstFeasibleText; set => Set(ref _firstFeasibleText, value); }

    private string _bestText = "Лучшее решение: —";
    public string BestText { get => _bestText; set => Set(ref _bestText, value); }

    private string _candidatesText = "Кандидатов: 0 / 5";
    public string CandidatesText { get => _candidatesText; set => Set(ref _candidatesText, value); }

    private string _elapsedText = "Прошло: 0 с";
    public string ElapsedText { get => _elapsedText; set => Set(ref _elapsedText, value); }

    public sealed record StageItem(string Name, bool IsCurrent, bool IsDone);
    private IReadOnlyList<StageItem> _stages =
        new[] { "Подготовка", "Поиск рабочего решения", "Улучшение", "Формирование вариантов" }
        .Select((n, i) => new StageItem(n, i == 0, false)).ToList();
    public IReadOnlyList<StageItem> Stages { get => _stages; private set => Set(ref _stages, value); }

    private Top5PanelModel _top5 = new() { Cards = [], TotalFound = 0 };
    public Top5PanelModel Top5 { get => _top5; set => Set(ref _top5, value); }

    private bool _diagnosticsExpanded; // §10: по умолчанию свернута
    public bool DiagnosticsExpanded { get => _diagnosticsExpanded; set => Set(ref _diagnosticsExpanded, value); }

    private GenerationDiagnostics _diagnostics = new("-", "-", "-", "-", "-", "-", "-", "-");
    public GenerationDiagnostics Diagnostics { get => _diagnostics; set => Set(ref _diagnostics, value); }

    private bool _canStop = true;
    public bool CanStop { get => _canStop; set => Set(ref _canStop, value); }

    public event Action? StopRequested;
    public event Action<CandidateCardModel>? OpenRequested;
    public event Action<CandidateCardModel>? AcceptRequested;

    public void RequestStop() => StopRequested?.Invoke();
    public void RequestOpen(CandidateCardModel card) => OpenRequested?.Invoke(card);
    public void RequestAccept(CandidateCardModel card) { if (card.CanAccept) AcceptRequested?.Invoke(card); }

    public void ApplyProgress(GenerationProgressDto p)
    {
        StatusText = p.Status;
        ElapsedText = $"Прошло: {(int)p.Elapsed.TotalSeconds} с";
        FirstFeasibleText = p.FirstFeasibleAt.HasValue
            ? $"Первое решение: {p.FirstFeasibleAt.Value.TotalSeconds:F1} с" : "Первое решение: —";
        BestText = p.BestSoft.HasValue ? $"Лучшее решение: Soft {p.BestSoft}" : "Лучшее решение: —";
        CandidatesText = $"Кандидатов: {p.ArchiveCount} / {p.ArchiveCapacity}";
        int stageIdx = p.Phase switch
        {
            GenerationPhase.Preparing => 0,
            GenerationPhase.SeekingFeasible => 1,
            GenerationPhase.Improving => 2,
            _ => 3,
        };
        string[] names = ["Подготовка", "Поиск рабочего решения", "Улучшение", "Формирование вариантов"];
        Stages = names.Select((n, i) => new StageItem(n, i == stageIdx, i < stageIdx)).ToList();
        if (p.Phase is GenerationPhase.Done or GenerationPhase.Stopped) CanStop = false;
    }

    private TrajectorySnapshot _trajectory = new([], [], false, false);
    public TrajectorySnapshot Trajectory { get => _trajectory; set => Set(ref _trajectory, value); }

    public void RefreshTrajectory(TrajectorySnapshot snapshot) => Trajectory = snapshot;

    // E7 — результат приёмки (пусто — ещё не принимали).
    private string _acceptResultText = "";
    public string AcceptResultText { get => _acceptResultText; set => Set(ref _acceptResultText, value); }

    private string _activeVersionText = "Активной версии нет";
    public string ActiveVersionText { get => _activeVersionText; set => Set(ref _activeVersionText, value); }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
