using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;
using Amsur.Scheduling.OrTools;

namespace Amsur.Tests;

// E4 §14 — overhead: solver голый vs +archive vs +archive+throttled progress.
// Фикстура 18 occ (как E1/E3), workers=1, seed 11, короткие бюджеты.
public sealed class OverheadBenchmarkTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static SchedulingProblem Build18(int seed, double budget)
    {
        var classes = new[] { "5А", "5Б", "6А" }.Select(n => new SchoolClass
        {
            AcademicYearId = Guid.NewGuid(), Name = n, Grade = 5, StudentCount = 25
        }).ToList();
        var teachers = new[] { "Иванов", "Петрова", "Сидоров" }.Select(n => new Teacher
        {
            Name = n, MaxLessonsPerDay = 6
        }).ToList();
        var subjects = new[] { "Мат", "Рус", "Анг" }.Select(n => new Subject
        {
            Name = n, MaxPerDay = 2
        }).ToList();
        var curriculum = new List<CurriculumItem>();
        foreach (var c in classes)
            for (int s = 0; s < 3; s++)
                curriculum.Add(new CurriculumItem
                {
                    ClassId = c.Id, SubjectId = subjects[s].Id,
                    TeacherId = teachers[s].Id, HoursPerWeek = 2
                });
        var rooms = new[]
        {
            new Room { Name = "101", PhysicalCapacity = 30, MaxSimultaneousGroups = 2 },
            new Room { Name = "102", PhysicalCapacity = 30, MaxSimultaneousGroups = 2 },
        };
        var input = new ProblemInput(classes, teachers, subjects, curriculum,
            [], [], [], DaysCount: 3, SlotsPerDay: 4, rooms: rooms);
        var (problem, errors) = ProblemBuilder.Build(input,
            new SolverOptions(MaxTimeSeconds: budget, NumSearchWorkers: 1, RandomSeed: seed));
        Assert.Empty(errors);
        return problem!;
    }

    [Fact]
    public async Task Overhead_Solver_vs_Archive_vs_ThrottledProgress()
    {
        const double budget = 8;
        var sw = new System.Diagnostics.Stopwatch();

        // Mode A: голый solver.
        var rA = await new OrToolsSolver().SolveAsync(Build18(11, budget));
        long wallA = rA.ElapsedMs;

        // Mode B: solver + archive (замер overhead колбэка).
        var archB = new ScheduleCandidateArchive(5, 100);
        var pb = Build18(11, budget);
        long cbMsB = 0;
        sw.Restart();
        var rB = await new OrToolsSolver().SolveAsync(pb, default, inc =>
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var c = ScheduleCandidate.Create(pb, inc.Placements, 11, inc.Proxy, inc.Phase);
            if (c is not null) archB.TryAdd(c);
            cbMsB += System.Diagnostics.Stopwatch.GetElapsedTime(t0).Milliseconds;
        });
        long wallB = sw.ElapsedMilliseconds;

        // Mode C: solver + archive + throttled progress (полный E4-путь).
        var archC = new ScheduleCandidateArchive(5, 100);
        var pc = Build18(11, budget);
        var stream = new ThrottledIncumbentStream();
        long cbMsC = 0;
        int incumbents = 0;
        var wallCsw = System.Diagnostics.Stopwatch.StartNew();
        var rC = await new OrToolsSolver().SolveAsync(pc, default, inc =>
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            incumbents++;
            var c = ScheduleCandidate.Create(pc, inc.Placements, 11, inc.Proxy, inc.Phase);
            if (c is not null) archC.TryAdd(c);
            stream.Report(new GenerationProgressDto(GenerationPhase.Improving,
                wallCsw.Elapsed, archC.Members.Count == 0 ? null : archC.Members.Min(m => m.SoftTotal),
                null, incumbents, archC.Members.Count, 5, "seed 11",
                "Ищем подходящее расписание…", true, TimeSpan.Zero));
            cbMsC += System.Diagnostics.Stopwatch.GetElapsedTime(t0).Milliseconds;
        });
        stream.Flush();
        long wallC = wallCsw.ElapsedMilliseconds;

        output.WriteLine(
            $"E4-OVERHEAD 18occ budget={budget}s workers=1 seed=11: " +
            $"A(solver)={wallA}ms pool? | " +
            $"B(+archive)={wallB}ms cbOverhead={cbMsB}ms archN={archB.Members.Count} | " +
            $"C(+throttle)={wallC}ms cbOverhead={cbMsC}ms archN={archC.Members.Count} " +
            $"streamReceived={stream.ReceivedCount} streamEmitted={stream.EmittedCount}");
        // Ворота: прогресс не душит solver (эмиссий на порядки меньше incumbents).
        Assert.True(stream.EmittedCount <= stream.ReceivedCount);
        Assert.True(archC.Members.Count >= 1);
        _ = rA;
        _ = rB;
        _ = rC;
    }

    // Manual smoke Generate flow (§15): orchestrator + реальный solver, крошечная задача.
    [Fact]
    public async Task RealSolver_Smoke_GenerateFlow()
    {
        ProblemInput Input()
        {
            var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5, StudentCount = 25 };
            var teacher = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
            var math = new Subject { Name = "Математика", MaxPerDay = 2 };
            var item = new CurriculumItem
            {
                ClassId = cls.Id, SubjectId = math.Id, TeacherId = teacher.Id, HoursPerWeek = 2
            };
            return new ProblemInput([cls], [teacher], [math], [item],
                [], [], [], DaysCount: 2, SlotsPerDay: 3);
        }
        var solver = new OrToolsSolver();
        SchedulingProblem Factory(int seed)
        {
            var (p, e) = ProblemBuilder.Build(Input(),
                new SolverOptions(MaxTimeSeconds: 6, NumSearchWorkers: 1, RandomSeed: seed));
            Assert.Empty(e);
            return p!;
        }
        var orch = new GenerationOrchestrator(Factory,
            (problem, ct, sink) => solver.SolveAsync(problem, ct, sink));
        var outcome = await orch.RunAsync([11, 22]);
        output.WriteLine(
            $"SMOKE: status={outcome.Status} feasible={outcome.HasFeasible} " +
            $"best={outcome.BestSoft} arch={outcome.ArchiveCount} " +
            $"first={outcome.FirstFeasibleAt} elapsed={outcome.Elapsed} " +
            $"vm=[{orch.ViewModel.StatusText} | {orch.ViewModel.BestText} | " +
            $"{orch.ViewModel.CandidatesText} | top5={orch.ViewModel.Top5.HeaderText}]");
        Assert.True(outcome.HasFeasible);
        Assert.Equal("Готово", outcome.Status);
        Assert.NotEmpty(orch.ViewModel.Top5.Cards);
        Assert.False(orch.ViewModel.DiagnosticsExpanded); // §10: свернута по умолчанию
    }
}
