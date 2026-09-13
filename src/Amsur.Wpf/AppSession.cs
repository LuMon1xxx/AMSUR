using System.IO;
using Amsur.Application;
using Amsur.Infrastructure;
using Amsur.Scheduling.Core;

namespace Amsur.Wpf;

// Сессия рабочей версии: данные школы (в памяти), год, SQLite-хранилище.
// OccurrenceId детерминированы StableKey (E11) → пересборка того же входа
// даёт те же Id: активное расписание переживает перезапуск (имена стабильны).
public sealed class AppSession
{
    private readonly string _dir;
    private readonly string _yearFile;

    public string DbPath { get; }
    public Guid AcademicYearId { get; private set; }
    public SchoolData? Data { get; private set; }
    public SqliteScheduleStore Store { get; }
    public ManualEditService EditService { get; }
    private readonly SqliteQualityProfileStore _profiles;

    /// <summary>Активный набор весов (S5): обычный завуч не трогает (STANDARD).</summary>
    public EffectiveRuleSet QualityRules { get; private set; } = EffectiveRuleSet.Default;

    /// <summary>Имя активного CUSTOM-профиля (null — пресет).</summary>
    public string? CustomProfileName { get; private set; }

    /// <summary>Режим генерации (промт §6): QUICK/STANDARD/MAXIMUM/EXPERT.</summary>
    public GenerateModeSpec GenerateMode { get; private set; } = GenerateModes.ByCode("STANDARD");

    /// <summary>Последняя оценка для dashboard (память сессии + активное расписание).</summary>
    public QualityRatingResult? LastQuality { get; set; }

    /// <summary>Строки «почему так» последнего лучшего варианта (для подробного анализа).</summary>
    public IReadOnlyList<string>? LastQualityLines { get; set; }

    public void SetQualityProfile(string profileName) =>
        QualityRules = RuleResolver.Resolve(profileName);

    public void SetGenerateMode(string code) =>
        GenerateMode = GenerateModes.ByCode(code);

    /// <summary>Применить правки настроек без сохранения (предпросмотр CUSTOM).</summary>
    public void ApplyOverrides(IReadOnlyDictionary<string, long> overrides)
    {
        QualityRules = RuleResolver.Resolve("CUSTOM", overrides);
        CustomProfileName = null;
    }

    public AppSession(string directory)
    {
        _dir = directory;
        _yearFile = Path.Combine(directory, "year.txt");
        DbPath = Path.Combine(directory, "amsur.db");
        Store = new SqliteScheduleStore($"Data Source={DbPath}");
        EditService = new ManualEditService(new AcceptScheduleService(Store));
        _profiles = new SqliteQualityProfileStore($"Data Source={DbPath}");
    }

    public async Task InitAsync()
    {
        await Store.InitializeAsync();
        await Store.RepairAsync(); // честная починка после нештатных завершений
        await _profiles.InitializeAsync();
        // Восстанавливаем сохранённый CUSTOM (промт §18); версия каталога новее —
        // профиль устарел: остаёмся на STANDARD, молча не перезаписываем.
        var saved = await _profiles.GetActiveAsync();
        if (saved is not null && saved.CatalogVersion == RuleCatalog.Version)
        {
            QualityRules = saved.ToRuleSet();
            CustomProfileName = saved.Name;
        }
        if (File.Exists(_yearFile) && Guid.TryParse(
                await File.ReadAllTextAsync(_yearFile), out var year))
            AcademicYearId = year;
    }

    /// <summary>Сохранить «Моя школа» (промт §18): валидация до записи, single-active.</summary>
    public async Task SaveCustomProfileAsync(
        string name, string baseProfile, IReadOnlyDictionary<string, long> overrides,
        CancellationToken ct = default)
    {
        await _profiles.SaveCustomAsync(name, baseProfile, overrides, ct);
        var saved = await _profiles.GetActiveAsync(ct);
        if (saved is not null)
        {
            QualityRules = saved.ToRuleSet();
            CustomProfileName = saved.Name;
        }
    }

    public async Task<QualityProfileRecord?> GetSavedProfileAsync(CancellationToken ct = default) =>
        await _profiles.GetActiveAsync(ct);

    public bool HasData => Data is not null;

    /// <summary>Ошибки последнего импорта (для dashboard; пусто — всё хорошо).</summary>
    public IReadOnlyList<string> LastImportErrors { get; private set; } = [];

    public sealed record SchoolSummary(
        int Classes, int Teachers, int Subjects, int Lessons, int Days, int Slots)
    ;

    public SchoolSummary? Summary => Data is null ? null : new SchoolSummary(
        Data.Classes.Count, Data.Teachers.Count, Data.Subjects.Count,
        Data.Curriculum.Sum(c => c.HoursPerWeek), Data.DaysCount, Data.SlotsPerDay);

    public void ImportLoad(IReadOnlyList<LoadRow> rows, int days, int slots)
    {
        AcademicYearId = Guid.NewGuid();
        LastImportErrors = [];
        var data = SchoolDataImporter.Import(AcademicYearId, rows, days, slots);
        // Fail-loud ДО принятия данных: вход обязан строиться.
        var (problem, errors) = ProblemBuilder.Build(data.ToProblemInput());
        if (problem is null)
        {
            LastImportErrors = errors.ToList();
            throw new InvalidOperationException(
                "Данные не строятся в задачу: " + string.Join("; ", errors.Take(5)));
        }
        Data = data;
        LastQuality = null; // новые данные — старая оценка невалидна
        File.WriteAllText(_yearFile, AcademicYearId.ToString("D"));
    }

    public SchedulingProblem BuildProblem()
    {
        if (Data is null)
            throw new InvalidOperationException("Нет данных школы — загрузите нагрузку.");
        var (problem, errors) = ProblemBuilder.Build(Data.ToProblemInput());
        if (problem is null)
            throw new InvalidOperationException(
                "Данные не строятся в задачу: " + string.Join("; ", errors.Take(5)));
        return problem;
    }

    public Task<ActiveSchedule?> GetActiveAsync(CancellationToken ct = default) =>
        Store.GetActiveAsync(AcademicYearId, ct);

    /// <summary>Оценка активного расписания для dashboard (null — нет активного).</summary>
    public async Task<QualityRatingResult?> GetActiveQualityAsync(CancellationToken ct = default)
    {
        var active = await GetActiveAsync(ct);
        if (active is null || Data is null) return null;
        try
        {
            var problem = BuildProblem();
            var bd = SoftEvaluator.Evaluate(problem, active.Placements, QualityRules);
            return QualityRating.FromBreakdown(bd);
        }
        catch { return null; } // данные сменились — честно нет оценки
    }

    public async Task ExportActiveAsync(string path, CancellationToken ct = default)
    {
        var active = await GetActiveAsync(ct);
        if (active is null)
            throw new InvalidOperationException("Нет активного расписания — нечего выгружать.");
        var problem = BuildProblem();
        await using var fs = File.Create(path);
        ScheduleExcelExporter.ExportGrid(problem, active.Placements, fs);
    }
}
