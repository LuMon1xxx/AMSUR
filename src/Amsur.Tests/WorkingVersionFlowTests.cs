using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;
using Amsur.Scheduling.OrTools;
using Amsur.Wpf;
using ClosedXML.Excel;

namespace Amsur.Tests;

// Сквозной цикл рабочей версии на реальном solver: шаблон → импорт → генерация →
// Top-5 → приёмка → правка → выгрузка. Маленькая школа, короткие бюджеты.
public sealed class WorkingVersionFlowTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"amsur-e2e-{Guid.NewGuid():N}");

    public ValueTask DisposeAsync()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task FullLoop_Import_Generate_Accept_Edit_Export()
    {
        Directory.CreateDirectory(_dir);
        var session = new AppSession(_dir);
        await session.InitAsync();
        Assert.False(session.HasData);

        // 1. Шаблон + заполнение + импорт (как пользователь через MainWindow).
        using (var tpl = new MemoryStream())
        {
            SchoolDataImporter.ExportTemplate(tpl);
            Assert.Empty(ExcelLoadExchange.ImportLoad(new MemoryStream(tpl.ToArray())));
        }
        var rows = new List<LoadRow>
        {
            new("5А", "Мат", 2, "Иванов", false, null, null),
            new("5А", "Рус", 1, "Петрова", false, null, null),
            new("5Б", "Мат", 2, "Иванов", false, null, null),
        };
        using (var ms = new MemoryStream())
        {
            ExcelLoadExchange.ExportLoad(ms, rows);
            rows = ExcelLoadExchange.ImportLoad(new MemoryStream(ms.ToArray())).ToList();
        }
        await session.ImportLoadAsync(rows, days: 3, slots: 4);
        Assert.True(session.HasData);

        // 2. Генерация реальным solver (короткий бюджет, 1 seed).
        var solver = new OrToolsSolver();
        var problem = session.BuildProblem();
        StreamingRun run = (p, ct, sink) => solver.SolveAsync(p, ct, sink);
        var orch = new GenerationOrchestrator(
            seed => session.BuildProblem(), run,
            acceptor: new AcceptScheduleService(session.Store));
        var outcome = await orch.RunAsync([11]);
        Assert.True(outcome.HasFeasible);
        Assert.NotEmpty(orch.ViewModel.Top5.Cards);
        Assert.True(orch.History.Count >= 1);

        // 3. Приёмка лучшего.
        var best = orch.ViewModel.Top5.Best!;
        var accepted = await orch.AcceptAsync(best);
        Assert.True(accepted.Succeeded);
        var active = await session.GetActiveAsync();
        Assert.NotNull(active);

        // 4. Правка: ищем разрешённый ход превью (без solver) и коммитим.
        // D-28: ход, рвущий компактность старого дня, запрещён — ищем ход с конца блока.
        bool committed = false;
        foreach (var occ2 in active!.Placements.OrderByDescending(x => x.SlotIndex))
        {
            foreach (int slot in new[] { 1, 2, 3, 4 })
            {
                var preview = session.EditService.Preview(problem, active.Placements,
                    new CandidateMove(occ2.OccurrenceId, DayIndex: 2, SlotIndex: slot, RoomId: occ2.RoomId));
                if (!preview.CanCommit) continue;
                var done = await session.EditService.CommitAsync(
                    session.AcademicYearId, problem, preview);
                Assert.True(done.Committed);
                committed = true;
                break;
            }
            if (committed) break;
        }
        Assert.True(committed, "Нет ни одного разрешённого хода — редактор заклинило");

        // 5. Выгрузка активного в Excel + проверка содержимого.
        string xlsx = Path.Combine(_dir, "расписание.xlsx");
        await session.ExportActiveAsync(xlsx);
        Assert.True(new FileInfo(xlsx).Length > 0);
        using var wb = new XLWorkbook(xlsx);
        var flat = string.Join("\n", wb.Worksheet("Расписание").RangeUsed().Cells()
            .Select(c => c.GetString()));
        Assert.Contains("Класс 5А", flat);
        Assert.Contains("Класс 5Б", flat);
    }
}
