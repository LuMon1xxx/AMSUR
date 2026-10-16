using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// Черновик-фолбэк (16.10.2026): строгий поиск не нашёл расписание — оркестратор
// отдаёт черновик в VM (окно + выгрузка). В архив кандидатов черновик не попадает,
// значит и принять его нельзя (INV-01: Accept только по чистым кандидатам).
public sealed class DraftFallbackOrchestratorTests
{
    private static SchedulingProblem Tiny(int classCap = 1)
    {
        var cls = new SchoolClass
        {
            AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5,
            StudentCount = 25, MaxLessonsPerDay = classCap,
        };
        var teacher = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var subjects = new[] { "Мат", "Рус", "Анг" }
            .Select(n => new Subject { Name = n, MaxPerDay = 2 }).ToList();
        var curriculum = subjects.Select(s => new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = s.Id, TeacherId = teacher.Id, HoursPerWeek = 1
        }).ToList();
        var input = new ProblemInput([cls], [teacher], subjects, curriculum,
            [], [], [], DaysCount: 2, SlotsPerDay: 3);
        var (problem, errors) = ProblemBuilder.Build(input,
            new SolverOptions(MaxTimeSeconds: 5, NumSearchWorkers: 1, RandomSeed: 11));
        Assert.Empty(errors);
        return problem!;
    }

    private static readonly StreamingRun Failed = (p, ct, sink) =>
        Task.FromResult(new SolverResult(SolverStatus.Unknown, [], 0, 0, 5, 0, 0,
            new Dictionary<string, long>(), [], new Dictionary<string, long>()));

    private static DraftOptions Relaxed(int seed = 11) =>
        new(DraftOptions.DefaultRelaxCodes, ExtraLessonsPerDay: 1, Seed: seed);

    [Fact]
    public async Task FailedRun_ThenDraft_IsReportedHonestly()
    {
        // Норма класса 1 урок/день, уроков 3 → строго не влезает; послабление даёт полное.
        var problem = Tiny(classCap: 1);
        var orch = new GenerationOrchestrator(_ => problem, Failed);
        var outcome = await orch.RunAsync([11]);
        Assert.False(outcome.HasFeasible);
        Assert.Empty(orch.ViewModel.Top5.Cards);

        var draft = orch.BuildDraft(Relaxed());

        Assert.NotNull(draft);
        Assert.True(orch.ViewModel.HasDraft);
        Assert.Contains("Черновик", orch.ViewModel.DraftText, StringComparison.Ordinal);
        Assert.True(orch.ViewModel.DraftExportable);
        Assert.Equal(problem.Occurrences.Count, draft!.PlacedCount);
        Assert.Empty(draft.Unplaced);
        // INV-01: черновик не стал кандидатом — принимать нечего.
        Assert.Empty(orch.Archive.Members);
        Assert.Empty(orch.ViewModel.Top5.Cards);
    }

    [Fact]
    public async Task StrictOnlyDraft_KeepsGapHonest()
    {
        var problem = Tiny(classCap: 1);
        var orch = new GenerationOrchestrator(_ => problem, Failed);
        await orch.RunAsync([11]);

        var draft = orch.BuildDraft(DraftOptions.StrictOnly(seed: 11));

        Assert.NotNull(draft);
        Assert.False(draft!.IsComplete);
        Assert.NotEmpty(draft.Unplaced);
        Assert.Contains("Не поставлено", orch.ViewModel.DraftText, StringComparison.Ordinal);
        Assert.Empty(draft.Blocking); // без послаблений нарушений нет, просто не всё поставлено
    }

    [Fact]
    public void BuildDraft_WithoutGeneration_SaysHonestly()
    {
        var orch = new GenerationOrchestrator(_ => throw new InvalidOperationException("нет"), Failed);

        var draft = orch.BuildDraft(Relaxed());

        Assert.Null(draft);
        Assert.False(orch.ViewModel.HasDraft);
        Assert.False(orch.ViewModel.DraftExportable);
        Assert.Contains("нет данных", orch.ViewModel.DraftText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Draft_DoesNotTouchArchiveCandidates()
    {
        // Чистый кандидат уже есть: черновик собирается отдельно и архив не трогает.
        var problem = Tiny(classCap: 7);
        StreamingRun feasible = (p, ct, sink) =>
        {
            var pl = p.Occurrences.Select((o, i) => new PlacedLesson
            {
                OccurrenceId = o.Id, DayIndex = i / 3, SlotIndex = i % 3 + 1,
            }).ToList();
            return Task.FromResult(new SolverResult(SolverStatus.Feasible, pl, 0, 1, 10, 5, 0,
                new Dictionary<string, long>(), [], new Dictionary<string, long>()));
        };
        var orch = new GenerationOrchestrator(_ => problem, feasible);
        await orch.RunAsync([11]);
        int cardsBefore = orch.ViewModel.Top5.Cards.Count;
        Assert.True(cardsBefore > 0);

        _ = orch.BuildDraft(Relaxed());

        Assert.Equal(cardsBefore, orch.ViewModel.Top5.Cards.Count);
        Assert.Equal(cardsBefore, orch.Archive.Members.Count);
    }
}
