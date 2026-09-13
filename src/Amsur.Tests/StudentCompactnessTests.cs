using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// D-28: компактность ученика — внутренние окна запрещены, старт не позже anchor+1.
// Якорь = min AllowedSlots класса (1-я смена → 1, 2-я → 8).
public sealed class StudentCompactnessTests
{
    private static SchedulingProblem Problem(
        int slots, int anchorBandStart = 1, int maxPerDay = 7, int grade = 5,
        int occCount = 2, int days = 1)
    {
        var classId = Guid.NewGuid();
        var teacher = Guid.NewGuid();
        var subject = Guid.NewGuid();
        var occs = Enumerable.Range(0, occCount).Select(_ => new LessonOccurrence
        {
            ClassId = classId, SubjectId = subject, TeacherId = teacher,
            CurriculumItemId = Guid.NewGuid(),
        }).ToList();
        var band = Enumerable.Range(anchorBandStart, 7).ToList();
        return new SchedulingProblem
        {
            Occurrences = occs,
            Classes = new()
            {
                [classId] = new SchoolClass
                {
                    AcademicYearId = Guid.NewGuid(), Name = "5А",
                    Grade = grade, MaxLessonsPerDay = maxPerDay,
                }
            },
            Teachers = new() { [teacher] = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 } },
            Subjects = new() { [subject] = new Subject { Name = "Математика", MaxPerDay = 2 } },
            AllowedSlots = occs.ToDictionary(o => o.Id, _ => band),
            AllowedDays = occs.ToDictionary(o => o.Id, _ => Enumerable.Range(0, days).ToList()),
            DaysCount = days,
            SlotsPerDay = slots,
        };
    }

    [Fact]
    public void InternalGap_IsHard()
    {
        var p = Problem(slots: 7);
        var pl = p.Occurrences.Select((o, i) => new PlacedLesson
        {
            OccurrenceId = o.Id, DayIndex = 0, SlotIndex = i == 0 ? 1 : 3,
        }).ToList();
        var r = PlacementValidator.Validate(p, pl);
        Assert.Contains(r.HardViolations, v => v.Code == "student-gap");
    }

    [Fact]
    public void StartAtSecondSlot_Allowed()
    {
        var p = Problem(slots: 7);
        var pl = p.Occurrences.Select((o, i) => new PlacedLesson
        {
            OccurrenceId = o.Id, DayIndex = 0, SlotIndex = i + 2, // уроки 2,3
        }).ToList();
        var r = PlacementValidator.Validate(p, pl);
        Assert.DoesNotContain(r.HardViolations, v => v.Code == "student-gap");
        Assert.DoesNotContain(r.HardViolations, v => v.Code == "student-late-start");
    }

    [Fact]
    public void StartAtThirdSlot_IsHard()
    {
        var p = Problem(slots: 7);
        var pl = p.Occurrences.Select((o, i) => new PlacedLesson
        {
            OccurrenceId = o.Id, DayIndex = 0, SlotIndex = i + 3, // уроки 3,4
        }).ToList();
        var r = PlacementValidator.Validate(p, pl);
        Assert.Contains(r.HardViolations, v => v.Code == "student-late-start");
    }

    [Fact]
    public void SecondShiftAnchor_AllowsSlots8And9()
    {
        var p = Problem(slots: 14, anchorBandStart: 8);
        var pl = p.Occurrences.Select((o, i) => new PlacedLesson
        {
            OccurrenceId = o.Id, DayIndex = 0, SlotIndex = 8 + i,
        }).ToList();
        Assert.True(PlacementValidator.Validate(p, pl).IsValid);
    }

    [Fact]
    public void SecondShiftAnchor_StartAt10_IsHard()
    {
        var p = Problem(slots: 14, anchorBandStart: 8);
        var pl = p.Occurrences.Select((o, i) => new PlacedLesson
        {
            OccurrenceId = o.Id, DayIndex = 0, SlotIndex = 10 + i,
        }).ToList();
        var r = PlacementValidator.Validate(p, pl);
        Assert.Contains(r.HardViolations, v => v.Code == "student-late-start");
    }

    [Fact]
    public void ClassDailyCap_IsHard()
    {
        var p = Problem(slots: 7, maxPerDay: 2, occCount: 3);
        var pl = p.Occurrences.Select((o, i) => new PlacedLesson
        {
            OccurrenceId = o.Id, DayIndex = 0, SlotIndex = i + 1,
        }).ToList();
        var r = PlacementValidator.Validate(p, pl);
        Assert.Contains(r.HardViolations, v => v.Code == "class-maxperday");
    }

    [Fact]
    public void Grade1_TwoFiveLessonDays_IsHard()
    {
        var p = Problem(slots: 7, maxPerDay: 5, grade: 1, occCount: 10, days: 2);
        var pl = p.Occurrences.Select((o, i) => new PlacedLesson
        {
            OccurrenceId = o.Id, DayIndex = i / 5, SlotIndex = (i % 5) + 1,
        }).ToList();
        var r = PlacementValidator.Validate(p, pl);
        Assert.Contains(r.HardViolations, v => v.Code == "class-maxperday");
    }

    [Fact]
    public void CompactRepair_FixesGap()
    {
        var p = Problem(slots: 7, occCount: 2);
        var pl = p.Occurrences.Select((o, i) => new PlacedLesson
        {
            OccurrenceId = o.Id, DayIndex = 0, SlotIndex = i == 0 ? 1 : 3,
        }).ToList();
        Assert.Contains(PlacementValidator.Validate(p, pl).HardViolations,
            v => v.Code == "student-gap");
        var rep = CompactRepair.Repair(p, pl);
        Assert.Equal(1, rep.RepairedDays);
        Assert.Equal(0, rep.FailedDays);
        Assert.True(PlacementValidator.Validate(p, rep.Placements).IsValid);
    }

    [Fact]
    public void CompactRepair_FixesLateStart()
    {
        var p = Problem(slots: 7, occCount: 2);
        var pl = p.Occurrences.Select((o, i) => new PlacedLesson
        {
            OccurrenceId = o.Id, DayIndex = 0, SlotIndex = i + 4, // уроки 4,5
        }).ToList();
        var rep = CompactRepair.Repair(p, pl);
        Assert.Equal(1, rep.RepairedDays);
        var r = PlacementValidator.Validate(p, rep.Placements);
        Assert.DoesNotContain(r.HardViolations, v => v.Code == "student-late-start");
        Assert.DoesNotContain(r.HardViolations, v => v.Code == "student-gap");
    }

    [Fact]
    public void EditorMove_CreatingGap_IsForbidden()
    {
        var p = Problem(slots: 7, occCount: 2);
        var cur = p.Occurrences.Select((o, i) => new PlacedLesson
        {
            OccurrenceId = o.Id, DayIndex = 0, SlotIndex = i + 1,
        }).ToList();
        var move = new CandidateMove(p.Occurrences[1].Id, DayIndex: 0, SlotIndex: 3, RoomId: null);
        var ev = IncrementalEvaluator.Evaluate(p, cur, move);
        Assert.Equal(EvaluationSeverity.Forbidden, ev.Severity);
        Assert.Contains(ev.HardViolations, v => v.Code == "student-gap");
    }

    [Fact]
    public void SoftEvaluator_LateStart_Component()
    {
        var p = Problem(slots: 7, occCount: 2);
        var pl = p.Occurrences.Select((o, i) => new PlacedLesson
        {
            OccurrenceId = o.Id, DayIndex = 0, SlotIndex = i + 4, // уроки 4,5: late 2
        }).ToList();
        var b = SoftEvaluator.Evaluate(p, pl);
        var late = b.Components.First(c => c.Code == "student-late-start");
        Assert.Equal(2 * RuleCatalog.StudentLateStart, late.Value);
        Assert.Equal(b.Components.Sum(c => c.Value), b.Total);
    }

    [Fact]
    public void RuinRecreate_NeverWorse_Deterministic()
    {
        var p = Problem(slots: 7, occCount: 3, days: 2);
        // Грязный старт: день 0 {1,3} с окном + день 1 {1}.
        var start = new List<PlacedLesson>
        {
            new() { OccurrenceId = p.Occurrences[0].Id, DayIndex = 0, SlotIndex = 1 },
            new() { OccurrenceId = p.Occurrences[1].Id, DayIndex = 0, SlotIndex = 3 },
            new() { OccurrenceId = p.Occurrences[2].Id, DayIndex = 1, SlotIndex = 1 },
        };
        var occById = p.Occurrences.ToDictionary(o => o.Id);
        int before = RuinRecreate.FailedDays(p, occById, start);
        Assert.Equal(1, before);
        long softBefore = SoftEvaluator.Evaluate(p, start).Total;
        var a = RuinRecreate.Improve(p, start, TimeSpan.FromSeconds(5), seed: 7);
        var b = RuinRecreate.Improve(p, start, TimeSpan.FromSeconds(5), seed: 7);
        // Контракт: лексикографически не хуже (failed, soft), детерминирован.
        Assert.True(a.FailedDays < before || (a.FailedDays == before && a.SoftTotal <= softBefore));
        Assert.Equal(a.FailedDays, b.FailedDays);
        Assert.Equal(a.SoftTotal, b.SoftTotal);
        Assert.Equal(start.Count, a.Placements.Count); // полнота не теряется
        // На крошечном экземпляре LNS обязан убрать окно полностью.
        Assert.Equal(0, a.FailedDays);
        Assert.True(PlacementValidator.Validate(p, a.Placements).IsValid);
    }
}
