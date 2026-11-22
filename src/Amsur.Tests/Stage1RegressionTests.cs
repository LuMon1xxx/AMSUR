using System.IO;
using Amsur.Application;
using Amsur.Scheduling.Core;
using Amsur.Wpf;

namespace Amsur.Tests;

// Регресс-тесты Stage 1: синхронизация оценки после принятия Rank 2..5,
// консистентность UiFormat.ModeDisplay и CSV-экспорт активного расписания.
public sealed class Stage1RegressionTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"amsur-s1-{Guid.NewGuid():N}");

    public ValueTask DisposeAsync()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void ModeDisplay_AllGenerateModesHaveUniqueNonEmptyMeta()
    {
        var seenTitles = new HashSet<string>();
        var recModes = new List<string>();
        string standardGenTime = UiFormat.ModeDisplay("STANDARD").GenerateTimeLabel;

        foreach (var mode in GenerateModes.All)
        {
            var ui = UiFormat.ModeDisplay(mode.Code);
            Assert.False(string.IsNullOrWhiteSpace(ui.Title));
            Assert.False(string.IsNullOrWhiteSpace(ui.TimeLabel));
            Assert.False(string.IsNullOrWhiteSpace(ui.Hint));
            Assert.False(string.IsNullOrWhiteSpace(ui.GenerateTimeLabel));
            Assert.True(seenTitles.Add(ui.Title), $"Duplicate Title for mode {mode.Code}: {ui.Title}");
            if (ui.IsRecommended) recModes.Add(mode.Code);
        }

        Assert.Equal(["STANDARD"], recModes);
        Assert.NotEqual(standardGenTime, UiFormat.ModeDisplay("EXPERT").GenerateTimeLabel);
    }

    [Fact]
    public async Task AcceptRank2_UpdatesSessionLastQualityAndLines()
    {
        Directory.CreateDirectory(_dir);
        var session = new AppSession(_dir);
        await session.InitAsync();
        await session.ImportLoadAsync(DemoSchoolTests.DemoRows(), days: 5, slots: 7);
        session.SetGenerateMode("QUICK");

        var (vm, orch) = GenerateHost.CreateOrchestrator(
            session.Data!.ToProblemInput(), session, session.GenerateMode.Name);
        var outcome = await orch.RunAsync([11]);
        Assert.True(outcome.HasFeasible);
        Assert.NotEmpty(vm.Top5.Cards);

        var picked = vm.Top5.Cards.Count >= 2 ? vm.Top5.Cards[1] : vm.Top5.Cards[0];
        var accepted = await orch.AcceptAsync(picked);
        Assert.True(accepted.Succeeded);

        var expected = QualityRating.FromBreakdown(picked.Candidate.Breakdown);
        Assert.NotNull(session.LastQuality);
        Assert.Equal(expected.Label, session.LastQuality!.Label);
        Assert.Equal(expected.Improvements, session.LastQuality.Improvements);
        Assert.NotNull(session.LastQualityLines);
        Assert.NotEmpty(session.LastQualityLines!);
    }

    [Fact]
    public async Task ExportActiveCsvAsync_WritesSemicolonCsvForValidActiveSchedule()
    {
        Directory.CreateDirectory(_dir);
        var session = new AppSession(_dir);
        await session.InitAsync();
        await session.ImportLoadAsync(DemoSchoolTests.DemoRows(), days: 5, slots: 7);
        session.SetGenerateMode("QUICK");

        var (vm, orch) = GenerateHost.CreateOrchestrator(
            session.Data!.ToProblemInput(), session, session.GenerateMode.Name);
        var outcome = await orch.RunAsync([11]);
        Assert.True(outcome.HasFeasible);
        Assert.NotNull(vm.Top5.Best);

        var accepted = await orch.AcceptAsync(vm.Top5.Best);
        Assert.True(accepted.Succeeded);

        string csvPath = Path.Combine(_dir, "schedule.csv");
        await session.ExportActiveCsvAsync(csvPath);

        Assert.True(File.Exists(csvPath));
        string[] lines = await File.ReadAllLinesAsync(csvPath);
        Assert.True(lines.Length > 10, $"Expected >10 CSV rows, got {lines.Length}");
        Assert.Contains(";", lines[0]);
        Assert.Contains(";", lines[1]);
    }
}
