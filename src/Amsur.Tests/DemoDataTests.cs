using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;
using Amsur.Wpf;

namespace Amsur.Tests;

// Демо-набор как данные продукта (22.09.2026): продукт и тесты обязаны
// использовать один источник — DemoSchoolData.Rows(). Без solver (быстро).
public sealed class DemoDataTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"amsur-demodata-{Guid.NewGuid():N}");

    public ValueTask DisposeAsync()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void DemoSchoolData_HasExpectedShape()
    {
        var rows = DemoSchoolData.Rows();
        Assert.Equal(56, rows.Count);
        Assert.Equal(6, rows.Select(r => r.ClassName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Single(rows, r => r.SplitSubgroups);
        Assert.Equal(DemoSchoolData.Days, 5);
        Assert.Equal(DemoSchoolData.Slots, 7);
    }

    [Fact]
    public void DemoSchoolData_BuildsProblem()
    {
        var data = SchoolDataImporter.Import(Guid.NewGuid(), DemoSchoolData.Rows(),
            daysCount: DemoSchoolData.Days, slotsPerDay: DemoSchoolData.Slots);
        var (problem, errors) = ProblemBuilder.Build(data.ToProblemInput());
        Assert.Empty(errors);
        Assert.NotNull(problem);
        Assert.Equal(131, problem!.Occurrences.Count);
    }

    [Fact]
    public async Task ImportDemoAsync_LoadsSession()
    {
        Directory.CreateDirectory(_dir);
        var session = new AppSession(_dir);
        await session.InitAsync();
        await session.ImportDemoAsync();
        Assert.True(session.HasData);
        Assert.Equal("demo", session.DataSource);
        Assert.Equal(6, session.Summary!.Classes);
        var problem = session.BuildProblem();
        Assert.Equal(131, problem.Occurrences.Count);
    }

    [Fact]
    public void DemoRows_EqualsProductSource()
    {
        // Паритет: тестовый хелпер делегирует продуктовому источнику.
        Assert.Equal(DemoSchoolData.Rows(), DemoSchoolTests.DemoRows());
    }
}
