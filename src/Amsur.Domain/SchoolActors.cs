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
    public int MaxSimultaneousGroups { get; set; } = 1; // Hard
}

public sealed class RoomCapability : Entity
{
    public Guid RoomId { get; set; }
    public Guid SubjectId { get; set; }
    public RoomCapabilityKind Kind { get; set; } = RoomCapabilityKind.Universal;
}
