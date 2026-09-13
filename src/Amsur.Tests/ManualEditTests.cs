using Amsur.Application;
using Amsur.Domain;
using Amsur.Infrastructure;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// E8 — превью без solver + коммит новой версией с gate.
public sealed class ManualEditTests : IAsyncDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"amsur-e8-{Guid.NewGuid():N}.db");
    private string Cs => $"Data Source={_dbPath}";

    public async ValueTask DisposeAsync()
    {
        for (int i = 0; i < 5; i++)
        {
            try { if (File.Exists(_dbPath)) File.Delete(_dbPath); break; }
            catch { await Task.Delay(100); }
        }
        try { if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal"); } catch { }
        try { if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm"); } catch { }
        GC.SuppressFinalize(this);
    }

    private static (SchedulingProblem Problem, Guid Year) Tiny()
    {
        var year = Guid.NewGuid();
        var cls = new SchoolClass { AcademicYearId = year, Name = "5А", Grade = 5, StudentCount = 25 };
        var teacher = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Мат", MaxPerDay = 2 };
        var item = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = math.Id, TeacherId = teacher.Id, HoursPerWeek = 2
        };
        var input = new ProblemInput([cls], [teacher], [math], [item],
            [], [], [], DaysCount: 2, SlotsPerDay: 3);
        var (p, e) = ProblemBuilder.Build(input,
            new SolverOptions(MaxTimeSeconds: 5, NumSearchWorkers: 1, RandomSeed: 1));
        Assert.Empty(e);
        return (p!, year);
    }

    private static List<PlacedLesson> Start(SchedulingProblem p) =>
        p.Occurrences.Select((o, i) => new PlacedLesson
        {
            OccurrenceId = o.Id, DayIndex = 0, SlotIndex = i + 1
        }).ToList();

    private async Task<(ManualEditService Svc, SqliteScheduleStore Store)> HarnessAsync()
    {
        var store = new SqliteScheduleStore(Cs);
        await store.InitializeAsync();
        return (new ManualEditService(new AcceptScheduleService(store)), store);
    }

    // --- 1. Свободная клетка: можно ---
    [Fact]
    public void Preview_ValidMove_Allowed()
    {
        var (p, _) = Tiny();
        var svc = new ManualEditService(new AcceptScheduleService(
            new SqliteScheduleStore("Data Source=:memory:")));
        var occ = p.Occurrences[0];
        var preview = svc.Preview(p, Start(p),
            new CandidateMove(occ.Id, DayIndex: 1, SlotIndex: 1, RoomId: null));
        Assert.True(preview.CanCommit);
        Assert.Equal(0, preview.SoftDelta);
        Assert.Contains("Можно", preview.VerdictText);
    }

    // --- 2. Коллизия: запрещено с причиной ---
    [Fact]
    public void Preview_Collision_ForbiddenWithReason()
    {
        var (p, _) = Tiny();
        var svc = new ManualEditService(new AcceptScheduleService(
            new SqliteScheduleStore("Data Source=:memory:")));
        var occ = p.Occurrences[0];
        var other = Start(p).First(x => x.OccurrenceId == p.Occurrences[1].Id);
        var preview = svc.Preview(p, Start(p),
            new CandidateMove(occ.Id, other.DayIndex, other.SlotIndex, null));
        Assert.False(preview.CanCommit);
        Assert.StartsWith("Нельзя:", preview.VerdictText);
        Assert.NotEmpty(preview.Reasons);
    }

    // --- 3. Ход с окном: запрещён (D-28: окна у учеников — HARD) ---
    [Fact]
    public void Preview_GapMove_ForbiddenWithReason()
    {
        var (p, _) = Tiny();
        var svc = new ManualEditService(new AcceptScheduleService(
            new SqliteScheduleStore("Data Source=:memory:")));
        var occ = p.Occurrences[1]; // (0,2) → (0,3): разрыв {1,3} = окно
        var preview = svc.Preview(p, Start(p),
            new CandidateMove(occ.Id, DayIndex: 0, SlotIndex: 3, RoomId: null));
        Assert.False(preview.CanCommit);
        Assert.StartsWith("Нельзя:", preview.VerdictText);
        Assert.Contains(preview.Reasons, r => r.ToLowerInvariant().Contains("окно"));
    }

    // --- 4. Коммит валидного: новая версия активна ---
    [Fact]
    public async Task Commit_Valid_NewActiveVersion()
    {
        var (p, year) = Tiny();
        var (svc, store) = await HarnessAsync();
        var preview = svc.Preview(p, Start(p),
            new CandidateMove(p.Occurrences[0].Id, 1, 1, null));
        Assert.True(preview.CanCommit);
        var outcome = await svc.CommitAsync(year, p, preview);
        Assert.True(outcome.Committed);
        Assert.Contains("версия 1", outcome.Message);
        var active = await store.GetActiveAsync(year);
        Assert.NotNull(active);
        Assert.Contains(active!.Placements,
            x => x.OccurrenceId == p.Occurrences[0].Id && x.DayIndex == 1);
    }

    // --- 5. Коммит запрещённого: отказ без записи ---
    [Fact]
    public async Task Commit_Forbidden_RefusedWithoutWrite()
    {
        var (p, year) = Tiny();
        var (svc, store) = await HarnessAsync();
        var other = Start(p).First(x => x.OccurrenceId == p.Occurrences[1].Id);
        var preview = svc.Preview(p, Start(p),
            new CandidateMove(p.Occurrences[0].Id, other.DayIndex, other.SlotIndex, null));
        Assert.False(preview.CanCommit);
        var outcome = await svc.CommitAsync(year, p, preview);
        Assert.False(outcome.Committed);
        Assert.Contains("Не сохранено", outcome.Message);
        Assert.Equal(0, await store.CountActiveVersionsAsync(year));
    }

    // --- 6. Разрыв sync-половинок: запрещено ---
    [Fact]
    public void Preview_SyncSplit_Forbidden()
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5 };
        var tA = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var tB = new Teacher { Name = "Петрова", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Мат", MaxPerDay = 2 };
        var item = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = math.Id, TeacherId = tA.Id,
            HoursPerWeek = 1, SplitSubgroups = true
        };
        var groups = new[]
        {
            new StudentGroup { ClassId = cls.Id, Name = "A" },
            new StudentGroup { ClassId = cls.Id, Name = "B" },
        };
        var input = new ProblemInput([cls], [tA, tB], [math], [item], groups, [], [],
            DaysCount: 2, SlotsPerDay: 3,
            SplitTeachers: new Dictionary<Guid, (Guid, Guid)> { [item.Id] = (tA.Id, tB.Id) });
        var (p, e) = ProblemBuilder.Build(input);
        Assert.Empty(e);
        var svc = new ManualEditService(new AcceptScheduleService(
            new SqliteScheduleStore("Data Source=:memory:")));
        // Обе половины в (0,1); двигаем только первую.
        var start = p!.Occurrences.Select(o => new PlacedLesson
        {
            OccurrenceId = o.Id, DayIndex = 0, SlotIndex = 1
        }).ToList();
        var preview = svc.Preview(p, start,
            new CandidateMove(p.Occurrences[0].Id, 1, 1, null));
        Assert.False(preview.CanCommit);
        Assert.Contains(preview.Reasons, r => r.Contains("синхронизация"));
    }
}
