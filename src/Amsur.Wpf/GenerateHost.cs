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
        var mode = session.GenerateMode;
        var solver = new OrToolsSolver();
        var rs = session.QualityRules;
        StreamingRun run = (problem, ct, sink) => solver.SolveAsync(problem, ct, sink, rules: rs);

        SchedulingProblem Factory(int seed)
        {
            var (problem, errors) = ProblemBuilder.Build(input,
                new SolverOptions(
                    MaxTimeSeconds: mode.BudgetSeconds,
                    NumSearchWorkers: 1,
                    RandomSeed: seed));
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
        vm.QualityProfileText = $"Профиль: {QualityHints.ProfileName(rs.ProfileName)} · Режим: {modeName}";
        var view = new Views.GenerateView(vm, orchestrator, session);
        return (view, orchestrator);
    }
}
