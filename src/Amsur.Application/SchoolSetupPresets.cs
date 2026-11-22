using Amsur.Domain;

namespace Amsur.Application;

public sealed record ParallelSpec(int Grade, IReadOnlyList<string> Letters, int Shift = 1);

public sealed record StarterSubjectSpec(
    string SubjectName, int HoursPerWeek, bool SplitSubgroups = false, string? RoomName = null);

/// <summary>
/// Стартовый конструктор школы и групповые операции без Excel:
/// типовые учебные планы РБ (NEEDS-CHECK), генератор классов/учителей/кабинетов,
/// переименование учителя по всей нагрузке и Flex, смена класса.
/// </summary>
public static class SchoolSetupPresets
{
    public static IReadOnlyList<StarterSubjectSpec> StandardCurriculumHours(int grade)
    {
        if (grade <= 2)
        {
            return
            [
                new("Русский язык", 4),
                new("Русская литература", 2),
                new("Белорусский язык", 3),
                new("Белорусская литература", 2),
                new("Математика", 4),
                new("Человек и мир", 1),
                new("Изобразительное искусство", 1),
                new("Музыка", 1),
                new("Трудовое обучение", 1),
                new("Физическая культура и здоровье", 2, false, "Спортзал"),
            ];
        }
        if (grade <= 4)
        {
            return
            [
                new("Русский язык", 4),
                new("Русская литература", 2),
                new("Белорусский язык", 3),
                new("Белорусская литература", 2),
                new("Математика", 4),
                new("Иностранный язык", 2, SplitSubgroups: true),
                new("Человек и мир", 1),
                new("Трудовое обучение", 1),
                new("Физическая культура и здоровье", 2, false, "Спортзал"),
            ];
        }
        if (grade <= 6)
        {
            return
            [
                new("Математика", 5),
                new("Русский язык", 3),
                new("Русская литература", 2),
                new("Белорусский язык", 3),
                new("Белорусская литература", 2),
                new("Иностранный язык", 3, SplitSubgroups: true),
                new("История Беларуси", 2),
                new(grade == 5 ? "Человек и мир" : "География", 1),
                new(grade == 5 ? "Основы безопасности жизнедеятельности" : "Биология", 1),
                new(grade == 5 ? "Изобразительное искусство" : "Информатика", 1, false, grade == 5 ? null : "ИТ"),
                new("Трудовое обучение", 1),
                new("Физическая культура и здоровье", 2, false, "Спортзал"),
            ];
        }
        if (grade <= 9)
        {
            return
            [
                new("Алгебра", 3),
                new("Геометрия", 2),
                new("Русский язык", 3),
                new("Русская литература", 2),
                new("Белорусский язык", 2),
                new("Белорусская литература", 2),
                new("Иностранный язык", 3, SplitSubgroups: true),
                new("История Беларуси", 2),
                new("Всемирная история", 1),
                new("Физика", 2),
                new("Химия", 2),
                new("Биология", 2),
                new("География", 1),
                new("Информатика", 1, false, "ИТ"),
                new("Физическая культура и здоровье", 2, false, "Спортзал"),
            ];
        }
        return
        [
            new("Алгебра", 3),
            new("Геометрия", 2),
            new("Русский язык", 2),
            new("Русская литература", 2),
            new("Белорусский язык", 2),
            new("Белорусская литература", 2),
            new("Иностранный язык", 3, SplitSubgroups: true),
            new("История Беларуси", 2),
            new("Всемирная история", 1),
            new("Обществоведение", 1),
            new("Физика", 2),
            new("Химия", 2),
            new("Биология", 2),
            new("География", 1),
            new("Информатика", 1, false, "ИТ"),
            new("Допризывная и медицинская подготовка", 1),
            new("Физическая культура и здоровье", 2, false, "Спортзал"),
        ];
    }

    public static int StarterClassDailyLimit(int grade) =>
        grade <= 4 ? 5 : grade <= 6 ? 6 : 7;

