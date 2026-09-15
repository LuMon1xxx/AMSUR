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

    public static List<LoadRow> DemoRows() =>
    [
        new("6А", "Математика", 4, "Иванова", false, null, "Каб 6А"),
        new("6А", "Русский язык", 4, "Петрова", false, null, "Каб 6А"),
        new("6А", "Литература", 2, "Петрова", false, null, "Каб 6А"),
        new("6А", "Английский язык", 3, "Орлова", false, null, "Линг"),
        new("6А", "История", 2, "Кузнецова", false, null, "Каб 6А"),
        new("6А", "Физкультура", 2, "Смирнов", false, null, "Спортзал"),
        new("6А", "Информатика", 1, "Сидоров", false, null, "ИТ"),
        new("6А", "Биология", 1, "Козлов", false, null, "Каб 6А"),
        new("6А", "География", 1, "Козлов", false, null, "Каб 6А"),

        new("6Б", "Математика", 4, "Иванова", false, null, "Каб 6Б"),
        new("6Б", "Русский язык", 4, "Петрова", false, null, "Каб 6Б"),
        new("6Б", "Литература", 2, "Орлова", false, null, "Каб 6Б"),
        new("6Б", "Английский язык", 3, "Орлова", false, null, "Линг"),
        new("6Б", "История", 2, "Кузнецова", false, null, "Каб 6Б"),
        new("6Б", "Физкультура", 2, "Смирнов", false, null, "Спортзал"),
        new("6Б", "Информатика", 1, "Сидоров", false, null, "ИТ"),
        new("6Б", "Биология", 1, "Козлов", false, null, "Каб 6Б"),
        new("6Б", "География", 1, "Козлов", false, null, "Каб 6Б"),

        new("7А", "Математика", 4, "Соколова", false, null, "Каб 7А"),
        new("7А", "Русский язык", 4, "Фёдорова", false, null, "Каб 7А"),
        new("7А", "Литература", 2, "Михайлова", false, null, "Каб 7А"),
        new("7А", "Английский язык", 3, "Михайлова", false, null, "Линг"),
        new("7А", "История", 2, "Кузнецова", false, null, "Каб 7А"),
        new("7А", "Физкультура", 2, "Смирнов", false, null, "Спортзал"),
        new("7А", "Физика", 2, "Сидоров", false, null, "Лаб1"),
        new("7А", "Биология", 1, "Козлов", false, null, "Каб 7А"),
        new("7А", "География", 1, "Козлов", false, null, "Каб 7А"),

        new("7Б", "Математика", 4, "Соколова", false, null, "Каб 7Б"),
        new("7Б", "Русский язык", 4, "Фёдорова", false, null, "Каб 7Б"),
        new("7Б", "Литература", 2, "Михайлова", false, null, "Каб 7Б"),
        new("7Б", "Английский язык", 3, "Михайлова", false, null, "Линг"),
        new("7Б", "История", 2, "Кузнецова", false, null, "Каб 7Б"),
        new("7Б", "Физкультура", 2, "Смирнов", false, null, "Спортзал"),
        new("7Б", "Физика", 2, "Сидоров", false, null, "Лаб1"),
        new("7Б", "Биология", 1, "Козлов", false, null, "Каб 7Б"),
        new("7Б", "География", 1, "Козлов", false, null, "Каб 7Б"),

        new("8А", "Математика", 4, "Иванова", false, null, "Каб 8А"),
        new("8А", "Русский язык", 4, "Фёдорова", false, null, "Каб 8А"),
        new("8А", "Литература", 2, "Орлова", false, null, "Каб 8А"),
        new("8А", "Английский язык", 3, "Михайлова", false, null, "Линг"),
        new("8А", "История", 2, "Кузнецова", false, null, "Каб 8А"),
        new("8А", "Физкультура", 2, "Смирнов", false, null, "Спортзал"),
        new("8А", "Физика", 2, "Сидоров", false, null, "Лаб1"),
        new("8А", "Информатика", 1, "Сидоров", false, null, "ИТ"),
        new("8А", "Химия", 2, "Козлов", false, null, "Лаб1"),
        new("8А", "Обществознание", 1, "Кузнецова", false, null, "Каб 8А"),

        new("8Б", "Математика", 4, "Иванова", false, null, "Каб 8Б"),
        new("8Б", "Русский язык", 4, "Петрова", false, null, "Каб 8Б"),
        new("8Б", "Литература", 2, "Орлова", false, null, "Каб 8Б"),
        new("8Б", "Английский язык", 3, "Орлова", true, "Петрова", "Линг"),
        new("8Б", "История", 2, "Кузнецова", false, null, "Каб 8Б"),
        new("8Б", "Физкультура", 2, "Смирнов", false, null, "Спортзал"),
        new("8Б", "Физика", 2, "Сидоров", false, null, "Лаб1"),
        new("8Б", "Информатика", 1, "Сидоров", false, null, "ИТ"),
        new("8Б", "Химия", 2, "Козлов", false, null, "Лаб1"),
        new("8Б", "Обществознание", 1, "Кузнецова", false, null, "Каб 8Б"),
    ];

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
