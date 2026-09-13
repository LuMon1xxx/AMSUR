namespace Amsur.Domain;

// Предметы / нагрузка / уроки (порт V1: Subjects.cs, Curriculum.cs).
public sealed class Subject : Entity
{
    public string Name { get; set; } = "";
    public int Difficulty { get; set; } = 5;      // 1..10, конфигурируемо; IsHeavy = >=7 (P1 в каталог)
    public bool IsPhysicalEducation { get; set; } // явный флаг, не name-matching
    public int MaxPerDay { get; set; } = 1;
}

public sealed class CurriculumItem : Entity
{
    public Guid ClassId { get; set; }
    public Guid SubjectId { get; set; }
    public Guid TeacherId { get; set; }   // INV-02: primary teacher фиксирован
    public Guid? RoomId { get; set; }     // предпочтительный кабинет (опционально)
    public int HoursPerWeek { get; set; }
    public bool SplitSubgroups { get; set; }  // A/B сплит (P0); N-сплиты — P1 Splits v2
}

// Единица размещения — конкретный час (из 5 ч → 5 occurrence).
public sealed class LessonOccurrence : Entity
{
    public Guid CurriculumItemId { get; set; }
    public Guid ClassId { get; set; }
    public Guid SubjectId { get; set; }
    public Guid TeacherId { get; set; }   // копия из CurriculumItem (денормализация для скорости)
    public Guid? GroupId { get; set; }    // null = весь класс; иначе подгруппа A/B
    public Guid? SyncGroupId { get; set; } // per hour-instance: A/B-пара делит start
    public int DurationSlots { get; set; } = 1;
    /// <summary>
    /// Стабильный логический ключ (E3): Class|Subject|Teacher|Group|#hour.
    /// Детерминирован между сборками одинакового входа; persistence identity (Id) не трогает.
    /// </summary>
    public string StableKey { get; set; } = "";
}

public sealed class LessonRelation : Entity
{
    public Guid? ClassId { get; set; }    // null = school-wide
    public Guid SubjectAId { get; set; }
    public Guid SubjectBId { get; set; }
    public LessonRelationKind Kind { get; set; }
    public int Priority { get; set; }     // explicit class > subject > profile default
}
