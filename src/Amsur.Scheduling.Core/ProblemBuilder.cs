namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// Вход строителя для упрощённой V2-модели (P0).
// Отличие от V1: нет Lesson/SchoolDay/TimeSlot-сетки со TimeOnly —
// используется (DaysCount × SlotsPerDay) + DayOff/Unavailability.
// Расширение CurriculumItem.HoursPerWeek → LessonOccurrence — здесь (порт семантики V1).
public sealed class ProblemInput
{
    public IReadOnlyList<SchoolClass> Classes { get; }
    public IReadOnlyList<Teacher> Teachers { get; }
    public IReadOnlyList<Subject> Subjects { get; }
    public IReadOnlyList<CurriculumItem> Curriculum { get; }
    public IReadOnlyList<StudentGroup> Groups { get; }
    public IReadOnlyList<TeacherDayOff> DaysOff { get; }
    public IReadOnlyList<TeacherUnavailability> Unavailability { get; }
    public IReadOnlyList<LessonRelation> Relations { get; }
    public IReadOnlyList<Room> Rooms { get; }
    public IReadOnlyList<RoomCapability> RoomCaps { get; }
    public int DaysCount { get; }
    public int SlotsPerDay { get; }

    // Для сплитов A/B: CurriculumItemId -> (teacherA, teacherB).
    public IReadOnlyDictionary<Guid, (Guid TeacherA, Guid TeacherB)> SplitTeachers { get; }

    // Посменные слоты (D-28): ClassId -> допустимые номера уроков.
    // Например 1-я смена 1..7, 2-я смена 8..14 при SlotsPerDay=14.
    // Пусто/отсутствует = все слоты (односменная школа, backward compatible).
    public IReadOnlyDictionary<Guid, IReadOnlyList<int>> ClassSlots { get; }

    // Смены школы для split teacher-gap (S5): null = дефолт по SlotsPerDay
    // (14 → [(1,7),(8,14)], иначе одна полоса [(1,SP)] = старое поведение).
    public IReadOnlyList<ShiftBand>? ShiftBands { get; }

    // R3–R8 (P2): общий урок (синтез occurrence), закрепления, гибкие настройки.
    // null = выключено/нейтрально (поведение до R1–R9).
    public CommonLesson? CommonLesson { get; }
    public IReadOnlyList<TeacherAssignment> Assignments { get; }
    public FlexSettings Flex { get; }

    // E3 perturbation: StableKey -> запрещённые (день, слот). Применяется ДО sync-пересечения.
    public IReadOnlyDictionary<string, IReadOnlySet<(int Day, int Slot)>> ExcludedPairs { get; }

    public ProblemInput(
        IReadOnlyList<SchoolClass> classes,
        IReadOnlyList<Teacher> teachers,
        IReadOnlyList<Subject> subjects,
        IReadOnlyList<CurriculumItem> curriculum,
        IReadOnlyList<StudentGroup> groups,
        IReadOnlyList<TeacherDayOff> daysOff,
        IReadOnlyList<TeacherUnavailability> unavailability,
        int DaysCount = 5,
        int SlotsPerDay = 7,
        IReadOnlyDictionary<Guid, (Guid, Guid)>? SplitTeachers = null,
        IReadOnlyList<LessonRelation>? relations = null,
        IReadOnlyList<Room>? rooms = null,
        IReadOnlyList<RoomCapability>? roomCaps = null,
        IReadOnlyDictionary<string, IReadOnlySet<(int Day, int Slot)>>? excludedPairs = null,
        IReadOnlyDictionary<Guid, IReadOnlyList<int>>? classSlots = null,
        IReadOnlyList<ShiftBand>? shiftBands = null,
        CommonLesson? commonLesson = null,
        IReadOnlyList<TeacherAssignment>? assignments = null,
        FlexSettings? flex = null)
    {
        Classes = classes;
        Teachers = teachers;
        Subjects = subjects;
        Curriculum = curriculum;
        Groups = groups;
        DaysOff = daysOff;
        Unavailability = unavailability;
        this.DaysCount = DaysCount;
        this.SlotsPerDay = SlotsPerDay;
        this.SplitTeachers = SplitTeachers ?? new Dictionary<Guid, (Guid, Guid)>();
        Relations = relations ?? [];
        Rooms = rooms ?? [];
        RoomCaps = roomCaps ?? [];
        ExcludedPairs = excludedPairs ?? new Dictionary<string, IReadOnlySet<(int Day, int Slot)>>();
        ClassSlots = classSlots ?? new Dictionary<Guid, IReadOnlyList<int>>();
        ShiftBands = shiftBands;
        CommonLesson = commonLesson;
        Assignments = assignments ?? [];
        Flex = flex ?? FlexSettings.Neutral;
    }

