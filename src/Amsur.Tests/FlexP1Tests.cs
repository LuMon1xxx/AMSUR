using Amsur.Application;
using Amsur.Domain;
using Amsur.Infrastructure;

namespace Amsur.Tests;

// P1 (R1–R9, D-34): Domain-поля, нормы часов, R7 fail-loud, FlexStore roundtrip.
// Без solver-логики и UI (P2/P3).
public sealed class FlexP1Tests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"amsur-flex-{Guid.NewGuid():N}");

    public ValueTask DisposeAsync()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        return ValueTask.CompletedTask;
    }

    // R2: Класс > Параллель > Предмет-дефолт > строка.
    [Fact]
    public void ResolveHours_Priority_ClassBeatsParallelBeatsDefault()
    {
        var norms = new List<HourNormRow>
        {
            new("Математика", 0, 4),  // дефолт предмета
            new("Математика", 9, 5),  // параллель
        };
        var overrides = new List<HourOverrideRow>
        {
            new("9Б", "Математика", 7),  // класс
        };
        Assert.Equal(7, HourResolution.ResolveHours("9Б", 9, "Математика", 4, norms, overrides));
        Assert.Equal(7, HourResolution.ResolveHours("9б", 9, "математика", 4, norms, overrides)); // регистр
        Assert.Equal(5, HourResolution.ResolveHours("9А", 9, "Математика", 4, norms, overrides));
        Assert.Equal(4, HourResolution.ResolveHours("8А", 8, "Математика", 4, norms, overrides));
        Assert.Equal(3, HourResolution.ResolveHours("8А", 8, "Физика", 3, norms, overrides)); // строка
    }

    [Fact]
    public void ResolveHours_NonPositiveNorm_Throws()
    {
        var norms = new List<HourNormRow> { new("Математика", 9, 0) };
        Assert.Throws<InvalidOperationException>(() =>
            HourResolution.ResolveHours("9А", 9, "Математика", 4, norms, []));
        var ov = new List<HourOverrideRow> { new("9Б", "Математика", -1) };
        Assert.Throws<InvalidOperationException>(() =>
            HourResolution.ResolveHours("9Б", 9, "Математика", 4, [], ov));
    }

    [Fact]
    public void ParseGrade_LeadingDigits()
    {
        Assert.Equal(9, HourResolution.ParseGrade("9Б"));
        Assert.Equal(11, HourResolution.ParseGrade("11А"));
        Assert.Equal(1, HourResolution.ParseGrade("1-А"));
        Assert.Equal(0, HourResolution.ParseGrade("Спортзал"));
        Assert.Equal(0, HourResolution.ParseGrade(""));
    }

    // R7: дефолт HardClass — дубли (класс,предмет) с разными учителями запрещены.
    [Fact]
    public void Import_R7HardClass_DuplicateTeacher_Throws()
    {
        var rows = new List<LoadRow>
        {
            new("9А", "Математика", 5, "Иванова", false, null, null),
            new("9А", "Математика", 5, "Петрова", false, null, null),
        };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SchoolDataImporter.Import(Guid.NewGuid(), rows));
        Assert.Contains("9А", ex.Message);
        Assert.Contains("Математика", ex.Message);
    }

    // R7: сплит A/B — штатно два учителя, под HardClass не попадает; DemoSchool импортируется.
    [Fact]
    public void Import_R7HardClass_SplitExempt_DemoOk()
    {
        var data = SchoolDataImporter.Import(Guid.NewGuid(), DemoSchoolTests.DemoRows());
        Assert.Equal(6, data.Classes.Count);
        // R2: параллель распарсена из имени.
        Assert.All(data.Classes, c => Assert.True(c.Grade is 6 or 7 or 8));
        Assert.Equal(8, data.Classes.Single(c => c.Name == "8Б").Grade);
    }

    [Fact]
    public void Import_R7Off_AllowsDuplicates()
    {
        var rows = new List<LoadRow>
        {
            new("9А", "Математика", 5, "Иванова", false, null, null),
            new("9А", "Математика", 5, "Петрова", false, null, null),
        };
        var flex = FlexDataset.Empty with
        {
            Settings = FlexSettingsRow.Default with { AssignMode = TeacherAssignMode.Off }
        };
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows, flex: flex);
        Assert.Equal(2, data.Curriculum.Count);
    }

    // R1/R5: конфигурация кабинета применяется к сущности.
    [Fact]
    public void Import_RoomConfig_Applied()
    {
        var rows = new List<LoadRow>
        {
            new("6А", "Физкультура", 2, "Смирнов", false, null, "Спортзал"),
            new("6А", "Химия", 1, "Козлов", false, null, "Конференц-зал"),
        };
        var flex = FlexDataset.Empty with
        {
            Rooms = new List<RoomConfigRow>
            {
                new("Спортзал", false, null, 4, 2, true),
                new("Конференц-зал", true, null, 1, 1, true),
            }
        };
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows, flex: flex);
        var gym = data.Rooms.Single(r => r.Name == "Спортзал");
        Assert.Equal(4, gym.MaxSimultaneousGroups);
        Assert.Equal(2, gym.DesiredGroups);
        Assert.False(gym.IsManualOnly);
        var conf = data.Rooms.Single(r => r.Name == "Конференц-зал");
        Assert.True(conf.IsManualOnly);
    }

    [Fact]
    public void Import_RoomConfig_BadDesired_Throws()
    {
        var rows = new List<LoadRow>
        {
            new("6А", "Физкультура", 2, "Смирнов", false, null, "Спортзал"),
        };
        var flex = FlexDataset.Empty with
        {
            Rooms = new List<RoomConfigRow> { new("Спортзал", false, null, 2, 3, true) }
        };
        Assert.Throws<InvalidOperationException>(() =>
            SchoolDataImporter.Import(Guid.NewGuid(), rows, flex: flex));
    }

    // R4: классрук из конфигурации.
    [Fact]
    public void Import_ClassConfig_TeacherAndGrade()
    {
        var rows = new List<LoadRow>
        {
            new("9Б", "Математика", 7, "Иванова", false, null, null),
        };
        var flex = FlexDataset.Empty with
        {
            Classes = new List<ClassConfigRow> { new("9Б", "Иванова", 9) }
        };
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows, flex: flex);
        var cls = data.Classes.Single();
        Assert.Equal(9, cls.Grade);
        var teacherId = data.Teachers.Single(t => t.Name == "Иванова").Id;
        Assert.Equal(teacherId, cls.ClassTeacherId);
    }

    // R2 end-to-end: норма параллели меняет часы строки.
    [Fact]
    public void Import_HourNorm_OverridesRowHours()
    {
        var rows = new List<LoadRow>
        {
            new("9А", "Математика", 4, "Иванова", false, null, null),
            new("9Б", "Математика", 4, "Иванова", false, null, null),
        };
        var flex = FlexDataset.Empty with
        {
            HourNorms = new List<HourNormRow> { new("Математика", 9, 5) },
            HourOverrides = new List<HourOverrideRow> { new("9Б", "Математика", 7) },
        };
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows, flex: flex);
        var byClass = data.Curriculum.ToDictionary(
            i => data.Classes.Single(c => c.Id == i.ClassId).Name);
        Assert.Equal(5, byClass["9А"].HoursPerWeek);
        Assert.Equal(7, byClass["9Б"].HoursPerWeek);
        Assert.Same(flex, data.Flex);
    }

    // Store: roundtrip всех таблиц.
    [Fact]
    public async Task FlexStore_Roundtrip()
    {
        Directory.CreateDirectory(_dir);
        var store = new SqliteFlexStore($"Data Source={Path.Combine(_dir, "flex.db")}");
        await store.InitializeAsync();
        var src = new FlexDataset(
            [new RoomConfigRow("Спортзал", false, null, 4, 2, true),
             new RoomConfigRow("Конференц-зал", true, "Химия", 1, 1, true)],
            [new ClassConfigRow("9Б", "Иванова", 9)],
            [new HourNormRow("Математика", 0, 4), new HourNormRow("Математика", 9, 5)],
            [new HourOverrideRow("9Б", "Математика", 7)],
            [new SubjectDifficultyRow("Математика", 8)],
            new CommonLessonRow(true, 3, 1, "5,6,7", true),
            [new TeacherAssignRow("Иванова", "Математика", AssignmentScope.Parallel, null, 9)],
            new FlexSettingsRow(true, 3, 2, 1, 7, TeacherAssignMode.HardClass));
        await store.SaveAsync(src);
        var got = await store.LoadAsync();
        Assert.Equal(2, got.Rooms.Count);
        Assert.True(got.Rooms.Single(r => r.RoomName == "Конференц-зал").IsManualOnly);
        Assert.Equal("Химия", got.Rooms.Single(r => r.RoomName == "Конференц-зал").OnlySubjectName);
        Assert.Single(got.Classes);
        Assert.Equal(2, got.HourNorms.Count);
        Assert.Single(got.HourOverrides);
        Assert.Single(got.SubjectDifficulty);
        Assert.Equal(8, got.SubjectDifficulty[0].Difficulty);
        Assert.NotNull(got.CommonLesson);
        Assert.True(got.CommonLesson!.Enabled);
        Assert.Equal("5,6,7", got.CommonLesson.GradesCsv);
        Assert.Single(got.Assignments);
        Assert.Equal(TeacherAssignMode.HardClass, got.Settings.AssignMode);
    }

    // Store: старая/пустая БД → дефолты, без исключений (обратная совместимость).
    [Fact]
    public async Task FlexStore_OldDb_ReadsDefaults()
    {
        Directory.CreateDirectory(_dir);
        var db = Path.Combine(_dir, "old.db");
        var school = new SqliteSchoolDataStore($"Data Source={db}");
        await school.InitializeAsync(); // только SchoolMeta/LoadRows — flex-таблиц нет
        var store = new SqliteFlexStore($"Data Source={db}");
        await store.InitializeAsync(); // создаёт недостающие
        var got = await store.LoadAsync();
        Assert.Empty(got.Rooms);
        Assert.Empty(got.HourNorms);
        Assert.Null(got.CommonLesson); // R3 по умолчанию выключен/отсутствует
        Assert.Equal(FlexSettingsRow.Default, got.Settings);
        Assert.Equal(7, new CommonLesson().Grades.Count); // дефолт 5–11
    }

    // R6/R8 дефолты (D-34).
    [Fact]
    public void FlexSettings_Defaults()
    {
        var s = FlexSettings.Default;
        Assert.True(s.GradePriorityEnabled);
        Assert.Equal(3, s.WeightForGrade(11));
        Assert.Equal(2, s.WeightForGrade(9));
        Assert.Equal(1, s.WeightForGrade(5));
        Assert.Equal(7, s.IsHeavyThreshold);
        Assert.Equal(TeacherAssignMode.HardClass, s.AssignMode);
        var off = new FlexSettings { GradePriorityEnabled = false, Grade11Weight = 5 };
        Assert.Equal(1, off.WeightForGrade(11));
    }
}