    public static (IReadOnlyList<LoadRow> Rows, FlexDataset Flex) BuildStarterSchool(
        IReadOnlyList<ParallelSpec> parallels,
        bool includeSubjects = true,
        bool includeHomerooms = true)
    {
        var rows = new List<LoadRow>();
        var classes = new List<ClassConfigRow>();
        var classLimits = new List<ClassLimitRow>();
        var seenClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Держим нагрузку каждого учителя-заглушки <= 22 ч/нед, чтобы школа всегда влезала в сетку.
        var teacherHours = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var subjectCounters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var subjectPools = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        string AllocTeacher(string subject, int hours, string? excludeTeacher = null)
        {
            string key = subject switch
            {
                "Алгебра" or "Геометрия" => "Математика",
                _ => subject,
            };
            if (!subjectPools.TryGetValue(key, out var pool))
                subjectPools[key] = pool = [];

            foreach (var candidate in pool)
            {
                if (string.Equals(candidate, excludeTeacher, StringComparison.OrdinalIgnoreCase)) continue;
                if (teacherHours.GetValueOrDefault(candidate) + hours <= 22)
                {
                    teacherHours[candidate] = teacherHours.GetValueOrDefault(candidate) + hours;
                    return candidate;
                }
            }

            int idx = subjectCounters.GetValueOrDefault(key) + 1;
            subjectCounters[key] = idx;
            string created = $"Учитель {idx} ({ShortSubject(key)})";
            pool.Add(created);
            teacherHours[created] = hours;
            return created;
        }

        int peHours = 0;
        int itHours = 0;

        foreach (var p in parallels.OrderBy(x => x.Grade))
        {
            int grade = Math.Clamp(p.Grade, 1, 11);
            int? shift = p.Shift == 2 ? 2 : null;
            foreach (var rawLetter in p.Letters)
            {
                string letter = (rawLetter ?? "").Trim();
                if (letter.Length == 0) continue;
                string className = $"{grade}{letter}";
                if (!seenClasses.Add(className)) continue;

                classes.Add(new ClassConfigRow(className, null, grade, 25));
                classLimits.Add(new ClassLimitRow(className, StarterClassDailyLimit(grade)));

                IReadOnlyList<StarterSubjectSpec> plan = includeSubjects
                    ? StandardCurriculumHours(grade)
                    : [new("Математика", 4), new("Русский язык", 4)];

                string homeroom = $"Каб {className}";
                foreach (var spec in plan)
                {
                    string teacherA = AllocTeacher(spec.SubjectName, spec.HoursPerWeek);
                    string? teacherB = spec.SplitSubgroups
                        ? AllocTeacher(spec.SubjectName, spec.HoursPerWeek, excludeTeacher: teacherA)
                        : null;
                    string? room = spec.RoomName ?? (includeHomerooms ? homeroom : null);
                    if (string.Equals(room, "Спортзал", StringComparison.OrdinalIgnoreCase))
                        peHours += spec.HoursPerWeek * (spec.SplitSubgroups ? 2 : 1);
                    else if (string.Equals(room, "ИТ", StringComparison.OrdinalIgnoreCase))
                        itHours += spec.HoursPerWeek;

                    rows.Add(new LoadRow(
                        className,
                        spec.SubjectName,
                        spec.HoursPerWeek,
                        teacherA,
                        spec.SplitSubgroups,
                        teacherB,
                        room,
                        Shift: shift));
                }
            }
        }

        var rooms = new List<RoomConfigRow>();
        if (peHours > 20)
            rooms.Add(new RoomConfigRow("Спортзал", false, null,
                MaxGroups: Math.Clamp((peHours + 19) / 20, 2, 4), DesiredGroups: 1, CountSubgroupAsGroup: true));
        if (itHours > 25)
            rooms.Add(new RoomConfigRow("ИТ", false, null,
                MaxGroups: Math.Clamp((itHours + 24) / 25, 2, 4), DesiredGroups: 1, CountSubgroupAsGroup: false));

        var difficulties = rows
            .Select(r => r.SubjectName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new SubjectDifficultyRow(name,
                SchoolPresets.SubjectDifficulties.TryGetValue(name, out int d) ? d : 5))
            .ToList();

        var subjectLimits = rows.Any(r =>
            string.Equals(r.SubjectName, "Физическая культура и здоровье", StringComparison.OrdinalIgnoreCase))
            ? new List<SubjectLimitRow> { new("Физическая культура и здоровье", 1) }
            : new List<SubjectLimitRow>();

        var flex = FlexDataset.Empty with
        {
            Classes = classes,
            Rooms = rooms,
            ClassLimits = classLimits,
            SubjectDifficulty = difficulties,
            SubjectLimits = subjectLimits,
        };

        return (rows, flex);
    }

