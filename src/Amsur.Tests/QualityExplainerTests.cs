using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// E5 — объяснимость качества. Слоты 1-based, дни 0-based (факт E4).
public sealed class QualityExplainerTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static ProblemInput InputQ()
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5, StudentCount = 25 };
        var teacher = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var subjects = new[] { "Мат", "Рус", "Анг" }.Select(n => new Subject { Name = n, MaxPerDay = 2 }).ToList();
        var curriculum = subjects.Select(s => new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = s.Id, TeacherId = teacher.Id, HoursPerWeek = 1
        }).ToList();
        return new ProblemInput([cls], [teacher], subjects, curriculum,
            [], [], [], DaysCount: 2, SlotsPerDay: 3);
    }

    private static SchedulingProblem Build(ProblemInput input, int seed = 11)
    {
        var (p, e) = ProblemBuilder.Build(input,
            new SolverOptions(MaxTimeSeconds: 5, NumSearchWorkers: 1, RandomSeed: seed));
        Assert.Empty(e);
        return p!;
    }

    private static List<PlacedLesson> Place(SchedulingProblem p, params (int Day, int Slot)[] at) =>
        p.Occurrences.Zip(at).Select(x => new PlacedLesson
        {
            OccurrenceId = x.First.Id, DayIndex = x.Second.Day, SlotIndex = x.Second.Slot
        }).ToList();

    private static ScheduleCandidate MustCreate(SchedulingProblem p, List<PlacedLesson> v, int seed = 11)
    {
        var c = ScheduleCandidate.Create(p, v, seed, 0, "B");
        Assert.NotNull(c);
        return c!;
    }

    // Сырой кандидат БЕЗ validator-gate: объяснитель обязан уметь рассказывать
    // и про грязные снапшоты (диагностика/импорт); продакшн подаёт только clean.
    private static ScheduleCandidate RawCandidate(SchedulingProblem p, List<PlacedLesson> v, int seed = 11)
    {
        var b = SoftEvaluator.Evaluate(p, v);
        var keys = p.Occurrences.ToDictionary(o => o.Id,
            o => string.IsNullOrEmpty(o.StableKey) ? o.Id.ToString("N") : o.StableKey);
        return new ScheduleCandidate(v, b.Total, b,
            ScheduleCandidate.BuildFingerprint(keys, v), seed, 0, "", DateTime.UtcNow, "B", keys);
    }

    // Фикстура чистых кандидатов с ненулевым soft: один предмет MaxPerDay=1 ×3ч,
    // 3 дня — повторы за день дают soft 15 без окон (окна — HARD, D-28).
    private static SchedulingProblem BuildRepeatFixture()
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "5А", Grade = 5, StudentCount = 25 };
        var teacher = new Teacher { Name = "Иванов", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Математика", MaxPerDay = 1 };
        var item = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = math.Id, TeacherId = teacher.Id, HoursPerWeek = 3
        };
        var input = new ProblemInput([cls], [teacher], [math], [item],
            [], [], [], DaysCount: 3, SlotsPerDay: 3);
        return Build(input);
    }

    // --- 1. Ноль: честная строка без сильных утверждений ---
    [Fact]
    public void ZeroCandidate_HonestLines()
    {
        var p = Build(InputQ());
        var c = MustCreate(p, Place(p, (0, 1), (0, 2), (0, 3)));
        Assert.Equal(0, c.SoftTotal);
        var lines = QualityExplainer.Explain(p, c);
        Assert.Single(lines);
        Assert.Contains("Мягких нарушений нет", lines[0]);
        Assert.DoesNotContain("оптимал", lines[0].ToLowerInvariant());
        Assert.Equal("Без мягких нарушений", QualityExplainer.QualitySummary(c));
    }

    // --- 2. Окна учеников: привязка к месту + паритет с breakdown ---
    [Fact]
    public void StudentGap_LinesMatchBreakdown()
    {
        var p = Build(InputQ());
        var c = RawCandidate(p, Place(p, (0, 1), (0, 3), (1, 1)));
        long sg = c.Breakdown.Components.First(x => x.Code == "student-gap").Value;
        Assert.Equal(100, sg); // 1 окно × 100 (веса v2, D-28)
        var lines = QualityExplainer.Explain(p, c);
        var student = Assert.Single(lines, l => l.Contains("Класс"));
        Assert.Contains("5А", student);
        Assert.Contains("день 1", student);
        Assert.Contains("уроки 1–3", student);
    }

    // --- 3. Окна учителей: имя учителя ---
    [Fact]
    public void TeacherGap_LinesNameTeacher()
    {
        var p = Build(InputQ());
        var c = RawCandidate(p, Place(p, (0, 1), (0, 3), (1, 1)));
        long tg = c.Breakdown.Components.First(x => x.Code == "teacher-gap").Value;
        Assert.Equal(10, tg); // тот же разрыв глазами учителя
        var lines = QualityExplainer.Explain(p, c);
        Assert.Contains(lines, l => l.Contains("Учитель Иванов"));
    }

    // --- 4. Повторы предмета: норма в строке ---
    [Fact]
    public void SubjectMaxPerDay_LinesShowNorm()
    {
        var cls = new SchoolClass { AcademicYearId = Guid.NewGuid(), Name = "6Б", Grade = 6, StudentCount = 20 };
        var teacher = new Teacher { Name = "Петрова", MaxLessonsPerDay = 6 };
        var math = new Subject { Name = "Математика", MaxPerDay = 1 };
        var item = new CurriculumItem
        {
            ClassId = cls.Id, SubjectId = math.Id, TeacherId = teacher.Id, HoursPerWeek = 3
        };
        var input = new ProblemInput([cls], [teacher], [math], [item],
            [], [], [], DaysCount: 2, SlotsPerDay: 3);
        var p = Build(input);
        var c = MustCreate(p, Place(p, (0, 1), (0, 2), (0, 3)));
        Assert.Equal(30, c.Breakdown.Components.First(x => x.Code == "subject-maxperday").Value);
        var lines = QualityExplainer.Explain(p, c);
        var subj = Assert.Single(lines, l => l.Contains("норма"));
        Assert.Contains("6Б", subj);
        Assert.Contains("Математика", subj);
        Assert.Contains("норма 1", subj);
    }

    // --- 5. Сравнение: чем хуже, с дельтой ---
    [Fact]
    public void Compare_WorseExplainsDeltas()
    {
        var p = Build(InputQ());
        var best = MustCreate(p, Place(p, (0, 1), (0, 2), (0, 3)));
        var worse = RawCandidate(p, Place(p, (0, 1), (0, 3), (1, 1)));
        var lines = QualityExplainer.Compare(p, best, worse);
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("хуже") && l.Contains("+100"));
        Assert.Contains(lines, l => l.Contains("хуже") && l.Contains("+10"));
        Assert.Contains(lines, l => l.Contains("окон у учеников") && l.Contains("больше"));
        Assert.DoesNotContain(lines, l => l.ToLowerInvariant().Contains("оптимал"));
    }

    // --- 6. Та же оценка, иное распределение: корректная ветка ---
    [Fact]
    public void Compare_EqualTotal_Graceful()
    {
        var p = Build(InputQ());
        var a = RawCandidate(p, Place(p, (0, 1), (0, 3), (1, 1)));
        var b = RawCandidate(p, Place(p, (0, 1), (0, 3), (1, 2)));
        Assert.Equal(a.SoftTotal, b.SoftTotal);
        Assert.NotEqual(a.Fingerprint, b.Fingerprint);
        var lines = QualityExplainer.Compare(p, a, b);
        Assert.Single(lines);
        Assert.Contains("Та же оценка", lines[0]);
    }

    // --- 7. Hard/soft разделены: в строках качества нет жёстких ---
    [Fact]
    public void HardSoft_SeparationOnCard()
    {
        // Чистые кандидаты с ненулевым soft — через повторы предмета (окна — HARD, D-28).
        var p = BuildRepeatFixture();
        var arch = new ScheduleCandidateArchive(5, 100);
        arch.TryAdd(MustCreate(p, Place(p, (0, 1), (1, 1), (2, 1))));
        arch.TryAdd(MustCreate(p, Place(p, (0, 1), (0, 2), (1, 1)), 22));
        var panel = Top5PanelModel.FromArchive(arch, p);
        Assert.Equal(2, panel.Cards.Count);
        foreach (var card in panel.Cards)
        {
            Assert.Equal("0 жёстких нарушений", card.HardText);
            Assert.DoesNotContain(card.QualityLines, l => l.ToLowerInvariant().Contains("жёстк"));
            Assert.DoesNotContain(card.QualitySummary.ToLowerInvariant(), "жёстк");
        }
        Assert.Equal("Без мягких нарушений", panel.Cards[0].QualitySummary);
        Assert.StartsWith("Основное:", panel.Cards[1].QualitySummary);
        Assert.NotEmpty(panel.Cards[0].QualityLines);
        Assert.NotEmpty(panel.Cards[1].QualityLines);
    }

    // --- 8. Словарь: без технических и сильных слов ---
    [Fact]
    public void Vocabulary_NoTechnicalOrStrongClaims()
    {
        var p = BuildRepeatFixture();
        var arch = new ScheduleCandidateArchive(5, 100);
        arch.TryAdd(MustCreate(p, Place(p, (0, 1), (1, 1), (2, 1))));
        arch.TryAdd(MustCreate(p, Place(p, (0, 1), (0, 2), (1, 1)), 22));
        arch.TryAdd(MustCreate(p, Place(p, (0, 1), (1, 1), (1, 2)), 33));
        var panel = Top5PanelModel.FromArchive(arch, p);
        var banned = new[] { "cp-sat", "incumbent", "proxy", "fingerprint", "boolvar",
            "intvar", "solver", "seed", "оптимальн", "идеальн", "невозможно",
            "нормативн", "линеар", "воркер", "hard", "жёстк" };
        var all = panel.Cards.SelectMany(c => c.QualityLines).Concat(
            panel.Cards.Select(c => c.QualitySummary)).ToList();
        Assert.NotEmpty(all);
        foreach (var line in all)
            foreach (var b in banned)
                Assert.DoesNotContain(b, line.ToLowerInvariant());
    }

    // --- 9. SanPin ненулевой: только с пометкой сверки (D-11) ---
    [Fact]
    public void SanpinNonzero_MarkedUnverified()
    {
        var p = Build(InputQ());
        var c = new ScheduleCandidate([], 10,
            new PenaltyBreakdown
            {
                Total = 10,
                Components = [new PenaltyComponent { Code = "sanpin-peak-days", Value = 10 }]
            },
            "fp", 11, 0, "", DateTime.UtcNow, "B",
            new Dictionary<Guid, string>());
        var lines = QualityExplainer.Explain(p, c);
        var sanpin = Assert.Single(lines);
        Assert.Contains("сверки", sanpin);
        Assert.DoesNotContain("нормативн", sanpin.ToLowerInvariant());
    }

    // --- 10. Неизвестный код: без выдуманных причин ---
    [Fact]
    public void UnknownCode_NoFalseCauses()
    {
        var p = Build(InputQ());
        var c = new ScheduleCandidate([], 7,
            new PenaltyBreakdown
            {
                Total = 7,
                Components = [new PenaltyComponent { Code = "future-rule", Value = 7 }]
            },
            "fp", 11, 0, "", DateTime.UtcNow, "B",
            new Dictionary<Guid, string>());
        var lines = QualityExplainer.Explain(p, c);
        var line = Assert.Single(lines);
        Assert.Contains("future-rule", line);
        Assert.DoesNotContain("окн", line.ToLowerInvariant());
    }

    // --- 11. Кросс-запуск: кандидат одного build, имена — из другого (StableKey) ---
    [Fact]
    public void CrossRun_ResolveViaStableKey()
    {
        var p1 = Build(InputQ());
        var p2 = Build(InputQ()); // те же имена, другие Guid
        var c = RawCandidate(p1, Place(p1, (0, 1), (0, 3), (1, 1)));
        var lines = QualityExplainer.Explain(p2, c); // эталон — чужая сборка
        Assert.Contains(lines, l => l.Contains("Класс 5А"));
        Assert.Contains(lines, l => l.Contains("Учитель Иванов"));
    }

    // --- 12. Perf 20 occ + обратная совместимость E4 ---
    [Fact]
    public void Explainer_Perf_And_BackwardCompatible()
    {
        var classes = Enumerable.Range(0, 5).Select(i => new SchoolClass
        {
            AcademicYearId = Guid.NewGuid(), Name = $"К{i}", Grade = 5, StudentCount = 20
        }).ToList();
        var teachers = Enumerable.Range(0, 4).Select(i => new Teacher
        {
            Name = $"У{i}", MaxLessonsPerDay = 6
        }).ToList();
        var subjects = Enumerable.Range(0, 4).Select(i => new Subject
        {
            Name = $"П{i}", MaxPerDay = 2
        }).ToList();
        var curriculum = new List<CurriculumItem>();
        foreach (var c in classes)
            for (int s = 0; s < subjects.Count; s++)
                curriculum.Add(new CurriculumItem
                {
                    ClassId = c.Id, SubjectId = subjects[s].Id,
                    TeacherId = teachers[s].Id, HoursPerWeek = 1
                });
        var input = new ProblemInput(classes, teachers, subjects, curriculum,
            [], [], [], DaysCount: 5, SlotsPerDay: 7);
        var p = Build(input);
        Assert.Equal(20, p.Occurrences.Count);
        // Compact-размещение: уроки класса по разным дням первыми (clean, D-28).
        // День (c+s)%5: внутри класса и учителя все времена различны.
        var placements = p.Occurrences.Select(o =>
        {
            int c = classes.FindIndex(x => x.Id == o.ClassId);
            int s = subjects.FindIndex(x => x.Id == o.SubjectId);
            return new PlacedLesson
            {
                OccurrenceId = o.Id, DayIndex = (c + s) % 5, SlotIndex = 1
            };
        }).ToList();
        var cand = MustCreate(p, placements);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lines = QualityExplainer.Explain(p, cand);
        var summary = QualityExplainer.QualitySummary(cand);
        sw.Stop();
        output.WriteLine($"E5-EXPLAIN 20occ: {sw.ElapsedMilliseconds}мс lines={lines.Count} summary={summary}");
        Assert.True(sw.ElapsedMilliseconds < 1000, $"explain {sw.ElapsedMilliseconds}мс");
        Assert.NotEmpty(summary);

        // E4-путь без problem: строки пустые, итог — Soft total (регрессии нет).
        var arch = new ScheduleCandidateArchive(5, 100);
        arch.TryAdd(cand);
        var panel = Top5PanelModel.FromArchive(arch);
        Assert.Empty(panel.Cards[0].QualityLines);
        Assert.Equal($"Soft {cand.SoftTotal}", panel.Cards[0].QualitySummary);
    }
}
