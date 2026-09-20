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
    /// <summary>P1: гибкие настройки R1–R9, применённые при импорте (дефолт — Empty).</summary>
    public FlexDataset Flex { get; init; } = FlexDataset.Empty;

    /// <summary>P-DAYOFF: недоступность учителей (из колонок UnavailDays/UnavailSlots).</summary>
    public IReadOnlyList<TeacherDayOff> DaysOff { get; init; } = [];

    /// <inheritdoc cref="DaysOff"/>
    public IReadOnlyList<TeacherUnavailability> Unavailability { get; init; } = [];

    public ProblemInput ToProblemInput()
    {
        // P2: flex → problem (имена → Id; неизвестные имена назначений пропускаем:
        // P3-UI предлагает только существующие; молчаливого создания сущностей нет).
        TeacherAssignment? ResolveAssign(TeacherAssignRow a)
        {
            if (a.Scope == AssignmentScope.None) return null;
            var t = Teachers.FirstOrDefault(x =>
                string.Equals(x.Name, a.TeacherName, StringComparison.OrdinalIgnoreCase));
            var s = Subjects.FirstOrDefault(x =>
                string.Equals(x.Name, a.SubjectName, StringComparison.OrdinalIgnoreCase));
            if (t is null || s is null) return null;
            Guid? classId = null;
            if (a.Scope == AssignmentScope.Class)
            {
                var c = Classes.FirstOrDefault(x =>
                    a.ClassName is not null &&
                    string.Equals(x.Name, a.ClassName, StringComparison.OrdinalIgnoreCase));
                if (c is null) return null;
                classId = c.Id;
            }
            if (a.Scope == AssignmentScope.Parallel && a.Grade is null) return null;
            return new TeacherAssignment
            {
                TeacherId = t.Id, SubjectId = s.Id, Scope = a.Scope,
                ClassId = classId, Grade = a.Grade,
            };
        }
        var common = Flex.CommonLesson;
        return new ProblemInput(
            Classes, Teachers, Subjects, Curriculum, Groups, DaysOff, Unavailability,
            DaysCount: DaysCount, SlotsPerDay: SlotsPerDay,
            SplitTeachers: new Dictionary<Guid, (Guid, Guid)>(SplitTeachers),
            rooms: Rooms, // P0-8: кабинеты из импорта обязаны доходить до solver (было: дроп в []).
            commonLesson: common is null ? null : new CommonLesson
            {
                Enabled = common.Enabled, DayIndex = common.DayIndex,
                SlotIndex = common.SlotIndex, SlotIndexShift2 = common.SlotIndexShift2,
                GradesCsv = common.GradesCsv,
                UseOwnRooms = common.UseOwnRooms,
            },
            assignments: Flex.Assignments
                .Select(ResolveAssign).Where(a => a is not null).Cast<TeacherAssignment>().ToList(),
            flex: Flex.Settings.ToSettings());
    }
}

