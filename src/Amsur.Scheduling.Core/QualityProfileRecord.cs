namespace Amsur.Scheduling.Core;

// Пользовательский профиль качества школы (S5 P4): снимок ручных правок поверх
// базового профиля. Хранится в SQLite (QualityProfiles), single-active на школу.
public sealed record QualityProfileRecord(
    Guid Id,
    string Name,
    string BaseProfile,
    Dictionary<string, long> Weights,
    int CatalogVersion,
    DateTime UpdatedAt,
    bool IsActive)
{
    /// <summary>
    /// B2: восстановление с загруженным согласием. v5+ (разреженные снимки):
    /// present dangerous = явное намерение. ≤v4 (полные словари): намерение =
    /// только веса, отличные от дефолта (дефолтные строгие остаются строгими).
    /// </summary>
    public EffectiveRuleSet ToRuleSet()
    {
        var confirmed = Weights
            .Where(kv => RuleCatalog.IsDangerous(kv.Key) &&
                (CatalogVersion >= 5 || kv.Value != RuleCatalog.DefaultWeight(kv.Key)))
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.Ordinal);
        return RuleResolver.Resolve("CUSTOM", Weights, confirmed);
    }
}
