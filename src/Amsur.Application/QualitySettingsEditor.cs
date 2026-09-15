using Amsur.Scheduling.Core;

namespace Amsur.Application;

// Логика экрана настроек качества (промт §§7–9 + B2): чистый класс без WPF,
// XAML только биндится/рисует. Тестируется обычным xUnit.
// Человеческие уровни 0..4 ↔ числовые веса; опасные (строгие по шаблону) —
// Tunable=true, но SetLevel/SetWeight требуют confirmed:true (попап B1 в UI).
// Без подтверждения — InvalidOperationException («требуется подтверждение»).
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
        // B2: опасные (строгие по шаблону) — бейдж остаётся, но настраиваются
        // ТОЛЬКО через подтверждение (DangerConfirm в UI). Дефолт = сегодняшнее
        // строгое (0 окон из коробки); ослабление уходит в Warnings, не в Hard.
        foreach (string code in RuleCatalog.DangerousCodes)
        {
            var h = QualityHints.For(code);
            var (min, max) = RuleCatalog.OverrideRange(code);
            long w = rules.Weight(code);
            // Ослаблено ли уже (вес отличается от дефолта или код в RelaxedStrict)?
            bool relaxed = rules.RelaxedStrict.Contains(code);
            ed.Options.Add(new QualityOption
            {
                Code = code, Title = h.Title, Kind = "Строгое",
                Hint = $"{h.What} {h.UpDown} Дефолт: строгое (шаблон так делает). " +
                       $"Ослабление — только через подтверждение.",
                IsStrict = true, Tunable = true,
                Level = DangerousWeightToLevel(w),
                Weight = w, DefaultWeight = RuleCatalog.DefaultWeight(code),
                Min = min, Max = max,
            });
            _ = relaxed; // факт ослабления читается через GetOverrides/RelaxedStrict
        }
        // Пожелания (слайдеры).
        foreach (string code in new[] { "teacher-gap", "teacher-cross-shift-gap",
                     "subject-maxperday", "heavy-edge", "room-preference",
                     "room-crowding", "teacher-split" })
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

    /// <summary>
    /// B2: для опасных кодов требуется confirmed:true (UI ставит после DangerConfirm).
    /// Без него — InvalidOperationException («требуется подтверждение»).
    /// </summary>
    public void SetLevel(string code, int level, bool confirmed = false)
    {
        var o = Options.First(x => x.Code == code);
        if (!o.Tunable) throw new InvalidOperationException($"«{o.Title}» — строгое правило, уровень не меняется.");
        if (o.IsStrict && !confirmed)
            throw new InvalidOperationException(
                $"«{o.Title}» — строгое правило: требуется подтверждение.");
        o.Level = Math.Clamp(level, 0, 4);
        o.Weight = o.IsStrict ? DangerousLevelToWeight(o.Level) : LevelToWeight(code, o.Level);
    }

    public void SetWeight(string code, long weight, bool confirmed = false)
    {
        var o = Options.First(x => x.Code == code);
        if (!o.Tunable) throw new InvalidOperationException($"«{o.Title}» — строгое правило, вес не меняется.");
        if (o.IsStrict && !confirmed)
            throw new InvalidOperationException(
                $"«{o.Title}» — строгое правило: требуется подтверждение.");
        var (min, max) = (o.Min, o.Max);
        if (weight < min || weight > max)
            throw new ArgumentOutOfRangeException(nameof(weight), $"Допустимо {min}..{max}.");
        o.Weight = weight;
        o.Level = o.IsStrict ? DangerousWeightToLevel(weight) : WeightToLevel(code, weight);
    }

    /// <summary>B2: линейная шкала опасного 0..4 ↔ 0..100 (дефолт строгих 100 = уровень 4).</summary>
    public static long DangerousLevelToWeight(int level) => Math.Clamp(level, 0, 4) * 25;

    public static int DangerousWeightToLevel(long weight)
    {
        if (weight <= 12) return 0;
        if (weight <= 37) return 1;
        if (weight <= 62) return 2;
        if (weight <= 87) return 3;
        return 4;
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
