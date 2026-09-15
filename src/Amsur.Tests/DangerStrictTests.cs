using Amsur.Application;
using Amsur.Domain;
using Amsur.Infrastructure;
using Amsur.Scheduling.Core;
using Amsur.Wpf;

namespace Amsur.Tests;

// B1+B2+A3+A4: опасные подтверждения, ослабление строгого, настройки, validator, restore.
// Промт требует минимум: 3 DangerConfirm + 4 strict-override + 1 AppSettings roundtrip.
public sealed class DangerStrictTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"amsur-danger-{Guid.NewGuid():N}");
    private readonly Func<System.Windows.Window?, string, string, (bool Proceed, bool DontAsk)> _savedPrompter;

    public DangerStrictTests()
    {
        Directory.CreateDirectory(_dir);
        _savedPrompter = DangerConfirm.Prompter;
    }

    public void Dispose()
    {
        DangerConfirm.Prompter = _savedPrompter;
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    // --- B1: 3 DangerConfirm-теста (показ/отмена/глобал-офф) ---

    [Fact]
    public void DangerConfirm_Show_UsesHumanTitle()
    {
        string? seenTitle = null;
        string? seenBody = null;
        DangerConfirm.Prompter = (owner, title, consequence) =>
        {
            seenTitle = title;
            seenBody = consequence;
            return (true, false);
        };
        try
        {
            var (proceed, dontAsk) = DangerConfirm.Show(null, "student-gap", "Окна", "Появятся окна.");
            Assert.True(proceed);
            Assert.False(dontAsk);
            Assert.NotNull(seenTitle);
            Assert.StartsWith("Осторожно:", seenTitle);
            Assert.Contains("Окна у учеников", seenTitle); // человеческое имя из QualityHints
            Assert.Equal("Появятся окна.", seenBody);
        }
        finally { DangerConfirm.Prompter = _savedPrompter; }
    }

    [Fact]
    public void DangerConfirm_Cancel_ReturnsFalse()
    {
        DangerConfirm.Prompter = (_, _, _) => (false, false);
        try
        {
            var (proceed, _) = DangerConfirm.Show(null, "student-gap", "Окна", "Появятся окна.");
            Assert.False(proceed);
        }
        finally { DangerConfirm.Prompter = _savedPrompter; }
    }

    [Fact]
    public async Task DangerConfirm_GlobalOff_SkipsPrompt()
    {
        var session = new AppSession(_dir);
        await session.InitAsync();
        await session.SetConfirmDangerousAsync(false);
        DangerConfirm.Prompter = (_, _, _) => throw new InvalidOperationException("попап не должен лезть");
        try
        {
            bool ok = await session.ConfirmDangerousAsync(null, "student-gap", "Окна", "Появятся окна.");
            Assert.True(ok); // глобал-офф = сразу да, без попапа
        }
        finally { DangerConfirm.Prompter = _savedPrompter; }
        // Персист тумблера: новая сессия читает false.
        var session2 = new AppSession(_dir);
        await session2.InitAsync();
        Assert.False(session2.ConfirmDangerous);
        await session2.SetConfirmDangerousAsync(true); // вернуть для других тестов
    }

    // --- B2: 4 strict-override-теста (вкл/выкл/персист/validator-soft) ---

    private static SchedulingProblem GapProblem()
    {
        var classId = Guid.NewGuid();
        var teacher = Guid.NewGuid();
        var subject = Guid.NewGuid();
        var occs = Enumerable.Range(0, 2).Select(_ => new LessonOccurrence
        {
            ClassId = classId, SubjectId = subject, TeacherId = teacher,
            CurriculumItemId = Guid.NewGuid(),
        }).ToList();
        return new SchedulingProblem
        {
            Occurrences = occs,
            Classes = new()
            {
                [classId] = new SchoolClass
                {
                    AcademicYearId = Guid.NewGuid(), Name = "5А",
                    Grade = 5, MaxLessonsPerDay = 7,
                }
            },
            Teachers = new() { [teacher] = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 } },
            Subjects = new() { [subject] = new Subject { Name = "Математика", MaxPerDay = 2 } },
            AllowedSlots = occs.ToDictionary(o => o.Id, _ => Enumerable.Range(1, 7).ToList()),
            AllowedDays = occs.ToDictionary(o => o.Id, _ => new List<int> { 0 }),
            DaysCount = 1,
            SlotsPerDay = 7,
        };
    }

    private static List<PlacedLesson> GapPlacements(SchedulingProblem p) =>
        p.Occurrences.Select((o, i) => new PlacedLesson
        {
            OccurrenceId = o.Id, DayIndex = 0, SlotIndex = i == 0 ? 1 : 3, // окно внутри дня
        }).ToList();

    [Fact]
    public void StrictOverride_Off_StaysHard()
    {
        var p = GapProblem();
        var r = PlacementValidator.Validate(p, GapPlacements(p)); // дефолт, без relaxed
        Assert.Contains(r.HardViolations, v => v.Code == "student-gap");
    }

    [Fact]
    public void StrictOverride_On_RelaxedToWarnings()
    {
        var p = GapProblem();
        var ed = QualitySettingsEditor.FromRules(EffectiveRuleSet.Default);
        ed.SetLevel("student-gap", 0, confirmed: true); // попап пройден в UI
        var ov = ed.GetOverrides();
        Assert.True(ov.ContainsKey("student-gap"));
        var rs = RuleResolver.Resolve("CUSTOM", ov, new HashSet<string> { "student-gap" });
        Assert.Contains("student-gap", rs.RelaxedStrict);
        var r = PlacementValidator.Validate(p, GapPlacements(p), rs);
        Assert.DoesNotContain(r.HardViolations, v => v.Code == "student-gap");
        Assert.Contains(r.Warnings, v => v.Code == "student-gap");
        Assert.True(r.IsValid); // gate Accept/Export читает тот же набор: Hard==0
    }

    [Fact]
    public async Task StrictOverride_Persist_Roundtrip()
    {
        var db = Path.Combine(_dir, "persist.db");
        var store = new SqliteQualityProfileStore($"Data Source={db}");
        await store.InitializeAsync();
        var ov = new Dictionary<string, long> { ["student-gap"] = 0 };
        var confirmed = new HashSet<string> { "student-gap" };
        await store.SaveCustomAsync("Моя школа", "STANDARD", ov, confirmed);
        var saved = await store.GetActiveAsync();
        Assert.NotNull(saved);
        var rs = saved!.ToRuleSet();
        Assert.Contains("student-gap", rs.RelaxedStrict);
        Assert.Equal(0, rs.Weight("student-gap"));
    }

    [Fact]
    public void StrictOverride_ValidatorSoft_Breakdown()
    {
        var p = GapProblem();
        var rs = RuleResolver.Resolve("CUSTOM",
            new Dictionary<string, long> { ["student-gap"] = 25 },
            new HashSet<string> { "student-gap" });
        var r = PlacementValidator.Validate(p, GapPlacements(p), rs);
        Assert.True(r.IsValid); // ослабленное строгое — не Hard
        Assert.Contains(r.Warnings, v => v.Code == "student-gap");
        var bd = SoftEvaluator.Evaluate(p, GapPlacements(p), rs);
        var comp = bd.Components.First(c => c.Code == "student-gap");
        Assert.True(comp.Value > 0); // штраф виден в breakdown
    }

    // --- B1: AppSettings roundtrip ---

    [Fact]
    public async Task AppSettings_Roundtrip()
    {
        var db = Path.Combine(_dir, "settings.db");
        var store = new SqliteAppSettingsStore($"Data Source={db}");
        await store.InitializeAsync();
        Assert.True(await store.GetBoolAsync("ui.confirmDangerous", true)); // дефолт true
        await store.SetBoolAsync("ui.confirmDangerous", false);
        Assert.False(await store.GetBoolAsync("ui.confirmDangerous", true));
        await store.SetBoolAsync("ui.confirmDangerous", true);
        Assert.True(await store.GetBoolAsync("ui.confirmDangerous", false));
    }

    // --- A4: чужой OccurrenceId — issue, не throw ---

    [Fact]
    public void Validator_UnknownOccurrence_ReturnsIssue()
    {
        var p = GapProblem();
        var placements = GapPlacements(p).Take(1).ToList();
        placements.Add(new PlacedLesson
        {
            OccurrenceId = Guid.NewGuid(), DayIndex = 0, SlotIndex = 1, // чужой ID
        });
        var ex = Record.Exception(() => PlacementValidator.Validate(p, placements));
        Assert.Null(ex); // не бросает KeyNotFound
        var r = PlacementValidator.Validate(p, placements);
        Assert.Contains(r.HardViolations, v => v.Code == "not-placed");
    }

    // --- A3: restore не перезаписывает year.txt, данные сразу видны ---

    [Fact]
    public async Task AppSession_Restore_KeepsYear()
    {
        var session = new AppSession(_dir);
        await session.InitAsync();
        Assert.True(session.IsReady);
        await session.ImportLoadAsync(DemoSchoolTests.DemoRows(), days: 5, slots: 7);
        string yearFile = Path.Combine(_dir, "year.txt");
        string year1 = await File.ReadAllTextAsync(yearFile);

        var session2 = new AppSession(_dir);
        await session2.InitAsync(); // restore из SQLite
        Assert.True(session2.IsReady);
        Assert.True(session2.HasData); // сразу «Загружены», без вранья
        string year2 = await File.ReadAllTextAsync(yearFile);
        Assert.Equal(year1, year2); // перезаписи при restore нет
    }

}
