namespace Amsur.Scheduling.Core;

// Каталог правил: веса версии 3 (S5 decision: разморозка D-05/D-28 продолжена —
// teacher-gap разделён на ordinary (внутрисменные окна) и cross-shift (перерывы
// между сменами, естественны для двухсменки, вес ниже); обе компоненты видны отдельно).
// Изменения только через A/B + bump Version (политика сохранена).
public static class RuleCatalog
{
    public const int Version = 3;

    // DEFAULTS v3 (v2 без изменений, кроме двух новых кодов):
    // student-компактность доминирует: 1 окно ученика (100) не разменивается
    // меньше чем на 10 внутрисменных окон учителя; межсменный разрыв (2) дешевле
    // обычного окна (10), но НЕ бесплатен и виден отдельно (запрет «красивого Soft»).
    public const long StudentGap = 100;
    public const long StudentLateStart = 100; // за каждый слот позже anchor+1
    public const long HeavyEdge = 20;
    public const long TeacherGap = 10; // ordinary: окна внутри одной смены
    public const long TeacherCrossShiftGap = 2; // перерыв между сменами (настраиваемо 0..50)
    public const long PrimaryEarlyStart = 0; // мягкое предпочтение раннего старта началки (продьюсер — будущее; покрыто HARD late-start)
    public const long RoomPreference = 5;
    public const long SubjectMaxPerDay = 15;
    public const long RelationViolation = 15;
    public const long StrictStudentGap = 50;
    public const long StrictTeacherGap = 20;

    // HARD_WEIGHT инвариант (INV-09): hard никогда не проигрывает soft структурно,
    // т.к. hard = constraints, а не термы. Формула для tie-cap:
    public static long HardWeight(long maxSoftTotal) => checked(maxSoftTotal * 10 + 1);

    public static IReadOnlyList<string> AllCodes { get; } =
    [
        "student-gap", "student-late-start", "teacher-gap", "teacher-cross-shift-gap", "primary-early-start",
        "heavy-edge", "room-preference",
        "subject-maxperday", "relation-violation",
        "sanpin-peak-days", "sanpin-heavy-edge-limit", "sanpin-pe-spacing",
        "sanpin-doubles", "sanpin-primary-early"
    ];

    /// <summary>Дефолтный вес кода (для EffectiveRuleSet и UI-подписей).</summary>
    public static long DefaultWeight(string code) => code switch
    {
        "student-gap" => StudentGap,
        "student-late-start" => StudentLateStart,
        "heavy-edge" => HeavyEdge,
        "teacher-gap" => TeacherGap,
        "teacher-cross-shift-gap" => TeacherCrossShiftGap,
        "primary-early-start" => PrimaryEarlyStart,
        "room-preference" => RoomPreference,
        "subject-maxperday" => SubjectMaxPerDay,
        "relation-violation" => RelationViolation,
        "strict-student-gap" => StrictStudentGap,
        "strict-teacher-gap" => StrictTeacherGap,
        _ => 0,
    };

    /// <summary>Допустимый диапазон ручной настройки (для GUI-валидации).</summary>
    public static (long Min, long Max) WeightRange(string code) => code switch
    {
        "teacher-gap" => (0, 100),
        "teacher-cross-shift-gap" => (0, 50),
        "primary-early-start" => (0, 100),
        "subject-maxperday" => (0, 100),
        "room-preference" => (0, 50),
        "heavy-edge" => (0, 100),
        _ => (0, 0),
    };

    // SanPin-дефолты UNVERIFIED (D-11): требуют сверки с НПА, в UI бейдж.
    public static bool NeedsConfirmation(string code) => code.StartsWith("sanpin-", StringComparison.Ordinal);
}
