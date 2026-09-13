namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// Input validation (отдельно от solver constraints и FullValidator — SPEC §6).
public static class InputValidator
{
    public static List<string> Validate(SchedulingProblem problem)
    {
        var errors = new List<string>();
        if (problem.Occurrences.Count == 0) errors.Add("No lesson occurrences.");
        foreach (var occ in problem.Occurrences)
        {
            if (!problem.Classes.ContainsKey(occ.ClassId)) errors.Add($"Occurrence {occ.Id}: unknown class.");
            if (!problem.Teachers.ContainsKey(occ.TeacherId)) errors.Add($"Occurrence {occ.Id}: unknown teacher.");
            if (!problem.Subjects.ContainsKey(occ.SubjectId)) errors.Add($"Occurrence {occ.Id}: unknown subject.");
            if (occ.DurationSlots is < 1 or > 2) errors.Add($"Occurrence {occ.Id}: bad duration.");
            if (problem.AllowedSlots.TryGetValue(occ.Id, out var s) && s.Count == 0)
                errors.Add($"Occurrence {occ.Id}: empty slot domain (shift incompatibility).");
        }
        // Sync guards (P0 B3): >=2 участников, дизъюнктные составы.
        foreach (var grp in problem.Occurrences.Where(o => o.SyncGroupId.HasValue).GroupBy(o => o.SyncGroupId!.Value))
        {
            if (grp.Count() < 2) errors.Add($"Sync group {grp.Key}: less than 2 members.");
            var teachers = grp.Select(o => o.TeacherId).Distinct().Count();
            if (teachers != grp.Count()) errors.Add($"Sync group {grp.Key}: one teacher covers both halves.");
        }
        return errors;
    }
}
