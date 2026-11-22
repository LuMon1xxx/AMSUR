using Amsur.Application;
using Amsur.Domain;
using Amsur.Infrastructure;

namespace Amsur.Tests;

// D-28: дневные лимиты классов/учителей должны переживать перезапуск.
// Раньше жили только в сессии (SettingsView.ApplyLimits), формат FlexDataset их не знал.
public sealed class LimitPersistTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "amsur-limits-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* темп-мусор */ }
    }

    [Fact]
    public void Import_ClassAndTeacherLimits_FromFlex()
    {
        var rows = new List<LoadRow>
        {
            new("9Б", "Математика", 7, "Иванова", false, null, null),
        };
        var flex = FlexDataset.Empty with
        {
            ClassLimits = [new ClassLimitRow("9Б", 4)],
            TeacherLimits = [new TeacherLimitRow("Иванова", 5)],
        };
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows, flex: flex);
        Assert.Equal(4, data.Classes.Single(c => c.Name == "9Б").MaxLessonsPerDay);
        Assert.Equal(5, data.Teachers.Single(t => t.Name == "Иванова").MaxLessonsPerDay);
    }

    [Fact]
    public void Import_NoLimits_KeepsSchoolDefaults()
    {
        var rows = new List<LoadRow>
        {
            new("9Б", "Математика", 7, "Иванова", false, null, null),
        };
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows, flex: FlexDataset.Empty);
        Assert.Equal(SchoolClass.DefaultMaxLessonsPerDay,
            data.Classes.Single(c => c.Name == "9Б").MaxLessonsPerDay);
        Assert.Equal(Teacher.DefaultMaxLessonsPerDay,
            data.Teachers.Single(t => t.Name == "Иванова").MaxLessonsPerDay);
    }

    [Theory]
    [InlineData(0, 7)]   // вне диапазона 1..10 → школьный дефолт
    [InlineData(11, 7)]
    [InlineData(1, 1)]
    [InlineData(10, 10)]
    public void Import_ClassLimitOutOfRange_FallsBack(int stored, int expected)
    {
        var rows = new List<LoadRow>
        {
            new("9Б", "Математика", 7, "Иванова", false, null, null),
        };
        var flex = FlexDataset.Empty with { ClassLimits = [new ClassLimitRow("9Б", stored)] };
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows, flex: flex);
        Assert.Equal(expected, data.Classes.Single(c => c.Name == "9Б").MaxLessonsPerDay);
    }

    [Theory]
    [InlineData(0, 6)]
    [InlineData(13, 6)]
    [InlineData(12, 12)]
    public void Import_TeacherLimitOutOfRange_FallsBack(int stored, int expected)
    {
        var rows = new List<LoadRow>
        {
            new("9Б", "Математика", 7, "Иванова", false, null, null),
        };
        var flex = FlexDataset.Empty with { TeacherLimits = [new TeacherLimitRow("Иванова", stored)] };
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows, flex: flex);
        Assert.Equal(expected, data.Teachers.Single(t => t.Name == "Иванова").MaxLessonsPerDay);
    }

    [Fact]
    public async Task FlexStore_Limits_PersistAcrossReopen()
    {
        Directory.CreateDirectory(_dir);
        string db = $"Data Source={Path.Combine(_dir, "limits.db")}";
        var src = FlexDataset.Empty with
        {
            Classes = [new ClassConfigRow("9Б", "Иванова", 9)],
            ClassLimits = [new ClassLimitRow("9Б", 4)],
            TeacherLimits = [new TeacherLimitRow("Иванова", 5)],
        };
        var store = new SqliteFlexStore(db);
        await store.InitializeAsync();
        await store.SaveAsync(src);

        // Новый экземпляр = новый процесс: проверяем именно персист, а не кэш.
        var reopened = new SqliteFlexStore(db);
        await reopened.InitializeAsync();
        var got = await reopened.LoadAsync();
        Assert.Equal(4, got.ClassLimits!.Single(l => l.ClassName == "9Б").MaxLessonsPerDay);
        Assert.Equal(5, got.TeacherLimits!.Single(l => l.TeacherName == "Иванова").MaxLessonsPerDay);
    }

    [Fact]
    public async Task FlexStore_NoLimits_LoadsEmpty()
    {
        Directory.CreateDirectory(_dir);
        var store = new SqliteFlexStore($"Data Source={Path.Combine(_dir, "empty.db")}");
        await store.InitializeAsync();
        await store.SaveAsync(FlexDataset.Empty);
        var got = await store.LoadAsync();
        Assert.Empty(got.ClassLimits!);
        Assert.Empty(got.TeacherLimits!);
    }

    [Fact]
    public async Task FlexStore_Overwrite_ReplacesLimits()
    {
        Directory.CreateDirectory(_dir);
        string db = $"Data Source={Path.Combine(_dir, "overwrite.db")}";
        var store = new SqliteFlexStore(db);
        await store.InitializeAsync();
        await store.SaveAsync(FlexDataset.Empty with
        {
            ClassLimits = [new ClassLimitRow("9Б", 4)],
            TeacherLimits = [new TeacherLimitRow("Иванова", 5)],
        });
        await store.SaveAsync(FlexDataset.Empty with
        {
            ClassLimits = [new ClassLimitRow("9Б", 6)],
        });
        var got = await store.LoadAsync();
        Assert.Equal(6, got.ClassLimits!.Single().MaxLessonsPerDay);
        Assert.Empty(got.TeacherLimits!);
    }
}
