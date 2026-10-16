using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// Черновик-фолбэк (16.10.2026): когда строгий поиск не дал полного расписания,
// черновик отдаёт лучшее доступное и честно разделяет нарушения на
// «школа разрешила» и «надо править руками».
public sealed class DraftPlannerTests
{
    private static SchedulingProblem Problem(
        int occCount, int slots, int classCap, int days = 1,
        int teacherCap = 6, int subjectCap = 7,
        IReadOnlyList<int>? explicitSlots = null)
    {
        var classId = Guid.NewGuid();
        var teacher = Guid.NewGuid();
        var subject = Guid.NewGuid();
        var occs = Enumerable.Range(0, occCount).Select(_ => new LessonOccurrence
        {
            ClassId = classId, SubjectId = subject, TeacherId = teacher,
            CurriculumItemId = Guid.NewGuid(), StableKey = $"у{_ + 1}",
        }).ToList();
        int from = 1;
        int to = slots;
        var band = explicitSlots?.ToList()
            ?? Enumerable.Range(from, to - from + 1).ToList();
        return new SchedulingProblem
        {
            Occurrences = occs,
            Classes = new()
            {
                [classId] = new SchoolClass
                {
                    AcademicYearId = Guid.NewGuid(), Name = "5А",
                    Grade = 5, MaxLessonsPerDay = classCap,
                }
            },
            Teachers = new() { [teacher] = new Teacher { Name = "Иванов", MaxLessonsPerDay = teacherCap } },
            Subjects = new() { [subject] = new Subject { Name = "Математика", MaxPerDay = subjectCap } },
            AllowedSlots = occs.ToDictionary(o => o.Id, _ => band),
            AllowedDays = occs.ToDictionary(o => o.Id, _ => Enumerable.Range(0, days).ToList()),
            DaysCount = days,
            SlotsPerDay = slots,
        };
    }

    private static DraftOptions Relax(params string[] codes) =>
        new(new HashSet<string>(codes, StringComparer.Ordinal), ExtraLessonsPerDay: 1, Seed: 1);

    [Fact]
    public void StrictOnly_ClassCapBlocks_LeavesUnplaced_WithoutViolations()
    {
        // Норма класса 2 урока/день, уроков 3 — жадный обязан оставить один.
        var p = Problem(occCount: 3, slots: 7, classCap: 2);
        var draft = DraftPlanner.Build(p, DraftOptions.StrictOnly(seed: 1));

        Assert.False(draft.IsComplete);
        Assert.Single(draft.Unplaced);
        Assert.Equal(2, draft.PlacedCount);
        Assert.Empty(draft.Blocking);
        Assert.Empty(draft.Allowed);
    }

    [Fact]
    public void RelaxedClassCap_PlacesAll_AndReportsAllowedBreak()
    {
        var p = Problem(occCount: 3, slots: 7, classCap: 2);
        var draft = DraftPlanner.Build(p, Relax(DraftPlanner.ClassMaxPerDay));

        Assert.True(draft.IsComplete);
        Assert.Empty(draft.Unplaced);
        Assert.Equal(3, draft.PlacedCount);
        Assert.Empty(draft.Blocking);
        Assert.Contains(draft.Allowed, v => v.Code == DraftPlanner.ClassMaxPerDay);
        Assert.True(draft.IsUsable);
        Assert.Contains(draft.Summary, s => s.Contains("Разрешено школой", StringComparison.Ordinal));
    }

    [Fact]
    public void RelaxedTeacherCap_PlacesAll_WhenClassCapIsNotTheBlocker()
    {
        // Классу можно 3 урока/день, но учителю — 2: без послабления учителя один урок не встанет.
        var p = Problem(occCount: 3, slots: 7, classCap: 3, teacherCap: 2);
        var strict = DraftPlanner.Build(p, DraftOptions.StrictOnly(seed: 1));
        Assert.Single(strict.Unplaced);

        var draft = DraftPlanner.Build(p, Relax(DraftPlanner.TeacherMaxPerDay));
        Assert.True(draft.IsComplete);
        Assert.Contains(draft.Allowed, v => v.Code == DraftPlanner.TeacherMaxPerDay);
        Assert.Empty(draft.Blocking);
    }

