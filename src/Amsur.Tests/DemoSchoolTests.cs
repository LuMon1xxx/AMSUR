using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;
using Amsur.Scheduling.OrTools;
using Amsur.Wpf;

namespace Amsur.Tests;

// Демо-школа из DemoSchool.xlsx: 6 классов, 10 учителей, 10 кабинетов (6 домашних + 4 спец), 56 строк.
// Сбалансирована под STANDARD 12с/1worker: макс нагрузка учителя 16-17 occ (3.2-3.4/день),
// кабинеты 10 lanes на 131 occ (37% утилизация) — Feasible за ~5с (проверено diag5).
// 1 A/B split (8Б Английский) для витрины подгрупп.
// Доказывает, что демо-набор строится и решается (пользователь гоняет его вживую).
public sealed class DemoSchoolTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"amsur-demo-{Guid.NewGuid():N}");

    public ValueTask DisposeAsync()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        return ValueTask.CompletedTask;
    }

    public static List<LoadRow> DemoRows() => DemoSchoolData.Rows();

    [Fact]
    public void DemoSchool_HasExpectedShape()
    {
        var rows = DemoRows();
        Assert.Equal(56, rows.Count);
        Assert.Equal(6, rows.Select(r => r.ClassName).Distinct().Count());
        Assert.Equal(10, rows.Select(r => r.TeacherName).Distinct().Count());
        Assert.Equal(10, rows.Select(r => r.RoomName).Where(s => s is not null).Distinct().Count());
        Assert.Single(rows, r => r.SplitSubgroups);
    }

    // Прод-путь демо-набора (как кнопка «Сгенерировать» на Стандарте):
    // импорт 5×7 → оркестратор по сидам → приёмка лучшего → validator чист.
    [Fact]
    public async Task DemoSchool_StandardRun_ProducesSchedule()
    {
        Directory.CreateDirectory(_dir);
        var session = new AppSession(_dir);
        await session.InitAsync();
        await session.ImportLoadAsync(DemoRows(), days: 5, slots: 7);
        Assert.True(session.HasData);

        var solver = new OrToolsSolver();
        StreamingRun run = (p, ct, sink) => solver.SolveAsync(p, ct, sink);
        var orch = new GenerationOrchestrator(
            _ => session.BuildProblem(), run,
            acceptor: new AcceptScheduleService(session.Store));
        var outcome = await orch.RunAsync([11, 22, 33]);
        Assert.True(outcome.HasFeasible);

        var best = orch.ViewModel.Top5.Best!;
        var accepted = await orch.AcceptAsync(best);
        Assert.True(accepted.Succeeded);

        var active = await session.GetActiveAsync();
        Assert.NotNull(active);
        var problem = session.BuildProblem();
        Assert.True(PlacementValidator.Validate(problem, active!.Placements).IsValid);
    }
}
