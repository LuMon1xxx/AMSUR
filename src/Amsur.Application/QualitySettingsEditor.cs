using Amsur.Scheduling.Core;

namespace Amsur.Application;

// Логика экрана настроек качества (промт §§7–9): чистый класс без WPF,
// XAML только биндится/рисует. Тестируется обычным xUnit.
// Человеческие уровни 0..4 ↔ числовые веса; строгие правила — только чтение.
public sealed class QualityOption
{
    public required string Code { get; init; }
    public required string Title { get; init; }
    public required string Kind { get; init; } // «Строгое» | «Пожелание»
    public required string Hint { get; init; }
    public bool IsStrict { get; init; }
    public bool Tunable { get; init; }
    public int Level { get; set; } // 0..4
    public long Weight { get; set; }
    public long DefaultWeight { get; init; }
    public long Min { get; init; }
    public long Max { get; init; }

    public static readonly string[] LevelNames =
        ["Не важно", "Ниже стандарта", "Стандарт", "Важно", "Очень важно"];

    public string LevelName => LevelNames[Math.Clamp(Level, 0, 4)];
}

public sealed class QualitySettingsEditor
{
    public List<QualityOption> Options { get; } = [];

    public static QualitySettingsEditor FromRules(EffectiveRuleSet rules)
    {
        var ed = new QualitySettingsEditor();
        // Строгие (чтение): влияют на выбор вариантов, торговаться нельзя.
        foreach (string code in new[] { "student-gap", "student-late-start" })
        {
            var h = QualityHints.For(code);
            ed.Options.Add(new QualityOption
            {
                Code = code, Title = h.Title, Kind = "Строгое", Hint = h.What + " " + h.UpDown,
                IsStrict = true, Tunable = false, Level = 2,
                Weight = rules.Weight(code), DefaultWeight = RuleCatalog.DefaultWeight(code),
                Min = 0, Max = 0,
            });
        }
        // Пожелания (слайдеры).
        foreach (string code in new[] { "teacher-gap", "teacher-cross-shift-gap",
                     "subject-maxperday", "heavy-edge", "room-preference" })
        {
            var h = QualityHints.For(code);
            var (min, max) = RuleCatalog.WeightRange(code);
            long w = rules.Weight(code);
            ed.Options.Add(new QualityOption
            {
                Code = code, Title = h.Title, Kind = "Пожелание",
                Hint = $"{h.What} {h.UpDown} Дефолт: {h.Default} {h.When}",
                IsStrict = false, Tunable = true, Level = WeightToLevel(code, w),
                Weight = w, DefaultWeight = RuleCatalog.DefaultWeight(code),
                Min = min, Max = max,
            });
        }
        return ed;
    }

    public void SetLevel(string code, int level)
    {
        var o = Options.First(x => x.Code == code);
        if (!o.Tunable) throw new InvalidOperationException($"«{o.Title}» — строгое правило, уровень не меняется.");
        o.Level = Math.Clamp(level, 0, 4);
        o.Weight = LevelToWeight(code, o.Level);
    }

    public void SetWeight(string code, long weight)
    {
        var o = Options.First(x => x.Code == code);
        if (!o.Tunable) throw new InvalidOperationException($"«{o.Title}» — строгое правило, вес не меняется.");
        var (min, max) = (o.Min, o.Max);
        if (weight < min || weight > max)
            throw new ArgumentOutOfRangeException(nameof(weight), $"Допустимо {min}..{max}.");
        o.Weight = weight;
        o.Level = WeightToLevel(code, weight);
    }

    /// <summary>Изменения против STANDARD (для CUSTOM-снимка и резолвера).</summary>
    public Dictionary<string, long> GetOverrides()
    {
        var d = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var o in Options.Where(x => x.Tunable && x.Weight != x.DefaultWeight))
            d[o.Code] = o.Weight;
        return d;
    }

    public bool IsModified => GetOverrides().Count > 0;

    public static long LevelToWeight(string code, int level)
    {
        long def = RuleCatalog.DefaultWeight(code);
        var (_, max) = RuleCatalog.WeightRange(code);
        return Math.Clamp(level, 0, 4) switch
        {
            0 => 0,
            1 => Math.Max(1, def / 4),
            2 => def,
            3 => Math.Min(max, def * 2),
            _ => max,
        };
    }

    public static int WeightToLevel(string code, long weight)
    {
        long def = RuleCatalog.DefaultWeight(code);
        var (_, max) = RuleCatalog.WeightRange(code);
        if (weight <= 0) return 0;
        if (def <= 0) return weight > 0 ? 2 : 0;
        double r = (double)weight / def;
        if (r < 0.5) return 1;
        if (r <= 1.5) return 2;
        if (weight >= max) return 4;
        return 3;
    }
}