    [Fact]
    public void UnallowedGap_IsNotedButDoesNotBlockExport()
    {
        // Классу доступны только слоты 1 и 4 → между уроками окно (2,3) → student-gap.
        var p = Problem(occCount: 2, slots: 5, classCap: 7, explicitSlots: [1, 4]);
        var onlyClassCap = DraftPlanner.Build(p, Relax(DraftPlanner.ClassMaxPerDay));

        // Окна школой не разрешены → идут в «проверь глазами», а не в блокирующие (D-42).
        Assert.Contains(onlyClassCap.Noted, v => v.Code == DraftPlanner.StudentGap);
        Assert.Empty(onlyClassCap.Blocking);
        Assert.True(onlyClassCap.IsUsable);
        Assert.Contains(onlyClassCap.Summary, s => s.Contains("Проверь глазами", StringComparison.Ordinal));

        // Разрешили окна — нарушение становится «разрешено школой», блокирующих по-прежнему нет.
        var withGaps = DraftPlanner.Build(p,
            Relax(DraftPlanner.ClassMaxPerDay, DraftPlanner.StudentGap));
        Assert.Empty(withGaps.Blocking);
        Assert.Contains(withGaps.Allowed, v => v.Code == DraftPlanner.StudentGap);
        Assert.True(withGaps.IsUsable);
    }

    [Fact]
    public void NonBlockingCodes_AreSingleSourceOfTruth()
    {
        Assert.True(DraftPlanner.IsNonBlockingForDraft("placement-count"));
        Assert.True(DraftPlanner.IsNonBlockingForDraft(DraftPlanner.StudentGap));
        Assert.True(DraftPlanner.IsNonBlockingForDraft(DraftPlanner.StudentLateStart));
        Assert.False(DraftPlanner.IsNonBlockingForDraft(DraftPlanner.ClassMaxPerDay));
        Assert.False(DraftPlanner.IsNonBlockingForDraft("room-capability"));
    }

    [Fact]
    public void Relaxation_DoesNotMutateInputProblem()
    {
        var p = Problem(occCount: 3, slots: 7, classCap: 2);
        int before = p.Classes.Values.Single().MaxLessonsPerDay;

        _ = DraftPlanner.Build(p, Relax(DraftPlanner.ClassMaxPerDay));

        Assert.Equal(before, p.Classes.Values.Single().MaxLessonsPerDay);
        Assert.Equal(2, p.Classes.Values.Single().MaxLessonsPerDay);
    }

    [Fact]
    public void Summary_MentionsUnplacedLessons()
    {
        var p = Problem(occCount: 3, slots: 7, classCap: 2);
        var draft = DraftPlanner.Build(p, DraftOptions.StrictOnly(seed: 1));

        Assert.Contains(draft.Summary, s => s.Contains("Не поставлено: 1", StringComparison.Ordinal));
        Assert.Contains(draft.Summary, s => s.Contains("размещено 2 из 3", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_IsDeterministic()
    {
        var p = Problem(occCount: 4, slots: 7, classCap: 3, days: 2);
        var a = DraftPlanner.Build(p, Relax(DraftPlanner.ClassMaxPerDay));
        var b = DraftPlanner.Build(p, Relax(DraftPlanner.ClassMaxPerDay));

        var keys = a.Placements
            .OrderBy(x => x.OccurrenceId)
            .Select(x => $"{x.OccurrenceId}:{x.DayIndex}:{x.SlotIndex}");
        var keys2 = b.Placements
            .OrderBy(x => x.OccurrenceId)
            .Select(x => $"{x.OccurrenceId}:{x.DayIndex}:{x.SlotIndex}");
        Assert.Equal(keys, keys2);
        Assert.Equal(a.Unplaced, b.Unplaced);
    }

    [Fact]
    public void ExtraLessonsPerDay_Zero_MeansNoRelaxationEvenIfAllowed()
    {
        var p = Problem(occCount: 3, slots: 7, classCap: 2);
        var options = new DraftOptions(
            new HashSet<string>(StringComparer.Ordinal) { DraftPlanner.ClassMaxPerDay },
            ExtraLessonsPerDay: 0, Seed: 1);

        var draft = DraftPlanner.Build(p, options);
        Assert.Single(draft.Unplaced);
    }
}
