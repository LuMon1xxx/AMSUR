using Amsur.Scheduling.Core;

namespace Amsur.Application;

// Режимы генерации человеческим языком (промт §6). Числа — только здесь,
// UI показывает название + описание. Экспертная отличается не бюджетом,
// а открытием полных настроек (правила — из Session.QualityRules).
public sealed record GenerateModeSpec(
    string Code, string Name, string Description,
    double BudgetSeconds, IReadOnlyList<int> Seeds);

public static class GenerateModes
{
    public static IReadOnlyList<GenerateModeSpec> All { get; } =
    [
        new("QUICK", "Быстро",
            "Хорошее расписание за минимальное время.",
            3, [11]),
        new("STANDARD", "Стандарт",
            "Рекомендуется: баланс качества и времени.",
            12, [11, 22, 33]),
        new("MAXIMUM", "Максимальное качество",
            "Больше времени и вариантов поиска.",
            30, [11, 22, 33, 42, 7]),
        new("EXPERT", "Экспертная настройка",
            "Сначала полные настройки, затем генерация.",
            12, [11, 22, 33]),
    ];

    public static GenerateModeSpec ByCode(string code) =>
        All.FirstOrDefault(m => m.Code == (code ?? "").ToUpperInvariant())
        ?? All[1];
}