    /// <summary>Дефолтные смены по сетке (S5): 14 слотов → две смены, иначе одна.</summary>
    public static List<ShiftBand> DefaultShiftBands(int slotsPerDay) =>
        slotsPerDay == 14
            ? [new ShiftBand(1, 7), new ShiftBand(8, 14)]
            : [new ShiftBand(1, slotsPerDay)];
}

public static class ProblemBuilder
{
    public static (SchedulingProblem? Problem, IReadOnlyList<string> Errors) Build(
        ProblemInput input, SolverOptions? options = null)
    {
        var errors = new List<string>();
        var clsById = input.Classes.ToDictionary(c => c.Id);
        var teacherById = input.Teachers.ToDictionary(t => t.Id);
        var subjById = input.Subjects.ToDictionary(s => s.Id);

        var occurrences = new List<LessonOccurrence>();
        var groupParents = input.Groups.ToDictionary(g => g.Id, g => g.ClassId);
        var groupNames = input.Groups.ToDictionary(g => g.Id, g => g.Name);
        // E3 StableKey: Class|Subject|Teacher|Group|#hour — детерминирован между сборками.
        string Key(Guid classId, Guid subjectId, Guid teacherId, Guid? groupId, int hour) =>
            $"{clsById[classId].Name}|{subjById[subjectId].Name}|{teacherById[teacherId].Name}|" +
            $"{(groupId.HasValue && groupNames.TryGetValue(groupId.Value, out var gn) ? gn : "Whole")}#{hour}";

        foreach (var item in input.Curriculum)
        {
            if (!clsById.ContainsKey(item.ClassId)) { errors.Add($"CurriculumItem {item.Id}: unknown class."); continue; }
            if (!subjById.ContainsKey(item.SubjectId)) { errors.Add($"CurriculumItem {item.Id}: unknown subject."); continue; }
            if (!teacherById.ContainsKey(item.TeacherId)) { errors.Add($"CurriculumItem {item.Id}: unknown teacher."); continue; }
            if (item.HoursPerWeek <= 0) { errors.Add($"CurriculumItem {item.Id}: bad hours."); continue; }

            if (!item.SplitSubgroups)
            {
                for (int h = 0; h < item.HoursPerWeek; h++)
                    occurrences.Add(new LessonOccurrence
                    {
                        // E11: Id детерминирован из StableKey (пересборки одного входа
                        // дают те же Id → персист/правка переживают перезапуск).
                        Id = StableId(Key(item.ClassId, item.SubjectId, item.TeacherId, null, h)),
                        CurriculumItemId = item.Id, ClassId = item.ClassId,
                        SubjectId = item.SubjectId, TeacherId = item.TeacherId,
                        StableKey = Key(item.ClassId, item.SubjectId, item.TeacherId, null, h),
                    });
            }
            else
            {
                // Сплит A/B: нужны ровно 2 группы класса + пара учителей.
                // N>2 — громкая ошибка (молчаливого Take(2) запрещено): Splits v2 — P1.
                var classGroups = input.Groups.Where(g => g.ClassId == item.ClassId).ToList();
                if (classGroups.Count > 2)
                { errors.Add($"CurriculumItem {item.Id}: {classGroups.Count} groups need Splits v2 (P1), only A/B supported."); continue; }
                var groups = classGroups.Take(2).ToList();
                if (groups.Count < 2) { errors.Add($"CurriculumItem {item.Id}: split needs 2 groups."); continue; }
                if (!input.SplitTeachers.TryGetValue(item.Id, out var pair))
                { errors.Add($"CurriculumItem {item.Id}: split teachers missing."); continue; }
                if (!teacherById.ContainsKey(pair.TeacherA) || !teacherById.ContainsKey(pair.TeacherB))
                { errors.Add($"CurriculumItem {item.Id}: split teacher unknown."); continue; }
                if (pair.TeacherA == pair.TeacherB)
                { errors.Add($"Sync group {item.Id}: one teacher covers both halves."); continue; }
                for (int h = 0; h < item.HoursPerWeek; h++)
                {
                    var sync = Guid.NewGuid(); // per hour-instance (INV-03)
                    occurrences.Add(new LessonOccurrence
                    {
                        Id = StableId(Key(item.ClassId, item.SubjectId, pair.TeacherA, groups[0].Id, h)),
                        CurriculumItemId = item.Id, ClassId = item.ClassId,
                        SubjectId = item.SubjectId, TeacherId = pair.TeacherA,
                        GroupId = groups[0].Id, SyncGroupId = sync,
                        StableKey = Key(item.ClassId, item.SubjectId, pair.TeacherA, groups[0].Id, h),
                    });
                    occurrences.Add(new LessonOccurrence
                    {
                        Id = StableId(Key(item.ClassId, item.SubjectId, pair.TeacherB, groups[1].Id, h)),
                        CurriculumItemId = item.Id, ClassId = item.ClassId,
                        SubjectId = item.SubjectId, TeacherId = pair.TeacherB,
                        GroupId = groups[1].Id, SyncGroupId = sync,
                        StableKey = Key(item.ClassId, item.SubjectId, pair.TeacherB, groups[1].Id, h),
                    });
                }
            }
        }

        if (errors.Count > 0) return (null, errors);

        // P2/R3: общий урок (классный час) — синтез occurrence с ОБЩИМ SyncGroupId
        // на все классы параллелей. Дальше работает штатная N-механика: пересечение
        // доменов, sync-equality в CP-SAT (= t), frozen в LS, same-start в валидаторе.
        // Кабинет подбирает solver (каждому классу свой — greedy least-loaded);
        // выбор конкретного кабинета — ручная правка (P3). UseOwnRooms=false
        // (сбор всех в один зал) — только через P3-выбор зала, здесь fail-loud.
        // Двухсменка (MidSchool): классы, чья смена не содержит SlotIndex, идут
        // второй sync-группой в SlotIndexShift2 (0 = нет второй группы, как раньше).
        // Группы в разное время → seenTeachers и syncId у каждой свои.
        var commonLesson = input.CommonLesson;
        var commonOccIds = new HashSet<Guid>();
        var commonSlotByOcc = new Dictionary<Guid, int>();
        if (commonLesson is not null && commonLesson.Enabled)
        {
            if (!commonLesson.UseOwnRooms)
            {
                errors.Add("CommonLesson: сбор всех классов в один зал задаётся выбором зала (P3).");
                return (null, errors);
            }
            var targets = input.Classes.Where(c => commonLesson.Grades.Contains(c.Grade)).ToList();
            if (targets.Count == 0)
            {
                errors.Add($"CommonLesson: классы параллелей [{commonLesson.GradesCsv}] не найдены.");
                return (null, errors);
            }
            var hourSubject = subjById.Values.FirstOrDefault(s =>
                string.Equals(s.Name, "Классный час", StringComparison.OrdinalIgnoreCase));
            if (hourSubject is null)
            {
                hourSubject = new Subject { Name = "Классный час", Difficulty = 1, MaxPerDay = 1 };
                subjById[hourSubject.Id] = hourSubject;
            }
            IReadOnlyList<int> BandOf(SchoolClass c) => input.ClassSlots.GetValueOrDefault(
                c.Id, Enumerable.Range(1, input.SlotsPerDay).ToList());
            var groupA = targets.Where(c => BandOf(c).Contains(commonLesson.SlotIndex)).ToList();
            var groupB = commonLesson.SlotIndexShift2 > 0
                ? targets.Except(groupA)
                    .Where(c => BandOf(c).Contains(commonLesson.SlotIndexShift2)).ToList()
                : new List<SchoolClass>();
            var homeless = targets.Except(groupA).Except(groupB).ToList();
            if (homeless.Count > 0)
            {
                string slots = commonLesson.SlotIndexShift2 > 0
                    ? $"уроки {commonLesson.SlotIndex}/{commonLesson.SlotIndexShift2}"
                    : $"урок {commonLesson.SlotIndex}";
                foreach (var cls in homeless.OrderBy(c => c.Name, StringComparer.Ordinal))
                    errors.Add($"CommonLesson: {slots} вне смены класса '{cls.Name}'.");
                return (null, errors);
            }
            void Synthesize(IReadOnlyList<SchoolClass> group, Guid syncId, int slot)
            {
                var seenTeachers = new HashSet<Guid>();
                foreach (var cls in group.OrderBy(c => c.Name, StringComparer.Ordinal))
                {
                    if (!cls.ClassTeacherId.HasValue ||
                        !teacherById.ContainsKey(cls.ClassTeacherId.Value))
                    {
                        errors.Add($"CommonLesson: у класса '{cls.Name}' нет классного руководителя.");
                        continue;
                    }
                    var tid = cls.ClassTeacherId.Value;
                    if (!seenTeachers.Add(tid))
                    {
                        errors.Add($"CommonLesson: учитель ведёт классный час в двух классах " +
                            $"(sync требует разных учителей; общий зал — P3).");
                        continue;
                    }
                    var key = $"{cls.Name}|{hourSubject.Name}|{teacherById[tid].Name}|Whole#0";
                    var co = new LessonOccurrence
                    {
                        Id = StableId(key),
                        CurriculumItemId = StableId("amsur-common-item|" + cls.Name),
                        ClassId = cls.Id, SubjectId = hourSubject.Id, TeacherId = tid,
                        SyncGroupId = syncId, StableKey = key,
                    };
                    occurrences.Add(co);
                    commonOccIds.Add(co.Id);
                    commonSlotByOcc[co.Id] = slot;
                }
            }
            var syncA = StableId($"amsur-common-sync|{commonLesson.DayIndex}|{commonLesson.SlotIndex}");
            Synthesize(groupA, syncA, commonLesson.SlotIndex);
            if (groupB.Count > 0)
            {
                var syncB = StableId(
                    $"amsur-common-sync2|{commonLesson.DayIndex}|{commonLesson.SlotIndexShift2}");
                Synthesize(groupB, syncB, commonLesson.SlotIndexShift2);
            }
            if (errors.Count > 0) return (null, errors);
        }

        // Дубли StableKey (напр. два класса с одним именем) давали бы дублирующиеся
        // детерминированные Id → громкая ошибка вместо падения словарей ниже.
        foreach (var k in occurrences.GroupBy(o => o.StableKey)
                     .Where(g => g.Count() > 1).Select(g => g.Key))
            errors.Add($"Duplicate StableKey '{k}': проверьте уникальность имён классов/предметов/учителей.");
        if (errors.Count > 0) return (null, errors);

        bool IsForbidden(Guid teacherId, int day, int slot) =>
            input.DaysOff.Any(d => d.TeacherId == teacherId && d.DayIndex == day) ||
            input.Unavailability.Any(u => u.TeacherId == teacherId && u.DayIndex == day &&
                u.SlotIndex == slot && u.Kind == AvailabilityKind.Forbidden);

        var allowedDays = new Dictionary<Guid, List<int>>();
        var allowedSlots = new Dictionary<Guid, List<int>>();
        foreach (var occ in occurrences)
        {
            var days = new List<int>();
            for (int d = 0; d < input.DaysCount; d++)
            {
                bool anySlot = false;
                for (int s = 1; s <= input.SlotsPerDay; s++)
                    if (!IsForbidden(occ.TeacherId, d, s)) { anySlot = true; break; }
                if (anySlot) days.Add(d);
            }
            if (days.Count == 0)
            {
                errors.Add($"Occurrence {occ.Id}: no allowed placements — teacher unavailable in all slots.");
                continue;
            }
            allowedDays[occ.Id] = days;
            // Слоты: пересечение учитель-доступности и посменных слотов класса (D-28).
            var classBand = input.ClassSlots.GetValueOrDefault(
                occ.ClassId, Enumerable.Range(1, input.SlotsPerDay).ToList());
            var slots = classBand
                .Where(s => Enumerable.Range(0, input.DaysCount).Any(d => !IsForbidden(occ.TeacherId, d, s)))
                .ToList();
            if (slots.Count == 0)
            {
                errors.Add($"Occurrence {occ.Id}: class shift band empty for teacher availability.");
                continue;
            }
            allowedSlots[occ.Id] = slots;
        }

        // P2/R3: фиксация общего урока в заданной клетке (вне смены/доступности — громко).
        if (commonLesson is not null && commonLesson.Enabled)
        {
            if (commonLesson.DayIndex < 0 || commonLesson.DayIndex >= input.DaysCount)
                errors.Add($"CommonLesson: день {commonLesson.DayIndex + 1} вне сетки ({input.DaysCount} дн.).");
            foreach (var occ in occurrences.Where(o => commonOccIds.Contains(o.Id)))
            {
                int slot = commonSlotByOcc.GetValueOrDefault(occ.Id, commonLesson.SlotIndex);
                var band = input.ClassSlots.GetValueOrDefault(
                    occ.ClassId, Enumerable.Range(1, input.SlotsPerDay).ToList());
                if (!band.Contains(slot))
                    errors.Add($"CommonLesson: урок {slot} вне смены класса '{clsById[occ.ClassId].Name}'.");
                else if (IsForbidden(occ.TeacherId, commonLesson.DayIndex, slot))
                    errors.Add($"CommonLesson: классный руководитель недоступен в заданной клетке.");
                else
                {
                    allowedDays[occ.Id] = [commonLesson.DayIndex];
                    allowedSlots[occ.Id] = [slot];
                }
            }
            if (errors.Count > 0) return (null, errors);
        }

        // E3 perturbation-исключения (до sync-пересечения, по StableKey).
        // Точный pair-ban: days/slots-списки cartesian и ban не выразят,
        // поэтому запреты едут отдельно в BannedTimes (глобальные t), а здесь лишь
        // проверка непустоты остатка.
        var bannedTimes = new Dictionary<Guid, HashSet<int>>();
        foreach (var occ in occurrences)
        {
            if (!input.ExcludedPairs.TryGetValue(occ.StableKey, out var banned) || banned.Count == 0)
                continue;
            var pairs = new HashSet<(int, int)>(
                allowedDays[occ.Id].SelectMany(d =>
                    allowedSlots[occ.Id]
                        .Where(s => !IsForbidden(occ.TeacherId, d, s))
                        .Select(s => (d, s))));
            pairs.ExceptWith(banned);
            if (pairs.Count == 0)
            {
                errors.Add($"Occurrence {occ.StableKey}: perturbation excluded all placements.");
                continue;
            }
            bannedTimes[occ.Id] = banned
                .Select(p => p.Day * input.SlotsPerDay + p.Slot)
                .ToHashSet();
        }

        if (errors.Count > 0) return (null, errors);

        // Sync intersection: общий домен (день × слот) для членов группы.
        foreach (var grp in occurrences.Where(o => o.SyncGroupId.HasValue).GroupBy(o => o.SyncGroupId!.Value))
        {
            var members = grp.ToList();
            var common = new HashSet<(int, int)>(
                allowedDays[members[0].Id].SelectMany(d =>
                    allowedSlots[members[0].Id]
                        .Where(s => !IsForbidden(members[0].TeacherId, d, s))
                        .Select(s => (d, s))));
            foreach (var m in members.Skip(1))
            {
                var set = new HashSet<(int, int)>(
                    allowedDays[m.Id].SelectMany(d =>
                        allowedSlots[m.Id]
                            .Where(s => !IsForbidden(m.TeacherId, d, s))
                            .Select(s => (d, s))));
                common.IntersectWith(set);
            }
            if (common.Count == 0)
            {
                errors.Add($"SyncGroup {grp.Key}: members have no common time (disjoint availability).");
                continue;
            }
            var commonDays = common.Select(p => p.Item1).Distinct().OrderBy(d => d).ToList();
            var commonSlots = common.Select(p => p.Item2).Distinct().OrderBy(s => s).ToList();
            foreach (var m in members)
            {
                allowedDays[m.Id] = commonDays;
                allowedSlots[m.Id] = commonSlots;
            }
        }

        if (errors.Count > 0) return (null, errors);

        var problem = new SchedulingProblem
        {
            Occurrences = occurrences,
            Classes = clsById,
            Teachers = teacherById,
            Rooms = input.Rooms.ToDictionary(r => r.Id),
            Subjects = subjById,
            AllowedDays = allowedDays,
            AllowedSlots = allowedSlots,
            GroupParents = groupParents,
            Relations = input.Relations.ToList(),
            DaysCount = input.DaysCount,
            SlotsPerDay = input.SlotsPerDay,
            ShiftBands = input.ShiftBands is not null
                ? input.ShiftBands.ToList()
                : ProblemInput.DefaultShiftBands(input.SlotsPerDay),
            Options = options ?? new SolverOptions(),
            BannedTimes = bannedTimes,
            Flex = input.Flex,
            Assignments = input.Assignments.ToList(),
            RoomCaps = input.RoomCaps
                .GroupBy(c => (c.RoomId, c.SubjectId))
                .ToDictionary(g => g.Key, g => g.First().Kind),
        };
        return (problem, Array.Empty<string>());
    }

    /// <summary>
    /// Детерминированный Id из StableKey (MD5 → Guid, вариант/версия не важны:
    /// нужен только стабильный ключ идентичности, не безопасность).
    /// </summary>
    internal static Guid StableId(string stableKey)
    {
        var hash = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes("amsur-occ|" + stableKey));
        return new Guid(hash);
    }
}
