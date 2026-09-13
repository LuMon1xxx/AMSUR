using Amsur.Application;
using Amsur.Wpf;

namespace Amsur.Tests;

// P3: ручной ввод — строки переживают перезапуск (SQLite), правки держат год,
// битые правки не сносят старые данные (fail-loud).
public sealed class ManualDataTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"amsur-manual-{Guid.NewGuid():N}");

    public ValueTask DisposeAsync()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        return ValueTask.CompletedTask;
    }

    private static List<LoadRow> TinyRows() =>
    [
        new("5А", "Мат", 2, "Иванов", false, null, "101"),
        new("5А", "Рус", 1, "Петрова", true, "Сидорова", null),
    ];

    // 1. Импорт сохраняется; новая сессия на том же каталоге восстанавливает всё.
    [Fact]
    public async Task Import_Persists_And_Restores()
    {
        Directory.CreateDirectory(_dir);
        var s1 = new AppSession(_dir);
        await s1.InitAsync();
        await s1.ImportLoadAsync(TinyRows(), days: 3, slots: 4);
        Assert.True(s1.HasData);
        var year = s1.AcademicYearId;

        var s2 = new AppSession(_dir);
        await s2.InitAsync();
        Assert.True(s2.HasData);
        Assert.Equal(year, s2.AcademicYearId);
        Assert.Equal(3, s2.Data!.DaysCount);
        Assert.Equal(4, s2.Data.SlotsPerDay);
        Assert.Equal(2, s2.Data.Curriculum.Count);
        Assert.Equal(2, s2.LoadRows.Count);
        Assert.Equal("excel", s2.DataSource);
        // Сплит-строка пережила roundtrip целиком.
        var split = s2.LoadRows.Single(r => r.SplitSubgroups);
        Assert.Equal("Сидорова", split.SplitTeacherBName);
        Assert.Equal("101", s2.LoadRows.First(r => r.RoomName == "101").RoomName);
        // Восстановленные данные строятся в задачу.
        Assert.NotNull(s2.BuildProblem());
    }

    // 2. Ручная правка держит учебный год и видна после перезапуска.
    [Fact]
    public async Task ManualEdit_KeepsYear_And_Persists()
    {
        Directory.CreateDirectory(_dir);
        var s1 = new AppSession(_dir);
        await s1.InitAsync();
        await s1.ImportLoadAsync(TinyRows(), days: 3, slots: 4);
        var year = s1.AcademicYearId;

        var edited = s1.LoadRows.Concat([new LoadRow("5Б", "Мат", 2, "Иванов", false, null, "102")]).ToList();
        await s1.SetManualRowsAsync(edited, days: 3, slots: 4);
        Assert.Equal(year, s1.AcademicYearId);
        Assert.Equal("manual", s1.DataSource);
        Assert.Equal(3, s1.Data!.Curriculum.Count);

        var s2 = new AppSession(_dir);
        await s2.InitAsync();
        Assert.Equal(year, s2.AcademicYearId);
        Assert.Equal(3, s2.LoadRows.Count);
        Assert.True(s2.LoadRows.Any(r => r.ClassName == "5Б" && r.RoomName == "102"));
    }

    // 3. Пустые строки — fail-loud, старые данные целы.
    [Fact]
    public async Task ManualEdit_Invalid_KeepsOldData()
    {
        Directory.CreateDirectory(_dir);
        var s1 = new AppSession(_dir);
        await s1.InitAsync();
        await s1.ImportLoadAsync(TinyRows(), days: 3, slots: 4);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s1.SetManualRowsAsync([], days: 3, slots: 4));
        Assert.True(s1.HasData);
        Assert.Equal(2, s1.Data!.Curriculum.Count);
        Assert.Equal(2, s1.LoadRows.Count);
    }

    // 4. Смена сетки (дни/уроки) — тоже ручная правка, строки те же.
    [Fact]
    public async Task ManualEdit_GridChange_Persists()
    {
        Directory.CreateDirectory(_dir);
        var s1 = new AppSession(_dir);
        await s1.InitAsync();
        await s1.ImportLoadAsync(TinyRows(), days: 3, slots: 4);

        await s1.SetManualRowsAsync(s1.LoadRows.ToList(), days: 5, slots: 7);
        Assert.Equal(5, s1.Data!.DaysCount);
        Assert.Equal(7, s1.Data.SlotsPerDay);

        var s2 = new AppSession(_dir);
        await s2.InitAsync();
        Assert.Equal(5, s2.Data!.DaysCount);
        Assert.Equal(7, s2.Data.SlotsPerDay);
        Assert.Equal(2, s2.LoadRows.Count);
    }
}
