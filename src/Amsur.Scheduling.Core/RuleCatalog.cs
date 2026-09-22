namespace Amsur.Scheduling.Core;

// Каталог правил: веса версии 6 (D-50: +teacher-active-day — цена открытого
// учителе-дня, bin-packing-давление против размазывания нагрузки;
// B2: +teacher-maxperday/+class-maxperday как настраиваемые коды;
// DangerousCodes + OverrideRange для ослабления строгого).
// Дефолты v5 НЕ меняются (дефолт нового кода 0 = сегодняшнее поведение);
// teacher-maxperday/class-maxperday дефолт 0 = hard-gate валидатора.
// Изменения только через A/B + bump Version (политика сохранена).
public static class RuleCatalog
{
    public const int Version = 6;

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
    public const long RoomCrowding = 8;   // R5: каждая единица сверх «желательно»
    public const long TeacherSplit = 25;  // R7-Soft: каждый лишний учитель на (класс,предмет)
    public const long TeacherMaxPerDay = 0; // B2: 0 = hard-gate; soft — только при ослаблении
    public const long ClassMaxPerDay = 0;   // B2: аналогично (включая норму 1-х классов)
    public const long TeacherActiveDay = 0; // D-50: цена занятого учителе-дня; 0 = выкл (дефолт-поведение не меняется)
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
        "heavy-edge", "room-preference", "room-crowding", "teacher-split",
        "teacher-maxperday", "class-maxperday", "teacher-active-day",
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
        "room-crowding" => RoomCrowding,
        "teacher-split" => TeacherSplit,
        "teacher-maxperday" => TeacherMaxPerDay,
        "class-maxperday" => ClassMaxPerDay,
        "teacher-active-day" => TeacherActiveDay,
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
        "teacher-active-day" => (0, 100),
        "teacher-cross-shift-gap" => (0, 50),
        "primary-early-start" => (0, 100),
        "subject-maxperday" => (0, 100),
        "room-preference" => (0, 50),
        "room-crowding" => (0, 50),
        "teacher-split" => (0, 100),
        "heavy-edge" => (0, 100),
        _ => (0, 0),
    };

    // B2: опасные коды — строгие правила, ослабляемые ТОЛЬКО через подтверждение
    // (DangerConfirm). Дефолт = сегодняшнее строгое (HARD в валидаторе); ослабление
    // уходит в Warnings + soft с весом из OverrideRange. Legacy strict-* слоты —
    // dormant-совместимость, не валидаторные коды (в список не входят).
    // grade-one-five-day покрывается кодом class-maxperday (та же ветка валидатора).
    public static IReadOnlyList<string> DangerousCodes { get; } =
    [
        "student-gap", "student-late-start", "teacher-maxperday", "class-maxperday",
        "sanpin-peak-days", "sanpin-heavy-edge-limit", "sanpin-pe-spacing",
        "sanpin-doubles", "sanpin-primary-early"
    ];

    public static bool IsDangerous(string code) =>
        DangerousCodes.Contains(code, StringComparer.Ordinal);

    /// <summary>Диапазон веса при ослаблении опасного (дефолт слайдера — max=100).</summary>
    public static (long Min, long Max) OverrideRange(string code) =>
        IsDangerous(code) ? (0, 100) : WeightRange(code);

    /// <summary>Действующий диапазон: опасные — OverrideRange, остальные — WeightRange.</summary>
    public static (long Min, long Max) EffectiveRange(string code) =>
        IsDangerous(code) ? OverrideRange(code) : WeightRange(code);

    // SanPin-дефолты UNVERIFIED (D-11): требуют сверки с НПА, в UI бейдж.
    public static bool NeedsConfirmation(string code) => code.StartsWith("sanpin-", StringComparison.Ordinal);
}
