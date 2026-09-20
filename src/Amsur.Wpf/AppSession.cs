using System.IO;
using Amsur.Application;
using Amsur.Domain;
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
    private readonly SqliteSchoolDataStore _schoolData;
    private readonly SqliteFlexStore _flexStore;
    private readonly SqliteAppSettingsStore _appSettings;

    /// <summary>B1: показывать предупреждения об опасных изменениях (дефолт true, персист).</summary>
    public bool ConfirmDangerous { get; private set; } = true;

    /// <summary>A3: InitAsync завершён (данные восстановлены или их нет).
    /// Окна показывают «Загрузка…», пока false.</summary>
    public bool IsReady { get; private set; }

    /// <summary>P3/R1–R9: гибкие настройки школы (нормы, кабинеты, классруки,
    /// общий урок, закрепления, веса). Источник — FlexStore; дефолт — Empty.</summary>
    public FlexDataset Flex { get; private set; } = FlexDataset.Empty;

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

    /// <summary>B1: переключить глобальные предупреждения (персист).</summary>
    public async Task SetConfirmDangerousAsync(bool value, CancellationToken ct = default)
    {
        ConfirmDangerous = value;
        await _appSettings.SetBoolAsync("ui.confirmDangerous", value, ct);
    }

    /// <summary>B1+B2: опасное изменение через подтверждение (глобал-офф = сразу да).</summary>
    /// <returns>true — продолжать.</returns>
    public async Task<bool> ConfirmDangerousAsync(
        System.Windows.Window? owner, string code, string title, string consequence)
    {
        if (!ConfirmDangerous) return true;
        var (proceed, dontAsk) = DangerConfirm.Show(owner, code, title, consequence);
        if (dontAsk) await SetConfirmDangerousAsync(false);
        return proceed;
    }

    /// <summary>Применить правки настроек без сохранения (предпросмотр CUSTOM).</summary>
    public void ApplyOverrides(IReadOnlyDictionary<string, long> overrides) =>
        ApplyOverrides(overrides, null);

    /// <summary>B2: применить с явным набором подтверждённых опасных кодов.</summary>
    public void ApplyOverrides(
        IReadOnlyDictionary<string, long> overrides, IReadOnlySet<string>? confirmedDangerous)
    {
        QualityRules = RuleResolver.Resolve("CUSTOM", overrides, confirmedDangerous);
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
        _schoolData = new SqliteSchoolDataStore($"Data Source={DbPath}");
        _flexStore = new SqliteFlexStore($"Data Source={DbPath}");
        _appSettings = new SqliteAppSettingsStore($"Data Source={DbPath}");
    }

    public async Task InitAsync()
    {
        await Store.InitializeAsync();
        await Store.RepairAsync(); // честная починка после нештатных завершений
        await _profiles.InitializeAsync();
        await _schoolData.InitializeAsync();
        await _flexStore.InitializeAsync();
        await _appSettings.InitializeAsync();
        // B1: глобальный тумблер предупреждений (дефолт true).
        try { ConfirmDangerous = await _appSettings.GetBoolAsync("ui.confirmDangerous", true); }
        catch { ConfirmDangerous = true; }
        // P3: гибкие настройки переживают перезапуск (старые БД — дефолты).
        try { Flex = await _flexStore.LoadAsync(); }
        catch { Flex = FlexDataset.Empty; }
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
        // P3: восстанавливаем сохранённые строки нагрузки (ручной ввод / прошлый импорт).
        try
        {
            var stored = await _schoolData.LoadAsync();
            if (stored is not null && stored.Rows.Count > 0)
            {
                AcademicYearId = stored.AcademicYearId;
                AcceptRows(FromStored(stored.Rows), stored.DaysCount, stored.SlotsPerDay, stored.Source);
                // A3: year.txt при restore НЕ перезаписываем (там уже тот же год).
            }
        }
        catch (Exception ex)
        {
            LastImportErrors = [$"Сохранённые данные не восстановились: {ex.Message}"];
        }
        IsReady = true;
    }

    /// <summary>Сохранить «Моя школа» (промт §18): валидация до записи, single-active.</summary>
    public async Task SaveCustomProfileAsync(
        string name, string baseProfile, IReadOnlyDictionary<string, long> overrides,
        IReadOnlySet<string>? confirmedDangerous = null,
        CancellationToken ct = default)
    {
        await _profiles.SaveCustomAsync(name, baseProfile, overrides, confirmedDangerous, ct);
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

    /// <summary>Исходные строки нагрузки (источник истины для ручного ввода).</summary>
    public IReadOnlyList<LoadRow> LoadRows { get; private set; } = [];

    /// <summary>Откуда данные: "excel" или "manual".</summary>
    public string DataSource { get; private set; } = "";

    /// <summary>Ошибки последнего импорта (для dashboard; пусто — всё хорошо).</summary>
    public IReadOnlyList<string> LastImportErrors { get; private set; } = [];

    public sealed record SchoolSummary(
        int Classes, int Teachers, int Subjects, int Lessons, int Days, int Slots)
    ;

    public SchoolSummary? Summary => Data is null ? null : new SchoolSummary(
        Data.Classes.Count, Data.Teachers.Count, Data.Subjects.Count,
        Data.Curriculum.Sum(c => c.HoursPerWeek), Data.DaysCount, Data.SlotsPerDay);

    public async Task ImportLoadAsync(
        IReadOnlyList<LoadRow> rows, int days, int slots,
        CancellationToken ct = default)
    {
        AcademicYearId = Guid.NewGuid();
        AcceptRows(rows, days, slots, source: "excel");
        await _schoolData.SaveAsync(AcademicYearId, ToStored(LoadRows),
            Data!.DaysCount, Data.SlotsPerDay, DataSource, ct);
        File.WriteAllText(_yearFile, AcademicYearId.ToString("D"));
    }

    /// <summary>P3: ручные правки — тот же AcceptRows, но год сохраняется
    /// (активное расписание остаётся привязанным к году).</summary>
    public async Task SetManualRowsAsync(
        IReadOnlyList<LoadRow> rows, int days, int slots,
        CancellationToken ct = default)
    {
        if (AcademicYearId == Guid.Empty)
            AcademicYearId = Guid.NewGuid();
        AcceptRows(rows, days, slots, source: "manual");
        await _schoolData.SaveAsync(AcademicYearId, ToStored(LoadRows),
            Data!.DaysCount, Data.SlotsPerDay, DataSource, ct);
        File.WriteAllText(_yearFile, AcademicYearId.ToString("D"));
    }

    /// <summary>
    /// P3/R1–R9: применить гибкие настройки: перепроверка текущего входа с новым
    /// flex (fail-loud — при ошибке старые данные и настройки нетронуты) + персист.
    /// </summary>
    public async Task ApplyFlexAsync(FlexDataset flex, CancellationToken ct = default)
    {
        if (Data is null)
        {
            // Данных нет — только сохраняем (применятся при импорте).
            Flex = flex;
            await _flexStore.SaveAsync(flex, ct);
            return;
        }
        // Сухой прогон через AcceptRows: сначала всё проверяется, Data/Flex
        // меняются только при успехе (внутри AcceptRows — после gate).
        var rows = LoadRows.ToList();
        int days = Data.DaysCount, slots = Data.SlotsPerDay;
        string source = DataSource;
        AcceptRows(rows, days, slots, source, flex);
        Flex = flex;
        await _flexStore.SaveAsync(flex, ct);
        await _schoolData.SaveAsync(AcademicYearId, ToStored(LoadRows),
            Data.DaysCount, Data.SlotsPerDay, DataSource, ct);
    }
    private static IReadOnlyList<StoredLoadRow> ToStored(IReadOnlyList<LoadRow> rows) =>
        rows.Select(r => new StoredLoadRow(r.ClassName, r.SubjectName, r.HoursPerWeek,
            r.TeacherName, r.SplitSubgroups, r.SplitTeacherBName, r.RoomName,
            r.UnavailDays, r.UnavailSlots)).ToList();

    private static IReadOnlyList<LoadRow> FromStored(IReadOnlyList<StoredLoadRow> rows) =>
        rows.Select(r => new LoadRow(r.ClassName, r.SubjectName, r.HoursPerWeek,
            r.TeacherName, r.SplitSubgroups, r.SplitTeacherBName, r.RoomName,
            r.UnavailDays, r.UnavailSlots)).ToList();

    private void AcceptRows(IReadOnlyList<LoadRow> rows, int days, int slots, string source) =>
        AcceptRows(rows, days, slots, source, null);

    /// <summary>P3: импорт с гибкими настройками (null — текущие Flex сессии).</summary>
    private void AcceptRows(
        IReadOnlyList<LoadRow> rows, int days, int slots, string source, FlexDataset? flex)
    {
        LastImportErrors = [];
        var data = SchoolDataImporter.Import(AcademicYearId, rows, days, slots, flex ?? Flex);
        // Fail-loud ДО принятия данных: вход обязан строиться.
        var (problem, errors) = ProblemBuilder.Build(data.ToProblemInput());
        if (problem is null)
        {
            LastImportErrors = errors.ToList();
            throw new InvalidOperationException(
                "Данные не строятся в задачу: " + string.Join("; ", errors.Take(5)));
        }
        Data = data;
        LoadRows = rows.ToList();
        DataSource = source;
        LastQuality = null; // новые данные — старая оценка невалидна
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

    public async Task ExportActiveAsync(string path, CancellationToken ct = default) =>
        await ExportActiveAsync(path, includeTeacherSheet: true, ct);

    /// <summary>P4/R9: выгрузка с опциональным листом «Учителя».</summary>
    public async Task ExportActiveAsync(string path, bool includeTeacherSheet, CancellationToken ct = default) =>
        await ExportActiveAsync(path, includeTeacherSheet, includeRoomSheet: true, ct);

    /// <summary>Три вида из ТЗ §22: классы + учителя + кабинеты (опц.).</summary>
    public async Task ExportActiveAsync(string path, bool includeTeacherSheet, bool includeRoomSheet, CancellationToken ct = default)
    {
        var active = await GetActiveAsync(ct);
        if (active is null)
            throw new InvalidOperationException("Нет активного расписания — нечего выгружать.");
        var problem = BuildProblem();
        await using var fs = File.Create(path);
        ScheduleExcelExporter.ExportGrid(problem, active.Placements, fs, includeTeacherSheet, includeRoomSheet);
    }

    /// <summary>B4: HTML-выгрузка активного (просмотр/печать без Excel).</summary>
    public async Task ExportActiveHtmlAsync(string path, CancellationToken ct = default)
    {
        var active = await GetActiveAsync(ct);
        if (active is null)
            throw new InvalidOperationException("Нет активного расписания — нечего выгружать.");
        var problem = BuildProblem();
        await using var fs = File.Create(path);
        ScheduleHtmlExporter.ExportHtml(problem, active.Placements, fs);
    }

    /// <summary>A2: быстрый черновик из текущих данных (жадный старт, ~секунды,
    /// без генерации). Честные дыры — листом «Неназначенные», gate ослаблен (D-42).</summary>
    public async Task<(int Placed, int Total)> ExportDraftAsync(string path, CancellationToken ct = default)
    {
        var problem = BuildProblem();
        var g = GreedyPlacer.Place(problem, 11);
        var placements = g.Placed.Select(kv => new PlacedLesson
        {
            OccurrenceId = kv.Key,
            DayIndex = kv.Value.Day,
            SlotIndex = kv.Value.Slot,
            RoomId = kv.Value.RoomId,
        }).ToList();
        await using var fs = File.Create(path);
        ScheduleExcelExporter.ExportDraftGrid(problem, placements, g.Unplaced, fs);
        return (g.Placed.Count, problem.Occurrences.Count);
    }

    /// <summary>A2: персональное расписание учителя из активного.</summary>
    public async Task ExportTeacherAsync(string path, Guid teacherId, CancellationToken ct = default)
    {
        var active = await GetActiveAsync(ct);
        if (active is null)
            throw new InvalidOperationException("Нет активного расписания — нечего выгружать.");
        var problem = BuildProblem();
        await using var fs = File.Create(path);
        ScheduleExcelExporter.ExportTeacherGrid(problem, active.Placements, teacherId, fs);
    }
}
