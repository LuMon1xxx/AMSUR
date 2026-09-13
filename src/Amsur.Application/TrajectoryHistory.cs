namespace Amsur.Application;

// E6 — история процесса для графика «лучшая оценка → время» поверх E4.
// ТОЛЬКО реально полученные события IIncumbentStream: ничего не достраивает,
// не интерполирует, проценты не выдумывает. bestSoft==null (до first feasible) —
// точка без значения (разрыв линии, не ноль). Пусто → честное пустое состояние.

public sealed record TrajectoryPoint(
    TimeSpan Elapsed,
    long? BestSoft,
    GenerationPhase Phase,
    string RunKey,
    bool IsTerminal);

public sealed record TrajectoryMarker(
    TimeSpan Elapsed,
    long? BestSoft,
    string Label);

public sealed record TrajectorySnapshot(
    IReadOnlyList<TrajectoryPoint> Points, // строго по времени (stable sort)
    IReadOnlyList<TrajectoryMarker> Markers,
    bool HasData,
    bool HasLine); // >=2 точек со значением — линию есть из чего строить

public sealed class TrajectoryHistory
{
    private readonly object _gate = new();
    private readonly List<TrajectoryPoint> _points = [];

    public int Count { get { lock (_gate) return _points.Count; } }

    /// <summary>Потокобезопасно. Вызывать из подписки на IIncumbentStream.</summary>
    public void Append(GenerationProgressDto dto)
    {
        lock (_gate)
            _points.Add(new TrajectoryPoint(dto.Elapsed, dto.BestSoft, dto.Phase,
                dto.SeedOrRun, dto.Phase is GenerationPhase.Done or GenerationPhase.Stopped));
    }

    public static string PhaseLabel(GenerationPhase phase) => phase switch
    {
        GenerationPhase.Preparing => "Подготовка",
        GenerationPhase.SeekingFeasible => "Поиск рабочего решения",
        GenerationPhase.Improving => "Улучшение",
        GenerationPhase.FormingVariants => "Формирование вариантов",
        GenerationPhase.Done => "Готово",
        GenerationPhase.Stopped => "Остановлено",
        _ => "—",
    };

    public TrajectorySnapshot Snapshot()
    {
        List<TrajectoryPoint> ordered;
        lock (_gate)
            ordered = _points.OrderBy(p => p.Elapsed).ToList(); // stable: равные — в порядке получения

        var markers = new List<TrajectoryMarker>();
        var runIndex = new Dictionary<string, int>();
        string? prevRun = null;
        GenerationPhase? prevPhase = null;
        foreach (var p in ordered)
        {
            if (!runIndex.ContainsKey(p.RunKey))
                runIndex[p.RunKey] = runIndex.Count + 1;
            bool runChanged = prevRun is not null && p.RunKey != prevRun;
            bool phaseChanged = prevPhase.HasValue && p.Phase != prevPhase.Value;
            // Один маркер на точку, приоритет: финал > запуск > фаза.
            // Номера запусков — порядковые («Запуск 1»), seed-числа наружу не идут (E4).
            if (p.IsTerminal)
                markers.Add(new TrajectoryMarker(p.Elapsed, p.BestSoft, PhaseLabel(p.Phase)));
            else if (runChanged)
                markers.Add(new TrajectoryMarker(p.Elapsed, p.BestSoft, $"Запуск {runIndex[p.RunKey]}"));
            else if (phaseChanged)
                markers.Add(new TrajectoryMarker(p.Elapsed, p.BestSoft, PhaseLabel(p.Phase)));
            prevRun = p.RunKey;
            prevPhase = p.Phase;
        }

        int valued = ordered.Count(p => p.BestSoft.HasValue);
        return new TrajectorySnapshot(ordered, markers,
            HasData: ordered.Count > 0, HasLine: valued >= 2);
    }
}