    public static (IReadOnlyList<LoadRow> Rows, FlexDataset Flex) RenameTeacher(
        IReadOnlyList<LoadRow> rows, FlexDataset flex, string oldName, string newName)
    {
        string from = (oldName ?? "").Trim();
        string to = (newName ?? "").Trim();
        if (from.Length == 0) throw new ArgumentException("Укажите текущее ФИО учителя.", nameof(oldName));
        if (to.Length == 0) throw new ArgumentException("Укажите новое ФИО учителя.", nameof(newName));
        if (string.Equals(from, to, StringComparison.Ordinal))
            return (rows.ToList(), flex);

        string Map(string name) => string.Equals(name, from, StringComparison.OrdinalIgnoreCase) ? to : name;

        var updatedRows = rows.Select(r =>
        {
            string main = Map(r.TeacherName);
            string? sec = r.SplitTeacherBName is null ? null : Map(r.SplitTeacherBName);
            if (r.SplitSubgroups && string.Equals(main, sec, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"В классе «{r.ClassName}» ({r.SubjectName}) учителя A и B совпадут в «{to}».");
            return r with { TeacherName = main, SplitTeacherBName = sec };
        }).ToList();

        var updatedClasses = flex.Classes.Select(c =>
            c.ClassTeacherName is not null && string.Equals(c.ClassTeacherName, from, StringComparison.OrdinalIgnoreCase)
                ? c with { ClassTeacherName = to }
                : c).ToList();

        var updatedAssigns = flex.Assignments
            .Select(a => a with { TeacherName = Map(a.TeacherName) })
            .GroupBy(a => (a.TeacherName.ToUpperInvariant(), a.SubjectName.ToUpperInvariant(), a.Scope,
                a.ClassName?.ToUpperInvariant() ?? "", a.Grade ?? -1))
            .Select(g => g.First())
            .ToList();

        var updatedLimits = (flex.TeacherLimits ?? [])
            .Select(l => l with { TeacherName = Map(l.TeacherName) })
            .GroupBy(l => l.TeacherName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();

        var updatedDays = (flex.TeacherDaysOff ?? [])
            .Select(d => d with { TeacherName = Map(d.TeacherName) })
            .GroupBy(d => (d.TeacherName.ToUpperInvariant(), d.DayIndex))
            .Select(g => g.First())
            .ToList();

        var updatedSlots = (flex.TeacherUnavailSlots ?? [])
            .Select(u => u with { TeacherName = Map(u.TeacherName) })
            .GroupBy(u => (u.TeacherName.ToUpperInvariant(), u.DayIndex, u.SlotIndex))
            .Select(g => g.First())
            .ToList();

        return (updatedRows, flex with
        {
            Classes = updatedClasses,
            Assignments = updatedAssigns,
            TeacherLimits = updatedLimits,
            TeacherDaysOff = updatedDays,
            TeacherUnavailSlots = updatedSlots,
        });
    }

    public static IReadOnlyList<LoadRow> ApplyClassShift(
        IReadOnlyList<LoadRow> rows, string className, int shift)
    {
        int? normShift = shift == 2 ? 2 : null;
        return rows.Select(r =>
            string.Equals(r.ClassName, className, StringComparison.OrdinalIgnoreCase)
                ? r with { Shift = normShift }
                : r).ToList();
    }

    private static string ShortSubject(string subject) => subject switch
    {
        "Физическая культура и здоровье" => "Физкультура",
        "Основы безопасности жизнедеятельности" => "ОБЖ",
        "Допризывная и медицинская подготовка" => "ДМП",
        "Изобразительное искусство" => "ИЗО",
        "Трудовое обучение" => "Труд",
        "Белорусский язык" => "Бел. яз.",
        "Белорусская литература" => "Бел. лит.",
        "Русский язык" => "Рус. яз.",
        "Русская литература" => "Рус. лит.",
        "Иностранный язык" => "Ин. яз.",
        _ => subject,
    };
}
