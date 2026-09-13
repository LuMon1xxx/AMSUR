using Amsur.Scheduling.Core;

namespace Amsur.Application;

// E4 §6 — человеческое объяснение различий (без raw fingerprint).
public static class DiversityExplainer
{
    public sealed record DiffSummary(
        int ChangedCount, int DayChanges, int SlotOnlyChanges, int RoomChanges);

    public static DiffSummary Compare(ScheduleCandidate best, ScheduleCandidate other)
    {
        var bm = IndexByStableKey(best);
        var om = IndexByStableKey(other);
        int changed = 0, days = 0, slots = 0, rooms = 0;
        foreach (var (key, b) in bm)
        {
            if (!om.TryGetValue(key, out var o)) { changed++; days++; continue; }
            bool timeDiff = b.DayIndex != o.DayIndex || b.SlotIndex != o.SlotIndex;
            bool roomDiff = b.RoomId != o.RoomId;
            if (!timeDiff && !roomDiff) continue;
            changed++;
            if (b.DayIndex != o.DayIndex) days++;
            else if (b.SlotIndex != o.SlotIndex) slots++;
            if (roomDiff) rooms++;
        }
        return new DiffSummary(changed, days, slots, rooms);
    }

    private static string KeyOf(ScheduleCandidate c, Guid occId) =>
        c.OccKeys.TryGetValue(occId, out var k) ? k : occId.ToString("N");

    private static Dictionary<string, PlacedLesson> IndexByStableKey(ScheduleCandidate c)
    {
        var d = new Dictionary<string, PlacedLesson>();
        foreach (var p in c.Placements)
            d[KeyOf(c, p.OccurrenceId)] = p;
        return d;
    }

    /// <summary>Строки для карточки («12 изменений относительно варианта 1» и т.д.).</summary>
    public static IReadOnlyList<string> ExplainLines(ScheduleCandidate best, ScheduleCandidate other)
    {
        var s = Compare(best, other);
        if (s.ChangedCount == 0) return ["Совпадает с вариантом 1"];
        var lines = new List<string> { $"{Plural(s.ChangedCount, "изменение", "изменения", "изменений")} относительно варианта 1" };
        if (s.DayChanges > 0)
            lines.Add($"Другие дни для {Plural(s.DayChanges, "урока", "уроков", "уроков")}");
        else if (s.SlotOnlyChanges > 0)
            lines.Add($"Другое время для {Plural(s.SlotOnlyChanges, "урока", "уроков", "уроков")}");
        if (s.RoomChanges > 0)
            lines.Add($"Другие кабинеты для {Plural(s.RoomChanges, "урока", "уроков", "уроков")}");
        return lines;

        static string Plural(int n, string one, string few, string many)
        {
            string form = (n % 10 == 1 && n % 100 != 11) ? one
                : (n % 10 >= 2 && n % 10 <= 4 && (n % 100 < 10 || n % 100 >= 20)) ? few : many;
            return $"{n} {form}";
        }
    }

    /// <summary>Процент отличия для бейджа (нормировка на max день-вес).</summary>
    public static int PercentDifferent(ScheduleCandidate best, ScheduleCandidate other, int occurrenceCount)
    {
        if (occurrenceCount <= 0) return 0;
        long dist = ScheduleCandidateArchive.Distance(best, other);
        long max = (long)occurrenceCount * ScheduleCandidateArchive.DayWeight;
        return (int)Math.Clamp(dist * 100 / Math.Max(1, max), 0, 100);
    }
}
