namespace Amsur.Domain;

// Школа / время / смены (порт V1: School.cs, TimeGrid.cs).
public sealed class School : Entity
{
    public string Name { get; set; } = "";
}

public sealed class AcademicYear : Entity
{
    public Guid SchoolId { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; }
}

public sealed class Shift : Entity
{
    public string Name { get; set; } = "";
}

// Урок в смене: индекс + интервал. Точное время опционально (SPEC §13).
public sealed class TimeSlot : Entity
{
    public Guid ShiftId { get; set; }
    public int Index { get; set; }           // номер урока 1..N
    public int StartMinutes { get; set; }    // минуты от 00:00
    public int EndMinutes { get; set; }
    public int DayIndex { get; set; }        // 0..5 (Пн..Сб)
}

public sealed class SchoolDayException : Entity
{
    public Guid AcademicYearId { get; set; }
    public DateOnly Date { get; set; }
    public bool IsHoliday { get; set; }
    public string Reason { get; set; } = "";
}
