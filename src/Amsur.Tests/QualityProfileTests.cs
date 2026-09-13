using Amsur.Application;
using Amsur.Domain;
using Amsur.Infrastructure;
using Amsur.Scheduling.Core;
using Amsur.Scheduling.OrTools;

namespace Amsur.Tests;

// S5 P4: профили как данные + персистентность CUSTOM + проброс в solver/оркестратор.
public sealed class QualityProfileTests : IAsyncDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"amsur-qp-{Guid.NewGuid():N}.db");
    private string Cs => $"Data Source={_dbPath}";

    public async ValueTask DisposeAsync()
    {
        for (int i = 0; i < 5; i++)
        {
            try { if (File.Exists(_dbPath)) File.Delete(_dbPath); break; }
            catch { await Task.Delay(100); }
        }
        try { if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal"); } catch { }
        try { if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm"); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SaveCustom_Roundtrip_AndSingleActive()
    {
        var store = new SqliteQualityProfileStore(Cs);
        await store.InitializeAsync();
        Assert.Null(await store.GetActiveAsync());
        await store.SaveCustomAsync("Наш профиль", "TEACHER_FRIENDLY",
            new Dictionary<string, long> { ["teacher-gap"] = 20, ["teacher-cross-shift-gap"] = 5 });
        var active = await store.GetActiveAsync();
        Assert.NotNull(active);
        Assert.Equal("Наш профиль", active.Name);
        Assert.Equal("TEACHER_FRIENDLY", active.BaseProfile);
        Assert.Equal(20, active.Weights["teacher-gap"]);
        Assert.Equal(RuleCatalog.Version, active.CatalogVersion);
        var rs = active.ToRuleSet();
        Assert.Equal("CUSTOM", rs.ProfileName);
        Assert.Equal(20, rs.Weight("teacher-gap"));
        // Второе сохранение деактивирует первое (single-active).
        await store.SaveCustomAsync("Второй", "STANDARD",
            new Dictionary<string, long> { ["teacher-gap"] = 12 });
        var active2 = await store.GetActiveAsync();
        Assert.Equal("Второй", active2!.Name);
    }

    [Fact]
    public async Task SaveCustom_InvalidWeight_RejectedBeforeWrite()
    {
        var store = new SqliteQualityProfileStore(Cs);
        await store.InitializeAsync();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.SaveCustomAsync("Плохой", "STANDARD",
                new Dictionary<string, long> { ["teacher-cross-shift-gap"] = 500 }));
        Assert.Null(await store.GetActiveAsync());
    }

    [Fact]
    public void QualityHints_CoverKeyCodes()
    {
        foreach (var code in new[] { "student-gap", "teacher-gap", "teacher-cross-shift-gap",
                     "subject-maxperday", "room-preference", "heavy-edge" })
        {
            var h = QualityHints.For(code);
            Assert.Equal(code, h.Code);
            Assert.NotEqual("", h.Title);
            Assert.NotEqual("", h.When);
        }
        Assert.Equal("Стандарт", QualityHints.ProfileName("STANDARD"));
        Assert.Equal("Удобно учителям", QualityHints.ProfileName("TEACHER_FRIENDLY"));
    }

    [Fact]
    public void Orchestrator_DefaultRulesStandard()
    {
        var orch = new GenerationOrchestrator(_ => throw new InvalidOperationException("no factory"),
            (_, _, _) => throw new InvalidOperationException("no run"));
        Assert.Equal("STANDARD", orch.Rules.ProfileName);
        orch.Rules = RuleResolver.Resolve("TEACHER_FRIENDLY");
        Assert.Equal(20, orch.Rules.Weight("teacher-gap"));
    }

    [Fact]
    public async Task Solver_NonStandardProfile_DiagnosticsHonest()
    {
        var cls = new Amsur.Domain.SchoolClass
        {
            AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5, StudentCount = 25,
        };
        var t = new Amsur.Domain.Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var s = new Amsur.Domain.Subject { Name = "Математика", MaxPerDay = 2 };
        var item = new Amsur.Domain.CurriculumItem
        {
            ClassId = cls.Id, SubjectId = s.Id, TeacherId = t.Id, HoursPerWeek = 2,
        };
        var input = new ProblemInput([cls], [t], [s], [item], [], [], [], 2, 4);
        var (problem, errors) = ProblemBuilder.Build(input,
            new SolverOptions(MaxTimeSeconds: 5, NumSearchWorkers: 1, RandomSeed: 7));
        Assert.Empty(errors);
        var result = await new OrToolsSolver().SolveAsync(problem!,
            CancellationToken.None, rules: RuleResolver.Resolve("TEACHER_FRIENDLY"));
        Assert.Equal(SolverStatus.Feasible, result.Status);
        Assert.Equal(0, result.HardViolations);
        Assert.Contains(result.Diagnostics, d => d.Contains("TEACHER_FRIENDLY"));
    }
}
