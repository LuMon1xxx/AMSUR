namespace Amsur.Application;

// E4 §3 — Throttling 2–4 Hz.
// Solver/Archive работают с полной частотой (Report вызывается на каждый incumbent),
// UI получает только агрегированные состояния не чаще minInterval.
public sealed class ThrottledIncumbentStream : IIncumbentStream
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(300); // ~3.3 Гц

    private readonly TimeSpan _minInterval;
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();
    private DateTime _lastEmittedUtc = DateTime.MinValue;
    private GenerationProgressDto _current;

    // Diagnostics: сколько solver-событий получено vs отдано UI.
    public int ReceivedCount { get; private set; }
    public int EmittedCount { get; private set; }

    public event Action<GenerationProgressDto>? ProgressChanged;

    public ThrottledIncumbentStream(
        TimeSpan? minInterval = null,
        Func<DateTime>? utcNow = null)
    {
        _minInterval = minInterval ?? DefaultInterval;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _current = new GenerationProgressDto(
            GenerationPhase.Preparing, TimeSpan.Zero, null, null,
            0, 0, 5, "", "Подготовка…", false, null);
    }

    public GenerationProgressDto Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>Вызывается с полной частотой solver. Потокобезопасно.</summary>
    public void Report(GenerationProgressDto next)
    {
        Action<GenerationProgressDto>? emit = null;
        lock (_gate)
        {
            ReceivedCount++;
            _current = next;
            var now = _utcNow();
            if (now - _lastEmittedUtc >= _minInterval)
            {
                _lastEmittedUtc = now;
                EmittedCount++;
                emit = ProgressChanged;
            }
        }
        emit?.Invoke(next);
    }

    /// <summary>Принудительно отдать последнее состояние (финал/остановка).</summary>
    public void Flush()
    {
        Action<GenerationProgressDto>? emit;
        GenerationProgressDto snapshot;
        lock (_gate)
        {
            snapshot = _current;
            _lastEmittedUtc = _utcNow();
            EmittedCount++;
            emit = ProgressChanged;
        }
        emit?.Invoke(snapshot);
    }
}
