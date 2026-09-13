namespace Amsur.Scheduling.Core;

// E3: слияние пулов разных запусков через единую Archive-политику (E3.2)
// + диагностика «мало кандидатов: поиск плох или пространство мало» (E3 §5).
public static class CandidatePoolMerger
{
    public static ScheduleCandidateArchive Merge(
        IEnumerable<ScheduleCandidate> candidates,
        int maxSize = 5,
        long diversityThreshold = 100)
    {
        var archive = new ScheduleCandidateArchive(maxSize, diversityThreshold);
        foreach (var c in candidates) archive.TryAdd(c);
        return archive;
    }

    public static int UniqueFingerprints(IEnumerable<ScheduleCandidate> candidates) =>
        candidates.Select(c => c.Fingerprint).Distinct().Count();
}

public static class PoolDiagnostics
{
    /// <summary>
    /// Различение WITHOUT expensive proof: флаг OptimalProven идёт из solver
    /// (Phase B завершилась доказательством), остальное — счётчики пула.
    /// </summary>
    public static string Summarize(
        Domain.SolverStatus status,
        bool optimalProven,
        int uniqueCount,
        int k) =>
        (status, optimalProven, uniqueCount >= k) switch
        {
            (_, _, true) => "POOL_SUFFICIENT",
            (_, true, false) => "GENUINELY_SMALL",
            _ => "SEARCH_LIMITED",
        };
}
