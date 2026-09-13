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
    public EffectiveRuleSet ToRuleSet() =>
        RuleResolver.Resolve("CUSTOM", Weights);
}
