namespace Amsur.Domain;

// R1–R9 гибкие настройки школы (D-34: дефолты = типовая школа из коробки,
// каждое правило перенастраивается; никаких хардкодов школьных привычек).
// Id-сущности (домен/солвер) + name-keyed DTO (store/import: имена схлопываются
// OrdinalIgnoreCase, как LoadRows; Grade=0 в нормах = дефолт предмета).

// R2: норма часов предмета. Grade 0 = дефолт на все параллели; 5..11 = параллель.
public sealed class SubjectHourNorm : Entity
{
    public Guid SubjectId { get; set; }
    public int Grade { get; set; }
    public int HoursPerWeek { get; set; }
}

// R2: переопределение часов на класс (бьёт норму параллели и дефолт предмета).
public sealed class ClassHourOverride : Entity
{
    public Guid ClassId { get; set; }
    public Guid SubjectId { get; set; }
    public int HoursPerWeek { get; set; }
}

// R3: общий урок (классный час). По умолчанию ВЫКЛЮЧЕН (Enabled=false, D-34).
// Двухсменка (MidSchool): SlotIndexShift2 = урок для классов, чья смена не
// содержит SlotIndex (0 = не задан → старое поведение: один слот на всех).
public sealed class CommonLesson : Entity
{
    public bool Enabled { get; set; }
    public int DayIndex { get; set; } = 3;    // Чт (0=Пн, 5-дневка)
    public int SlotIndex { get; set; } = 1;   // 1-й урок (1-based, как AllowedSlots)
    public int SlotIndexShift2 { get; set; } = 0; // урок 2-й смены (0 = нет второй группы)
    public string GradesCsv { get; set; } = "5,6,7,8,9,10,11";
    public bool UseOwnRooms { get; set; } = true;  // свои кабинеты; false = общий зал (P2)

    public IReadOnlyList<int> Grades => GradesCsv.Split(',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(s => int.TryParse(s, out int g) ? g : -1)
        .Where(g => g >= 1 && g <= 11)
        .Distinct().Order().ToList();
}

// R7: закрепление учителя на класс/параллель.
public sealed class TeacherAssignment : Entity
{
    public Guid TeacherId { get; set; }
    public Guid SubjectId { get; set; }
    public AssignmentScope Scope { get; set; } = AssignmentScope.Class;
    public Guid? ClassId { get; set; }   // Scope=Class
    public int? Grade { get; set; }      // Scope=Parallel
}

// R6/R8 + режимы R7: одиночные настройки школы (синглтон; дефолты — D-34).
public sealed class FlexSettings : Entity
{
    public bool GradePriorityEnabled { get; set; } = true;
    public int Grade11Weight { get; set; } = 3;
    public int Grade9Weight { get; set; } = 2;
    public int GradeOtherWeight { get; set; } = 1;
    public int IsHeavyThreshold { get; set; } = 7;
    public TeacherAssignMode AssignMode { get; set; } = TeacherAssignMode.HardClass;

    public static FlexSettings Default => new();

    /// <summary>
    /// Нейтральный набор для ручных SchedulingProblem (тесты/харнесы): все
    /// множители 1, приоритет выкл, закрепление выкл. Поведение = как до R1–R9.
    /// Прикладной дефолт (D-34) — Default, подставляется импортёром из FlexDataset.
    /// </summary>
    public static FlexSettings Neutral => new()
    {
        GradePriorityEnabled = false,
        Grade11Weight = 1,
        Grade9Weight = 1,
        GradeOtherWeight = 1,
        IsHeavyThreshold = 7,
        AssignMode = TeacherAssignMode.Off,
    };

    public int WeightForGrade(int grade)
    {
        if (!GradePriorityEnabled) return 1;
        if (grade == 11) return Grade11Weight;
        if (grade == 9) return Grade9Weight;
        return GradeOtherWeight;
    }
}

// Name-keyed DTO для store/import (без Id; см. шапку файла).
public sealed record RoomConfigRow(
    string RoomName,
    bool IsManualOnly,
    string? OnlySubjectName,
    int MaxGroups,
    int DesiredGroups,
    bool CountSubgroupAsGroup);

public sealed record ClassConfigRow(
    string ClassName,
    string? ClassTeacherName,
    int Grade,
    int StudentCount = 25);

public sealed record HourNormRow(
    string SubjectName,
    int Grade,
    int HoursPerWeek);

public sealed record HourOverrideRow(
    string ClassName,
    string SubjectName,
    int HoursPerWeek);

// R6: сложность предмета (1..10; IsHeavy = >= порога из FlexSettings).
public sealed record SubjectDifficultyRow(
    string SubjectName,
    int Difficulty);

public sealed record CommonLessonRow(
    bool Enabled,
    int DayIndex,
    int SlotIndex,
    string GradesCsv,
    bool UseOwnRooms,
    int SlotIndexShift2 = 0);

public sealed record TeacherAssignRow(
    string TeacherName,
    string SubjectName,
    AssignmentScope Scope,
    string? ClassName,
    int? Grade);

public sealed record FlexSettingsRow(
    bool GradePriorityEnabled,
    int W11,
    int W9,
    int WOther,
    int IsHeavyThreshold,
    TeacherAssignMode AssignMode)
{
    public static FlexSettingsRow Default => new(true, 3, 2, 1, 7, TeacherAssignMode.HardClass);

    public FlexSettings ToSettings() => new()
    {
        GradePriorityEnabled = GradePriorityEnabled,
        Grade11Weight = W11,
        Grade9Weight = W9,
        GradeOtherWeight = WOther,
        IsHeavyThreshold = IsHeavyThreshold,
        AssignMode = AssignMode,
    };
}

public sealed record FlexDataset(
    IReadOnlyList<RoomConfigRow> Rooms,
    IReadOnlyList<ClassConfigRow> Classes,
    IReadOnlyList<HourNormRow> HourNorms,
    IReadOnlyList<HourOverrideRow> HourOverrides,
    IReadOnlyList<SubjectDifficultyRow> SubjectDifficulty,
    CommonLessonRow? CommonLesson,
    IReadOnlyList<TeacherAssignRow> Assignments,
    FlexSettingsRow Settings)
{
    public static FlexDataset Empty => new([], [], [], [], [], null, [],
        FlexSettingsRow.Default);
}
