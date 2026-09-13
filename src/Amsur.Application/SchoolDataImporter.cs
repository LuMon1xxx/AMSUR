using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Application;

// E10 — ввод данных школы: LoadRow (Excel) → сущности → ProblemInput.
// Привязка имён к Id (было P2-заглушкой C2): имена схлопываются без учёта регистра;
// классы/предметы/учителя/кабинеты создаются из имён с разумными дефолтами.
// Сплит-строка → одна A/B-пара групп на класс + SplitTeachers (DMP — P1, см. план).
// Дефолты зафиксированы здесь, а не в UI: MaxPerDay=2, MaxLessonsPerDay=6,
// Room 30 мест / 1 группа одновременно.

public sealed record SchoolData(
    Guid AcademicYearId,
    IReadOnlyList<SchoolClass> Classes,
    IReadOnlyList<Teacher> Teachers,
    IReadOnlyList<Subject> Subjects,
    IReadOnlyList<CurriculumItem> Curriculum,
    IReadOnlyList<StudentGroup> Groups,
    IReadOnlyList<Room> Rooms,
    IReadOnlyDictionary<Guid, (Guid TeacherA, Guid TeacherB)> SplitTeachers,
    int DaysCount,
    int SlotsPerDay,
    IReadOnlyList<string> Notes)
{
    public ProblemInput ToProblemInput() => new(
        Classes, Teachers, Subjects, Curriculum, Groups, [], [],
        DaysCount: DaysCount, SlotsPerDay: SlotsPerDay,
        SplitTeachers: new Dictionary<Guid, (Guid, Guid)>(SplitTeachers),
        rooms: Rooms); // P0-8: кабинеты из импорта обязаны доходить до solver (было: дроп в []).
}

public static class SchoolDataImporter
{
    public static void ExportTemplate(Stream destination) =>
        ExcelLoadExchange.ExportLoad(destination, []);

    public static SchoolData Import(
        Guid academicYearId,
        IReadOnlyList<LoadRow> rows,
        int daysCount = 5,
        int slotsPerDay = 7)
    {
        if (rows.Count == 0)
            throw new InvalidOperationException("Файл не содержит строк нагрузки.");
        if (daysCount <= 0 || slotsPerDay <= 0)
            throw new InvalidOperationException("Дни и уроки в день должны быть положительными.");

        var classes = new Dictionary<string, SchoolClass>(StringComparer.OrdinalIgnoreCase);
        var teachers = new Dictionary<string, Teacher>(StringComparer.OrdinalIgnoreCase);
        var subjects = new Dictionary<string, Subject>(StringComparer.OrdinalIgnoreCase);
        var rooms = new Dictionary<string, Room>(StringComparer.OrdinalIgnoreCase);
        var groups = new Dictionary<Guid, (StudentGroup A, StudentGroup B)>();
        var curriculum = new List<CurriculumItem>();
        var splitTeachers = new Dictionary<Guid, (Guid, Guid)>();
        var notes = new List<string>();

        SchoolClass Cls(string name)
        {
            var key = name.Trim();
            if (!classes.TryGetValue(key, out var c))
            {
                c = new SchoolClass
                {
                    AcademicYearId = academicYearId, Name = key, Grade = 0, StudentCount = 25
                };
                classes[key] = c;
            }
            return c;
        }

        Teacher Teach(string name)
        {
            var key = name.Trim();
            if (!teachers.TryGetValue(key, out var t))
            {
                t = new Teacher { Name = key, MaxLessonsPerDay = 6 };
                teachers[key] = t;
            }
            return t;
        }

        Subject Subj(string name)
        {
            var key = name.Trim();
            if (!subjects.TryGetValue(key, out var s))
            {
                s = new Subject { Name = key, MaxPerDay = 2 };
                subjects[key] = s;
            }
            return s;
        }

        foreach (var r in rows)
        {
            var cls = Cls(r.ClassName);
            var subj = Subj(r.SubjectName);
            var teacher = Teach(r.TeacherName);
            var item = new CurriculumItem
            {
                ClassId = cls.Id, SubjectId = subj.Id, TeacherId = teacher.Id,
                HoursPerWeek = r.HoursPerWeek, SplitSubgroups = r.SplitSubgroups,
            };
            if (r.RoomName is not null)
            {
                var key = r.RoomName.Trim();
                if (!rooms.TryGetValue(key, out var room))
                {
                    room = new Room { Name = key, PhysicalCapacity = 30, MaxSimultaneousGroups = 1 };
                    rooms[key] = room;
                }
                item.RoomId = room.Id;
            }
            if (r.SplitSubgroups)
            {
                if (!groups.TryGetValue(cls.Id, out var pair))
                {
                    pair = (new StudentGroup { ClassId = cls.Id, Name = "A" },
                            new StudentGroup { ClassId = cls.Id, Name = "B" });
                    groups[cls.Id] = pair;
                }
                var teacherB = Teach(r.SplitTeacherBName!);
                splitTeachers[item.Id] = (teacher.Id, teacherB.Id);
            }
            curriculum.Add(item);
        }

        notes.Add($"Классов: {classes.Count}, учителей: {teachers.Count}, " +
            $"предметов: {subjects.Count}, строк нагрузки: {curriculum.Count}, " +
            $"сплитов: {splitTeachers.Count}, кабинетов: {rooms.Count}.");
        if (groups.Count > 0)
            notes.Add("Подгруппы A/B созданы по одной паре на класс (разные деления по предметам — позже).");

        return new SchoolData(academicYearId,
            classes.Values.ToList(), teachers.Values.ToList(), subjects.Values.ToList(),
            curriculum, groups.Values.SelectMany(g => new[] { g.A, g.B }).ToList(),
            rooms.Values.ToList(), splitTeachers, daysCount, slotsPerDay, notes);
    }
}
