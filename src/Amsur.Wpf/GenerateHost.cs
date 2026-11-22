using Amsur.Application;
using Amsur.Scheduling.Core;
using Amsur.Scheduling.OrTools;

namespace Amsur.Wpf;

// Композиция (§11, D-19): единственный, кто знает про OrTools.
// Application получает только делегат StreamingRun (без CP-SAT типов в сигнатуре UI).
public static class GenerateHost
{
    // E7: dbPath == null — без персиста (Accept честно откажет); иначе SQLite-файл.
    // rules == null — STANDARD (профиль выбирается в MainWindow до генерации).
    // P-D4: старый Create(Window) удалён — не осталось вызывающих.
    // Композиция для однооконной навигации (view вместо Window).
    public static (Views.GenerateView View, GenerationOrchestrator Orchestrator) CreateView(
        ProblemInput input,
        AppSession session,
        string modeName = "Стандарт")
    {
        var (vm, orchestrator) = CreateOrchestrator(input, session, modeName);
        var view = new Views.GenerateView(vm, orchestrator, session);
        return (view, orchestrator);
    }

    public static (GenerateViewModel ViewModel, GenerationOrchestrator Orchestrator) CreateOrchestrator(
        ProblemInput input,
        AppSession session,
        string modeName = "Стандарт")
    {
        var mode = session.GenerateMode;
        var solver = new OrToolsSolver();
        var rs = session.QualityRules;
        // Большая школа (≈900 уроков-позиций) не успевает за 12 с: на реальной
        // нагрузке СШ №8 чистый результат находится за ~40 с. Даём адаптивный
        // бюджет, иначе «Стандарт» честно, но бесполезно пишет «не найдено».
        double budget = input.Curriculum.Sum(c => c.HoursPerWeek) >= 600
            ? mode.Code switch
            {
                "QUICK" => 20,
                "STANDARD" => 30,
                "EXPERT" => 45,
                _ => Math.Max(60, mode.BudgetSeconds),
            }
            : mode.BudgetSeconds;
        // TOP: результат запуска перед архивом проходит grind-lite доводку
        // (~60с). Архив и Top-5 получают уже отполированное (валидация — там же).
        StreamingRun run = async (problem, ct, sink) =>
        {
            var result = await solver.SolveAsync(problem, ct, sink, rules: rs);
            if (!problem.Options.EnableGrindLite || result.Placements.Count == 0)
                return result;
            var polished = GrindLite.Polish(problem, result.Placements, rs,
                TimeSpan.FromSeconds(20), problem.Options.RandomSeed ?? 11, ct);
            var hard = PlacementValidator.Validate(problem, polished.Placements, rs).HardViolations.Count;
            // Полировка не должна ухудшать: если она сломала жёсткое правило,
            // возвращаем исходный валидный результат.
            if (hard > 0) return result;
            var bd = SoftEvaluator.Evaluate(problem, polished.Placements, rs);
            return result with
            {
                Placements = polished.Placements,
                ObjectiveValue = bd.Total,
                HardViolations = hard,
                Breakdown = bd.Components.ToDictionary(c => c.Code, c => c.Value),
                Diagnostics = [.. result.Diagnostics,
                    $"GrindLite: {string.Join(" | ", polished.Log)}"],
            };
        };

        SchedulingProblem Factory(int seed)
        {
            var (problem, errors) = ProblemBuilder.Build(input,
                new SolverOptions(
                    MaxTimeSeconds: budget,
                    NumSearchWorkers: 1,
                    RandomSeed: seed,
                    EnableGrindLite: mode.Code == "TOP"));
            if (problem is null)
                throw new InvalidOperationException(
                    "Problem build failed: " + string.Join("; ", errors));
            return problem;
        }

        var stream = new ThrottledIncumbentStream();
        var vm = new GenerateViewModel();
        AcceptScheduleService? acceptor = null;
        if (session.DbPath is not null)
        {
            var store = new Infrastructure.SqliteScheduleStore($"Data Source={session.DbPath}");
            store.InitializeAsync().GetAwaiter().GetResult();
            acceptor = new AcceptScheduleService(store);
        }
        var orchestrator = new GenerationOrchestrator(Factory, run, stream, vm, acceptor: acceptor);
        orchestrator.Rules = rs;
        orchestrator.CandidateAccepted += card =>
        {
            session.LastQuality = QualityRating.FromBreakdown(card.Candidate.Breakdown);
            session.LastQualityLines = orchestrator.LastProblem is { } problem
                ? QualityExplainer.Explain(problem, card.Candidate)
                : card.QualityLines.ToList();
        };
        vm.QualityProfileText = $"Профиль: {QualityHints.ProfileName(rs.ProfileName)} · Режим: {modeName}";
        return (vm, orchestrator);
    }
}
