using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;
using ClosedXML.Excel;

namespace Amsur.Tests;

// Пакет 3: полный TF+LNS-прогон на сверенной нагрузке → АМСУР_движок_ФИНАЛ_v2.
// Старый ФИНАЛ не перезаписывается.
public sealed class FullRunV2(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void BuildAll_Reconciled_Shape()
    {
        var built = ScratchGen.BuildAll(11);
        var problem = built.Problem;
        output.WriteLine($"V2 occurrences={problem.Occurrences.Count}");
        // Структура не менялась (только учителя физики/астрономии 10А/11А) —
        // счёт как у ФИНАЛа 22.09.
        Assert.Equal(888, problem.Occurrences.Count);
        // Денискин 14ч в задаче (замороженное №2).
        var den = built.Data.Teachers.First(t => t.Name == "Денискин Е.В.");
        int denHours = built.Data.Curriculum.Where(i => i.TeacherId == den.Id).Sum(i => i.HoursPerWeek);
        output.WriteLine($"V2 Denis hours={denHours}");
        Assert.Equal(14, denHours);
        // 10А/11А физика+астрономия — только Денискин.
        var subjNames = built.Data.Subjects.ToDictionary(s => s.Id, s => s.Name);
        foreach (var clsName in new[] { "10А", "11А" })
        {
            var cls = built.Data.Classes.First(c => c.Name == clsName);
            foreach (var item in built.Data.Curriculum.Where(i => i.ClassId == cls.Id))
            {
                var sn = subjNames[item.SubjectId];
                if (sn is "Физика" or "Астрономия")
                    Assert.Equal(den.Id, item.TeacherId);
            }
        }
        // Классный час: 24 occurrence, пины Чт-1/Чт-7.
        var hourId = problem.Subjects.Values
            .First(s => string.Equals(s.Name, "Классный час", StringComparison.OrdinalIgnoreCase)).Id;
        var hours = problem.Occurrences.Where(o => o.SubjectId == hourId).ToList();
        Assert.Equal(24, hours.Count);
        var g = GreedyPlacer.Place(problem, 11);
        output.WriteLine($"V2 greedy placed={g.Placed.Count}/{problem.Occurrences.Count}");
        Assert.True(g.Placed.Count >= 800,
            $"Greedy разместил {g.Placed.Count} — ниже порога 800.");
    }

    // Пакет 3b: полный TF+LNS-прогон → АМСУР_движок_ФИНАЛ_v2.xlsx.
    // SKIP: прогон выполнен 23.09.2026 (v2 выгружен: 262 дыры, hard=0).
    // Для повтора убрать Skip (долгий: ~15 мин). Старый ФИНАЛ не трогается.
    [Fact(Skip = "Разовая генерация ФИНАЛ_v2 — артефакт уже выгружен.")]
    public void FullRun_TF_LNS_V2()
    {
        var tf = RuleResolver.Resolve("TEACHER_FRIENDLY");
        var m = ScratchGen.RunFullPipeline(output, "АМСУР_движок_ФИНАЛ_v2.xlsx", tf, 11);
        output.WriteLine($"V2FINAL placed={m.Placed}/{m.Total} gaps={m.Gaps} " +
            $"failedDays={m.FailedDays} pupilHard={m.PupilHard} hard={m.Hard} " +
            $"soft={m.Soft} dbl={m.Doubles} activeDays={m.ActiveDays}");
        Assert.Equal(m.Total, m.Placed);
        Assert.Equal(0, m.Hard);
        Assert.Equal(0, m.PupilHard);
    }

    // Deep-dive 23.09: быстрый старт итераций с готового ФИНАЛ_v2 без 15-мин
    // пересборки. Матчинг строк (класс,предмет,учитель) с occurrence: sync-пары —
    // целиком по общей клетке (в валидном листе она принадлежит только им).
    internal static string FindDataFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var cand = Path.Combine(dir.FullName, "данные", "тест_реал", name);
            if (File.Exists(cand)) return cand;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(name + " не найден.");
    }

    internal static List<PlacedLesson> ImportV2(SchedulingProblem problem, SchoolData data, string path)
    {
        var clsByName = data.Classes.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var subjByName = data.Subjects.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var teachByName = data.Teachers.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var roomByName = problem.Rooms.Values.ToDictionary(r => r.Name, StringComparer.Ordinal);
        var dayMap = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Пн"] = 0, ["Вт"] = 1, ["Ср"] = 2, ["Чт"] = 3, ["Пт"] = 4,
        };
        var rowsByKey = new Dictionary<(Guid, Guid, Guid), List<(int Day, int Slot, Guid? Room)>>();
        using (var wb = new XLWorkbook(path))
        {
            var ws = wb.Worksheet("Учителя");
            foreach (var row in ws.RowsUsed().Skip(1))
            {
                string tn = row.Cell(1).GetString().Trim();
                string dn = row.Cell(2).GetString().Trim();
                if (tn == "" || dn == "") continue;
                int slot = row.Cell(3).GetValue<int>();
                string cn = row.Cell(4).GetString().Trim();
                string sn = row.Cell(5).GetString().Trim();
                string rn = row.Cell(6).GetString().Trim();
                Guid? room = null;
                if (rn != "" && rn != "—" && rn != "-" && roomByName.TryGetValue(rn, out var rm))
                    room = rm.Id;
                var key = (clsByName[cn].Id, subjByName[sn].Id, teachByName[tn].Id);
                if (!rowsByKey.TryGetValue(key, out var l)) rowsByKey[key] = l = [];
                l.Add((dayMap[dn], slot, room));
            }
        }
        var placements = new List<PlacedLesson>();
        var syncGroups = problem.Occurrences
            .Where(o => o.SyncGroupId.HasValue)
            .GroupBy(o => o.SyncGroupId!.Value)
            .OrderBy(g => g.Min(o => o.StableKey), StringComparer.Ordinal)
            .ToList();
        foreach (var grp in syncGroups)
        {
            var members = grp.OrderBy(o => o.StableKey, StringComparer.Ordinal).ToList();
            var keys = members.Select(o => (o.ClassId, o.SubjectId, o.TeacherId)).ToList();
            var common = rowsByKey[keys[0]].Select(x => (x.Day, x.Slot)).Distinct()
                .Where(cell => keys.All(k => rowsByKey[k].Any(x => x.Day == cell.Day && x.Slot == cell.Slot)))
                .OrderBy(cell => cell).ToList();
            if (common.Count == 0)
                throw new InvalidOperationException("Sync-группа без общей клетки.");
            var (sday, sslot) = common[0];
            for (int i = 0; i < members.Count; i++)
            {
                var lst = rowsByKey[keys[i]];
                int ix = lst.FindIndex(x => x.Day == sday && x.Slot == sslot);
                if (ix < 0)
                    throw new InvalidOperationException("Нет строки halves.");
                var cell = lst[ix];
                lst.RemoveAt(ix);
                placements.Add(new PlacedLesson
                {
                    OccurrenceId = members[i].Id, DayIndex = cell.Day,
                    SlotIndex = cell.Slot, RoomId = cell.Room,
                });
            }
        }
        var synced = new HashSet<Guid>(syncGroups.SelectMany(g => g.Select(o => o.Id)));
        var occByKey = problem.Occurrences
            .Where(o => !synced.Contains(o.Id))
            .GroupBy(o => (o.ClassId, o.SubjectId, o.TeacherId))
            .ToDictionary(g => g.Key,
                g => g.OrderBy(o => o.StableKey, StringComparer.Ordinal).ToList());
        foreach (var (key, lst) in rowsByKey)
        {
            if (!occByKey.TryGetValue(key, out var occs)) continue;
            if (occs.Count != lst.Count)
                throw new InvalidOperationException(
                    $"Ключ не сошёлся: occs={occs.Count} rows={lst.Count}");
            var ordered = lst.OrderBy(x => (x.Day, x.Slot)).ToList();
            for (int i = 0; i < occs.Count; i++)
                placements.Add(new PlacedLesson
                {
                    OccurrenceId = occs[i].Id, DayIndex = ordered[i].Day,
                    SlotIndex = ordered[i].Slot, RoomId = ordered[i].Room,
                });
        }
        if (placements.Count != problem.Occurrences.Count)
            throw new InvalidOperationException($"Импорт {placements.Count}/{problem.Occurrences.Count}");
        return placements;
    }
}