public static class SchoolDataImporter
{
    /// <summary>B5: человеческие сокращения → официальные РБ-названия (SUBJECTS_RB.md §1).
    /// Неизвестное НЕ трогаем (создаётся как есть); каждое применение пишется в Notes,
    /// чтобы завуч видел «что исправить» в Excel. Fail-loud пути не меняются.</summary>
    public static readonly IReadOnlyDictionary<string, string> SubjectAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Матем"] = "Математика",
            ["ИЗО"] = "Изобразительное искусство",
            ["Физра"] = "Физическая культура и здоровье",
            ["Физкультура"] = "Физическая культура и здоровье",
            ["Труд"] = "Трудовое обучение",
            ["Технология"] = "Трудовое обучение",
            ["ОБЖ"] = "Основы безопасности жизнедеятельности",
            ["Окружающий мир"] = "Человек и мир",
            ["Обществознание"] = "Обществоведение",
        };

    public static void ExportTemplate(Stream destination) =>
        ExcelLoadExchange.ExportLoad(destination, []);

    public static SchoolData Import(
        Guid academicYearId,
        IReadOnlyList<LoadRow> rows,
        int daysCount = 5,
        int slotsPerDay = 7,
        FlexDataset? flex = null)
    {
        if (rows.Count == 0)
            throw new InvalidOperationException("Файл не содержит строк нагрузки.");
        if (daysCount <= 0 || slotsPerDay <= 0)
            throw new InvalidOperationException("Дни и уроки в день должны быть положительными.");
        flex ??= FlexDataset.Empty;

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
                // R2/R4: явная конфигурация класса бьёт автопарсинг имени.
                var cfg = flex.Classes.FirstOrDefault(x =>
                    string.Equals(x.ClassName, key, StringComparison.OrdinalIgnoreCase));
                int grade = cfg?.Grade ?? HourResolution.ParseGrade(key);
                if (grade < 0 || grade > 12)
                    throw new InvalidOperationException(
                        $"Класс '{key}': параллель — 0..12 (задано {grade}).");
                int count = cfg?.StudentCount ?? 25;
                if (count <= 0)
                    throw new InvalidOperationException(
                        $"Класс '{key}': учеников должно быть больше 0.");
                c = new SchoolClass
                {
                    AcademicYearId = academicYearId, Name = key,
                    Grade = grade, StudentCount = count
                };
                if (cfg?.ClassTeacherName is not null)
                    c.ClassTeacherId = Teach(cfg.ClassTeacherName).Id;
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
            if (SubjectAliases.TryGetValue(key, out var official))
            {
                notes.Add($"«{key}» распознано как «{official}» — " +
                    "в Excel лучше писать официальное название.");
                key = official;
            }
            if (!subjects.TryGetValue(key, out var s))
            {
                s = new Subject { Name = key, MaxPerDay = 2 };
                // P-PE-FLAG: явный флаг из официального названия (после алиасов —
                // «Физра» уже стала официальным именем выше). Name-matching
                // запрещён только для произвольных строк; официальное имя — факт.
                if (string.Equals(key, "Физическая культура и здоровье",
                        StringComparison.OrdinalIgnoreCase))
                    s.IsPhysicalEducation = true;
                // R6: явная сложность предмета (1..10) бьёт дефолт 5.
                var diff = flex.SubjectDifficulty.FirstOrDefault(x =>
                    string.Equals(x.SubjectName, key, StringComparison.OrdinalIgnoreCase));
                if (diff is not null)
                {
                    if (diff.Difficulty < 1 || diff.Difficulty > 10)
                        throw new InvalidOperationException(
                            $"Предмет '{key}': сложность — 1..10 (задано {diff.Difficulty}).");
                    s.Difficulty = diff.Difficulty;
                }
                subjects[key] = s;
            }
            return s;
        }

        foreach (var r in rows)
        {
            var cls = Cls(r.ClassName);
            var subj = Subj(r.SubjectName);
            var teacher = Teach(r.TeacherName);
            // R2: часы по приоритету Класс > Параллель > Предмет-дефолт > строка.
            int hours = HourResolution.ResolveHours(cls.Name, cls.Grade, subj.Name,
                r.HoursPerWeek, flex.HourNorms, flex.HourOverrides);
            var item = new CurriculumItem
            {
                ClassId = cls.Id, SubjectId = subj.Id, TeacherId = teacher.Id,
                HoursPerWeek = hours, SplitSubgroups = r.SplitSubgroups,
            };
            if (r.RoomName is not null)
            {
                var key = r.RoomName.Trim();
                if (!rooms.TryGetValue(key, out var room))
                {
                    room = new Room { Name = key, PhysicalCapacity = 30, MaxSimultaneousGroups = 1 };
                    // R1/R5: конфигурация кабинета (режим + вместимость).
                    var rcfg = flex.Rooms.FirstOrDefault(x =>
                        string.Equals(x.RoomName, key, StringComparison.OrdinalIgnoreCase));
                    if (rcfg is not null)
                    {
                        if (rcfg.MaxGroups < 1)
                            throw new InvalidOperationException(
                                $"Кабинет '{key}': максимум групп должен быть ≥ 1.");
                        if (rcfg.DesiredGroups < 0 || rcfg.DesiredGroups > rcfg.MaxGroups)
                            throw new InvalidOperationException(
                                $"Кабинет '{key}': желательно групп — 0 (не задано) или 1..{rcfg.MaxGroups} " +
                                $"(задано {rcfg.DesiredGroups}).");
                        room.IsManualOnly = rcfg.IsManualOnly;
                        if (rcfg.OnlySubjectName is not null)
                            room.OnlySubjectId = Subj(rcfg.OnlySubjectName).Id;
                        room.MaxSimultaneousGroups = rcfg.MaxGroups;
                        room.DesiredGroups = rcfg.DesiredGroups;
                        room.CountSubgroupAsGroup = rcfg.CountSubgroupAsGroup;
                    }
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

        // P-DAYOFF: недоступность учителей (UnavailDays/UnavailSlots строк).
        // Дни — 1-based номера через запятую → DayIndex; слоты — как есть.
        // Объединение по учителю со всех его строк; мусор — громкая ошибка.
        var offDays = new Dictionary<string, SortedSet<int>>(StringComparer.OrdinalIgnoreCase);
        var offSlots = new Dictionary<string, SortedSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var tname = r.TeacherName.Trim();
            if (!string.IsNullOrWhiteSpace(r.UnavailDays))
                foreach (int d in ParseDays(r.UnavailDays!, tname, daysCount))
                {
                    if (!offDays.TryGetValue(tname, out var set))
                        offDays[tname] = set = [];
                    set.Add(d);
                }
            if (!string.IsNullOrWhiteSpace(r.UnavailSlots))
                foreach (int s in ParseSlots(r.UnavailSlots!, tname, slotsPerDay))
                {
                    if (!offSlots.TryGetValue(tname, out var set))
                        offSlots[tname] = set = [];
                    set.Add(s);
                }
        }
        var daysOff = new List<TeacherDayOff>();
        var unavailability = new List<TeacherUnavailability>();
        foreach (var (tname, set) in offDays)
        {
            var t = Teach(tname);
            daysOff.AddRange(set.Select(d => new TeacherDayOff { TeacherId = t.Id, DayIndex = d }));
        }
        foreach (var (tname, set) in offSlots)
        {
            // Слот без дня = «этот урок ежедневно»: раскрываем на все дни
            // (движок матчит точное (DayIndex, SlotIndex), см. ProblemBuilder).
            var t = Teach(tname);
            foreach (int d in Enumerable.Range(0, daysCount))
                unavailability.AddRange(set.Select(s => new TeacherUnavailability
                {
                    TeacherId = t.Id, DayIndex = d, SlotIndex = s, Kind = AvailabilityKind.Forbidden
                }));
        }
        if (daysOff.Count > 0 || unavailability.Count > 0)
            notes.Add($"Недоступность учителей: дней — {daysOff.Count}, слотов — {unavailability.Count}.");

        // R7: один учитель на (класс,предмет). Сплиты освобождены (A/B — штатно два учителя).
        // Режим из FlexSettings (дефолт HardClass, D-34); Soft/Off — только заметка.
        var mode = flex.Settings.AssignMode;
        if (mode is TeacherAssignMode.HardClass or TeacherAssignMode.HardParallel)
        {
            var classNameOf = classes.ToDictionary(kv => kv.Value.Id, kv => kv.Value.Name);
            var gradeOf = classes.ToDictionary(kv => kv.Value.Id, kv => kv.Value.Grade);
            var teacherNameOf = teachers.ToDictionary(kv => kv.Value.Id, kv => kv.Value.Name);
            var subjectNameOf = subjects.ToDictionary(kv => kv.Value.Id, kv => kv.Value.Name);
            IEnumerable<IGrouping<string, CurriculumItem>> dupGroups = mode == TeacherAssignMode.HardClass
                ? curriculum.Where(i => !i.SplitSubgroups)
                    .GroupBy(i => $"{i.ClassId:D}|{i.SubjectId:D}")
                : curriculum.Where(i => !i.SplitSubgroups)
                    .GroupBy(i => $"{gradeOf[i.ClassId]}|{i.SubjectId:D}");
            foreach (var g in dupGroups)
            {
                var who = g.Select(i => i.TeacherId).Distinct().ToList();
                if (who.Count <= 1) continue;
                var first = g.First();
                string where = mode == TeacherAssignMode.HardClass
                    ? $"класс '{classNameOf[first.ClassId]}', предмет '{subjectNameOf[first.SubjectId]}'"
                    : $"параллель {gradeOf[first.ClassId]}, предмет '{subjectNameOf[first.SubjectId]}'";
                throw new InvalidOperationException(
                    $"Закрепление учителей ({(mode == TeacherAssignMode.HardClass ? "класс" : "параллель")}): " +
                    $"{where} ведут несколько учителей: " +
                    $"{string.Join(", ", who.Select(t => $"'{teacherNameOf[t]}'"))}. " +
                    $"Оставьте одного или переключите режим в настройках.");
            }
        }

        notes.Add($"Классов: {classes.Count}, учителей: {teachers.Count}, " +
            $"предметов: {subjects.Count}, строк нагрузки: {curriculum.Count}, " +
            $"сплитов: {splitTeachers.Count}, кабинетов: {rooms.Count}.");
        if (groups.Count > 0)
            notes.Add("Подгруппы A/B созданы по одной паре на класс (разные деления по предметам — позже).");

        return new SchoolData(academicYearId,
            classes.Values.ToList(), teachers.Values.ToList(), subjects.Values.ToList(),
            curriculum, groups.Values.SelectMany(g => new[] { g.A, g.B }).ToList(),
            rooms.Values.ToList(), splitTeachers, daysCount, slotsPerDay, notes)
        { Flex = flex, DaysOff = daysOff, Unavailability = unavailability };
    }

    private static IReadOnlyList<int> ParseDays(string raw, string teacher, int daysCount)
    {
        var out_ = new SortedSet<int>();
        foreach (var part in raw.Split(',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, out int v) || v < 1 || v > daysCount)
                throw new InvalidOperationException(
                    $"Учитель '{teacher}': НеДоступенДни — номера дней 1..{daysCount} " +
                    $"через запятую (задано '{part}').");
            out_.Add(v - 1);
        }
        return out_.ToList();
    }

    private static IReadOnlyList<int> ParseSlots(string raw, string teacher, int slotsPerDay)
    {
        var out_ = new SortedSet<int>();
        foreach (var part in raw.Split(',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, out int v) || v < 1 || v > slotsPerDay)
                throw new InvalidOperationException(
                    $"Учитель '{teacher}': НеДоступенСлоты — номера уроков 1..{slotsPerDay} " +
                    $"через запятую (задано '{part}').");
            out_.Add(v);
        }
        return out_.ToList();
    }
}
