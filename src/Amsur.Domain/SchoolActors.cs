namespace Amsur.Domain;

// Классы / подгруппы / люди / кабинеты (порт V1: Classes.cs, People.cs, Rooms.cs).
public sealed class SchoolClass : Entity
{
    public Guid AcademicYearId { get; set; }
    public string Name { get; set; } = "";      // "8А"
    public int Grade { get; set; }
    public Guid ShiftId { get; set; }
    public int StudentCount { get; set; }
    /// <summary>СанПиН-максимум уроков в день (SANPIN_RB.md §3): 1→5, 2–4→5, 5–6→6, 7–11→7. HARD.</summary>
    public int MaxLessonsPerDay { get; set; } = 7;
    /// <summary>R4: классный руководитель (источник учителя для общего урока R3).</summary>
    public Guid? ClassTeacherId { get; set; }
}

// Подгруппа — только для реальных сплитов; привязка к классу.
// Разные деления по предметам (DMP) — P1 (Splits v2); P0: A/B.
public sealed class StudentGroup : Entity
{
    public Guid ClassId { get; set; }
    public string Name { get; set; } = "";      // "A" / "B"
}

public sealed class Teacher : Entity
{
    public string Name { get; set; } = "";
    public int MaxLessonsPerDay { get; set; } = 6;  // D-04: Hard FROZEN в P0
    public int PreferredStartSlot { get; set; } = 1;
    public int PreferredEndSlot { get; set; } = 7;
}

// Квалификация = множество преподаваемых предметов (операционная, не информационная).
public sealed class TeacherSubject : Entity
{
    public Guid TeacherId { get; set; }
    public Guid SubjectId { get; set; }
}

public sealed class TeacherUnavailability : Entity
{
    public Guid TeacherId { get; set; }
    public int DayIndex { get; set; }
    public int SlotIndex { get; set; }
    public AvailabilityKind Kind { get; set; } = AvailabilityKind.Forbidden;
}

public sealed class TeacherDayOff : Entity
{
    public Guid TeacherId { get; set; }
    public int DayIndex { get; set; }
}

public sealed class Room : Entity
{
    public string Name { get; set; } = "";
    public string Building { get; set; } = "";
    public int Floor { get; set; }
    public int PhysicalCapacity { get; set; } = 30;     // Hard: MaxSimultaneousGroups-sweep
    public int ComfortableCapacity { get; set; } = 28;  // Soft
    public int MaxSimultaneousGroups { get; set; } = 1; // Hard (R5 MaxGroups)
    /// <summary>R1: только ручное назначение — solver сам сюда не ставит, ручная правка разрешена.</summary>
    public bool IsManualOnly { get; set; }
    /// <summary>R1: ONLY-предмет — никакой другой урок solver сюда не ставит (чужой = hard).</summary>
    public Guid? OnlySubjectId { get; set; }
    /// <summary>R5: желательно групп одновременно (soft; превышение = штраф room-crowding).
    /// 0 = не задано → = MaxSimultaneousGroups (без претензий, см. RoomPolicy).</summary>
    public int DesiredGroups { get; set; }
    /// <summary>R5: подгруппа считается отдельной единицей вместимости (спортзал: класс со сплитом = 2).</summary>
    public bool CountSubgroupAsGroup { get; set; } = true;
}

public sealed class RoomCapability : Entity
{
    public Guid RoomId { get; set; }
    public Guid SubjectId { get; set; }
    public RoomCapabilityKind Kind { get; set; } = RoomCapabilityKind.Universal;
}
