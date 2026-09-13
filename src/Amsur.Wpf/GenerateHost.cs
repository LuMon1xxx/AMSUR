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
    public static (GenerateWindow Window, GenerationOrchestrator Orchestrator) Create(
        ProblemInput input,
        double perSeedBudgetSeconds = 12,
        int numWorkers = 1,
        string? dbPath = null,
        EffectiveRuleSet? rules = null,
        string modeName = "Стандарт")
    {
        var solver = new OrToolsSolver();
        var rs = rules ?? EffectiveRuleSet.Default;
        StreamingRun run = (problem, ct, sink) => solver.SolveAsync(problem, ct, sink, rules: rs);

        SchedulingProblem Factory(int seed)
        {
            var (problem, errors) = ProblemBuilder.Build(input,
                new SolverOptions(
                    MaxTimeSeconds: perSeedBudgetSeconds,
                    NumSearchWorkers: numWorkers,
                    RandomSeed: seed));
            if (problem is null)
                throw new InvalidOperationException(
                    "Problem build failed: " + string.Join("; ", errors));
            return problem;
        }

        var stream = new ThrottledIncumbentStream();
        var vm = new GenerateViewModel();
        AcceptScheduleService? acceptor = null;
        if (dbPath is not null)
        {
            var store = new Infrastructure.SqliteScheduleStore($"Data Source={dbPath}");
            store.InitializeAsync().GetAwaiter().GetResult();
            acceptor = new AcceptScheduleService(store);
        }
        var orchestrator = new GenerationOrchestrator(Factory, run, stream, vm, acceptor: acceptor);
        orchestrator.Rules = rs;
        vm.QualityProfileText = $"Профиль: {QualityHints.ProfileName(rs.ProfileName)} · Режим: {modeName}";
        var window = new GenerateWindow(vm);
        return (window, orchestrator);
    }
}
