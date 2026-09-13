namespace Amsur.Scheduling.Core;

// Настраиваемый набор весов качества (S5 decision): профили — данные, не ветки кода.
// Student-компактность (gap/late-start) — HARD-gate валидатора и профилями НЕ ослабляется:
// здесь только soft-градиент для поиска. FullValidator остаётся авторитетом.
public sealed record EffectiveRuleSet(
    IReadOnlyDictionary<string, long> Weights,
    string ProfileName,
    int CatalogVersion)
{
    public long Weight(string code) =>
        Weights.TryGetValue(code, out long w) ? w : 0;

    public static EffectiveRuleSet Default => RuleResolver.Resolve("STANDARD");
}

public static class RuleResolver
{
    public static readonly IReadOnlyList<string> Profiles =
        ["STANDARD", "STUDENT_FRIENDLY", "TEACHER_FRIENDLY", "CUSTOM"];

    /// <summary>
    /// Профиль + ручные переопределения школы → итоговый набор.
    /// overrides: code → вес (валидируется по WeightRange; вне диапазона — ArgumentException).
    /// </summary>
    public static EffectiveRuleSet Resolve(
        string profileName,
        IReadOnlyDictionary<string, long>? overrides = null)
    {
        string profile = (profileName ?? "STANDARD").ToUpperInvariant();
        var weights = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (string code in RuleCatalog.AllCodes)
            weights[code] = RuleCatalog.DefaultWeight(code);
        // strict-* нет в AllCodes — тоже несём (для Strict-режима UI).
        weights["strict-student-gap"] = RuleCatalog.StrictStudentGap;
        weights["strict-teacher-gap"] = RuleCatalog.StrictTeacherGap;

        switch (profile)
        {
            case "STUDENT_FRIENDLY":
                // Ученик-приоритет в рамках HARD: окна учителей дешевле обычного.
                weights["teacher-gap"] = 8;
                weights["teacher-cross-shift-gap"] = 1;
                break;
            case "TEACHER_FRIENDLY":
                // Учителям плотнее день; student-HARD не трогаем (gate един для всех).
                weights["teacher-gap"] = 20;
                weights["teacher-cross-shift-gap"] = 5;
                break;
            case "CUSTOM":
            case "STANDARD":
                break;
            default:
                throw new ArgumentException($"Неизвестный профиль '{profileName}'.", nameof(profileName));
        }

        if (overrides is not null)
            foreach (var (code, w) in overrides)
            {
                if (!weights.ContainsKey(code))
                    throw new ArgumentException($"Код '{code}' неизвестен.", nameof(overrides));
                var (min, max) = RuleCatalog.WeightRange(code);
                if (max == 0 && min == 0)
                {
                    // Ненастраиваемый код (HARD-gate или заглушка): принимаем только дефолт
                    // (нужно для roundtrip полных снимков весов через QualityProfiles).
                    if (w != RuleCatalog.DefaultWeight(code))
                        throw new ArgumentException(
                            $"Код '{code}' не настраивается (допустим только дефолт).",
                            nameof(overrides));
                    weights[code] = w;
                    continue;
                }
                if (w < min || w > max)
                    throw new ArgumentOutOfRangeException(nameof(overrides),
                        $"Вес '{code}' должен быть {min}..{max}.");
                weights[code] = w;
            }

        string name = profile == "CUSTOM" || overrides is not null ? "CUSTOM" : profile;
        return new EffectiveRuleSet(weights, name, RuleCatalog.Version);
    }

    /// <summary>Короткий детерминированный отпечаток весов (для Candidate snapshot).</summary>
    public static string WeightsFingerprint(EffectiveRuleSet rs)
    {
        string raw = string.Join("|", rs.Weights.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}"));
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..12];
    }
}

/// <summary>
/// Человеческая шкала ↔ число (UI показывает шкалу + подпись числом мелко; движок — числа).
/// Пороги: <=25% диапазона «Не важно», <=75% «Стандарт», <=125% «Важно», иначе «Очень важно».
/// </summary>
public static class HumanScale
{
    public static string Label(string code, long value)
    {
        long def = RuleCatalog.DefaultWeight(code);
        if (def <= 0) return value <= 0 ? "Не учитывать" : "Учитывать";
        double r = (double)value / def;
        if (value <= 0) return "Не важно";
        if (r <= 0.25) return "Не важно";
        if (r <= 0.75) return "Ниже стандарта";
        if (r <= 1.25) return "Стандарт";
        if (r <= 2.0) return "Важно";
        return "Очень важно";
    }
}
