using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// Реальная школа СШ №8 по фото висящего расписания 5–11 (данные/, 20.09.2026):
// 24 класса (5–9 по 4, 10–11 по 2), 396 строк нагрузки, 769 ч/нед.
// Учителя на фото отсутствуют — плейсхолдеры "<Предмет>·<параллель>" (сверить с завучем).
// Корректирует ASSUME фикстуры OurSchoolTests: там 27 классов (5–7 по 5),
// смены "6,7 + 8Г/9Г во 2-й"; по фото: 24 класса, 2-я смена только 6–7-е
// (нумерация №6–12), 5-е и 8–11-е — 1-я. Сплит ин.яза есть и в 5-х.
// Файл: данные/НашаШкола_5-11_нагрузка.xlsx (генератор — данные/build_load_5-11.py).
public sealed class PhotoSchoolTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static string FindWorkbook()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var cand = Path.Combine(dir.FullName, "данные", "НашаШкола_5-11_нагрузка.xlsx");
            if (File.Exists(cand)) return cand;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("НашаШкола_5-11_нагрузка.xlsx не найден вверх от тестов.");
    }

    private static SchoolData LoadSchool()
    {
        using var fs = File.OpenRead(FindWorkbook());
        var rows = ExcelLoadExchange.ImportLoad(fs);
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows,
            daysCount: 5, slotsPerDay: 8);
        // Школа подтверждает: 8-урочные дни бывают (пиковые); дефолт импорта 6/день
        // иначе не вмещает 8Б (33ч) и 9Б (34ч) — честная настройка, не подгонка.
        foreach (var c in data.Classes) c.MaxLessonsPerDay = 8;
        return data;
    }

    [Fact]
    public void PhotoSchool_Import_Shape()
    {
        var data = LoadSchool();
        output.WriteLine(data.Notes[^1]);
        Assert.Equal(24, data.Classes.Count);
        Assert.Equal(396, data.Curriculum.Count);
        Assert.Equal(769, data.Curriculum.Sum(i => i.HoursPerWeek));
        // Сплиты: ин.яз/инф/труд/ДМП — двухгруппные строки обязаны иметь TeacherB.
        Assert.True(data.SplitTeachers.Count >= 60);
        // Каждый класс имеет классный час (Чт, первый урок смены — на всех фото).
        var hourId = data.Subjects.Single(s => s.Name == "Классный час").Id;
        Assert.Equal(24, data.Curriculum.Count(i => i.SubjectId == hourId));
    }

    [Fact]
    public void PhotoSchool_Greedy_Coverage()
    {
        var data = LoadSchool();
        var (problem, errors) = ProblemBuilder.Build(data.ToProblemInput(),
            new SolverOptions(MaxTimeSeconds: 8, NumSearchWorkers: 1,
                RandomSeed: 11, PresolveInPhaseA: false));
        Assert.Empty(errors);
        var g = GreedyPlacer.Place(problem!, 11);
        output.WriteLine($"PHOTO greedy: placed {g.Placed.Count}/{problem!.Occurrences.Count}");
        var bySubject = g.Unplaced
            .Select(id => problem.Occurrences.First(o => o.Id == id))
            .GroupBy(o => problem.Subjects[o.SubjectId].Name)
            .Select(grp => (Name: grp.Key, Count: grp.Count()))
            .OrderByDescending(x => x.Count).ToList();
        foreach (var (name, count) in bySubject.Take(10))
            output.WriteLine($"PHOTO greedy unplaced: {name} — {count}");
        var placements = g.Placed.Select(kv => new PlacedLesson
        {
            OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
            SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
        }).ToList();
        // Greedy-сырец: окна у учеников ожидаемы (их убирает CompactRepair
        // в боевом пайплайне) — фиксируем только покрытие и его структуру.
        var vrRaw = PlacementValidator.Validate(problem, placements);
        output.WriteLine($"PHOTO raw hard={vrRaw.HardViolations.Count} " +
            $"student-gap={vrRaw.HardViolations.Count(v => v.Code == "student-gap")}");
        // Боевой пайплайн ×3 seed (как оркестратор E4: multi-seed + лучший):
        // CompactRepair + LocalSearch VND. LS time-boxed → под нагрузкой
        // результат плавает, поэтому ворота — только на детерминированных
        // стадиях (greedy + repair), LS — logged evidence для замеров.
        var repairedOnce = CompactRepair.Repair(problem, placements);
        var vrRep = PlacementValidator.Validate(problem, repairedOnce.Placements);
        int repGaps = vrRep.HardViolations.Count(v =>
            v.Code is "student-gap" or "student-late-start");
        int rawGaps = vrRaw.HardViolations.Count(v =>
            v.Code is "student-gap" or "student-late-start");
        output.WriteLine($"PHOTO repair: gaps {rawGaps} -> {repGaps}");
        // Замер 20.09.2026: 21 -> 10/11. Repair обязан улучшать и держать ≤12.
        Assert.True(repGaps < rawGaps, "CompactRepair не уменьшил окна учеников.");
        Assert.True(repGaps <= 12,
            $"CompactRepair оставил {repGaps} ученических окон — хуже порога 12.");
        foreach (int seed in new[] { 11, 22, 33 })
        {
            var gSeed = GreedyPlacer.Place(problem, seed);
            var startSeed = gSeed.Placed.Select(kv => new PlacedLesson
            {
                OccurrenceId = kv.Key, DayIndex = kv.Value.Day,
                SlotIndex = kv.Value.Slot, RoomId = kv.Value.RoomId
            }).ToList();
            var repSeed = CompactRepair.Repair(problem, startSeed);
            var impSeed = LocalSearch.Improve(problem, repSeed.Placements,
                TimeSpan.FromSeconds(10), seed: seed);
            var vrSeed = PlacementValidator.Validate(problem, impSeed.Placements);
            int gaps = vrSeed.HardViolations.Count(v =>
                v.Code is "student-gap" or "student-late-start");
            // Evidence only (замеры в BENCHMARKS): 20.09.2026 — 5/7/5, soft ~2740–2950.
            output.WriteLine($"PHOTO pipeline seed={seed}: placed={gSeed.Placed.Count} " +
                $"soft={impSeed.SoftTotal} gaps={gaps} ms={impSeed.ElapsedMs}");
        }
        // Замер 20.09.2026: 878/884 (99.3%). Все 6 неразмещённых — 10А:
        // профильные пары (разные предметы подгрупп в один слот) формат Load
        // выразить не может (Splits v2 — backlog), движок видит их как 47
        // независимых occurrence при 40 слотах класса. Порог 870 — честный:
        // ниже означает регресс вне известного sync-зазора.
        Assert.True(g.Placed.Count >= 870,
            $"Greedy разместил {g.Placed.Count} — ниже порога 870, смотреть unplaced выше.");
    }
}
