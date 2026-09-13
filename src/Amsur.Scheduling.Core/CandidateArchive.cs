namespace Amsur.Scheduling.Core;

// E1 Archive Spike: in-memory пул VISITED-кандидатов (D-15).
// Архив НЕ предполагает «later incumbent == better soft»: каждый кандидат идёт
// Candidate → FullValidator → SoftEvaluator → Archive. Hard>0 — никогда не принят.
// P1-scope: volatile, без персиста; Top5View/прогресс — E2+.
public sealed record ScheduleCandidate(
    IReadOnlyList<PlacedLesson> Placements, // immutable snapshot (копия при создании)
    long SoftTotal,
    PenaltyBreakdown Breakdown,
    string Fingerprint,
    int Seed,
    long Proxy,          // proxy-objective solver на момент incumbent (sum t)
    string SolverStats,  // seed/workers/elapsed/sequence — воспроизводимость
    DateTime Timestamp,
    string Phase,        // "A" | "B"
    IReadOnlyDictionary<Guid, string> OccKeys, // occId → StableKey (кросс-запусковый identity)
    string ProfileName = "STANDARD", // профиль весов (S5: кандидаты разных профилей несравнимы)
    int CatalogVersion = RuleCatalog.Version,
    string WeightsHash = "") // ужатый снимок весов (полный JSON — в QualityProfiles)
{
    /// <returns>null, если кандидат не FullValidator-clean (в архив не идёт).</returns>
    public static ScheduleCandidate? Create(
        SchedulingProblem problem,
        IReadOnlyList<PlacedLesson> placements,
        int seed, long proxy, string phase, string stats = "",
        EffectiveRuleSet? rules = null)
    {
        if (!PlacementValidator.Validate(problem, placements).IsValid) return null;
        var rs = rules ?? EffectiveRuleSet.Default;
        var breakdown = SoftEvaluator.Evaluate(problem, placements, rs);
        var keys = problem.Occurrences.ToDictionary(o => o.Id,
            o => string.IsNullOrEmpty(o.StableKey) ? o.Id.ToString("N") : o.StableKey);
        var snapshot = placements
            .Select(p => new PlacedLesson
            {
                OccurrenceId = p.OccurrenceId, DayIndex = p.DayIndex,
                SlotIndex = p.SlotIndex, RoomId = p.RoomId
            }).ToList();
        return new ScheduleCandidate(snapshot, breakdown.Total, breakdown,
            BuildFingerprint(keys, snapshot), seed, proxy, stats, DateTime.UtcNow, phase, keys,
            rs.ProfileName, rs.CatalogVersion, RuleResolver.WeightsFingerprint(rs));
    }

    private static string KeyOf(
        IReadOnlyDictionary<Guid, string> keys, Guid occId) =>
        keys.TryGetValue(occId, out var k) ? k : occId.ToString("N");

    internal static string KeyOfPublic(IReadOnlyDictionary<Guid, string> keys, Guid occId) =>
        KeyOf(keys, occId);

    /// <summary>stableKey → (day, slot, room), детерминированный порядок. E3: сравним между запусками.</summary>
    public static string BuildFingerprint(
        IReadOnlyDictionary<Guid, string> keys,
        IReadOnlyList<PlacedLesson> placements) =>
        string.Join(";", placements
            .OrderBy(p => KeyOf(keys, p.OccurrenceId))
            .Select(p => $"{KeyOf(keys, p.OccurrenceId)}:{p.DayIndex}:{p.SlotIndex}:{p.RoomId?.ToString("N")[..8] ?? "-"}"));
}

public sealed class ScheduleCandidateArchive(int maxSize = 5, long diversityThreshold = 100)
{
    // Начальные веса weighted Hamming (НЕ тюнингованы — baseline E1):
    // день 100 / слот 30 / кабинет 5.
    public const long DayWeight = 100;
    public const long SlotWeight = 30;
    public const long RoomWeight = 5;

    private readonly List<ScheduleCandidate> _members = [];

    // E2-счётчики sufficiency (не влияют на политику; измерения по измерениям,
    // пересечение возможно: отклонённый может быть одновременно хуже и похож).
    public int AcceptedCount { get; private set; }
    public int RejectedInvalidCount { get; private set; }
    public int RejectedWorseCount { get; private set; }
    public int RejectedSimilarCount { get; private set; }

    public IReadOnlyList<ScheduleCandidate> Members =>
        _members.OrderBy(m => m.SoftTotal).ToList();

    public static long Distance(ScheduleCandidate a, ScheduleCandidate b)
    {
        var bm = new Dictionary<string, PlacedLesson>();
        foreach (var p in b.Placements)
        {
            string k = ScheduleCandidate.KeyOfPublic(b.OccKeys, p.OccurrenceId);
            bm[k] = p;
        }
        long dist = 0;
        foreach (var p in a.Placements)
        {
            string k = ScheduleCandidate.KeyOfPublic(a.OccKeys, p.OccurrenceId);
            if (!bm.TryGetValue(k, out var q)) { dist += DayWeight; continue; }
            if (p.DayIndex != q.DayIndex) dist += DayWeight;
            else if (p.SlotIndex != q.SlotIndex) dist += SlotWeight;
            if (p.RoomId != q.RoomId) dist += RoomWeight;
        }
        return dist;
    }

    /// <summary>
    /// Политика E1-baseline: clean-only; пустой — принять; лучше худшего — заменить
    /// худшего; иначе — принять как diversity только при minDist &gt;= threshold.
    /// </summary>
    public bool TryAdd(ScheduleCandidate? candidate)
    {
        if (candidate is null) { RejectedInvalidCount++; return false; }
        if (_members.Count < maxSize) { _members.Add(candidate); AcceptedCount++; return true; }
        var worst = _members.MaxBy(m => m.SoftTotal)!;
        if (candidate.SoftTotal < worst.SoftTotal)
        {
            _members.Remove(worst);
            _members.Add(candidate);
            AcceptedCount++;
            return true;
        }
        long minDist = _members.Min(m => Distance(candidate, m));
        bool worse = candidate.SoftTotal >= worst.SoftTotal;
        bool similar = minDist < diversityThreshold;
        if (!worse || !similar)
        {
            // Лучше худшего (quality) или достаточно отличается (diversity) — принять.
            _members.Remove(worst);
            _members.Add(candidate);
            AcceptedCount++;
            return true;
        }
        RejectedWorseCount++;
        RejectedSimilarCount++;
        return false;
    }
}
