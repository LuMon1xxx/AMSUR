using Amsur.Scheduling.Core;

namespace Amsur.Application;

// Человеческая оценка качества вместо голого «Soft = 3374» (промт §15).
// Три честных уровня + топ-3 «что можно улучшить» + ссылка «почему так».
// Без выдуманной точности: цифры — только реальные юниты из breakdown.
public sealed record QualityRatingResult(
    string Label, int Level, IReadOnlyList<string> Improvements);

public static class QualityRating
{
    public static QualityRatingResult FromBreakdown(PenaltyBreakdown breakdown)
    {
        var units = new List<(string Code, long Units, long Value)>();
        foreach (var c in breakdown.Components)
        {
            if (c.Value <= 0) continue;
            long w = c.Code switch
            {
                "student-gap" => RuleCatalog.StudentGap,
                "student-late-start" => RuleCatalog.StudentLateStart,
                "teacher-gap" => RuleCatalog.TeacherGap,
                "teacher-cross-shift-gap" => RuleCatalog.TeacherCrossShiftGap,
                "subject-maxperday" => RuleCatalog.SubjectMaxPerDay,
                "room-preference" => RuleCatalog.RoomPreference,
                "heavy-edge" => RuleCatalog.HeavyEdge,
                "relation-violation" => RuleCatalog.RelationViolation,
                _ => 0,
            };
            units.Add((c.Code, w > 0 ? c.Value / w : c.Value, c.Value));
        }
        var improvements = units
            .OrderByDescending(u => u.Value)
            .Take(3)
            .Select(u => $"{QualityExplainer.HumanName(u.Code)}: {u.Units}")
            .ToList();
        bool hasStudent = units.Any(u =>
            u.Code is "student-gap" or "student-late-start");
        if (breakdown.Total == 0)
            return new QualityRatingResult("Отличное", 0, []);
        if (hasStudent)
            return new QualityRatingResult("Требует внимания", 2, improvements);
        return new QualityRatingResult("Хорошее", 1, improvements);
    }
}
