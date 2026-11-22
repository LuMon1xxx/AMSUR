namespace Amsur.Wpf;

public sealed record ModeUiInfo(
    string Title, string TimeLabel, string Hint, string GenerateTimeLabel, bool IsRecommended);

public static class UiFormat
{
    /// <summary>
    /// Единая таблица метаданных режимов генерации для Dashboard и экрана Генерации.
    /// </summary>
    public static ModeUiInfo ModeDisplay(string code) => (code ?? "").ToUpperInvariant() switch
    {
        "QUICK" => new("Быстро", "~3 сек", "Проверка / быстрый результат", "лимит ~3 сек", false),
        "STANDARD" => new("Стандарт", "12–30 сек", "Баланс качества и скорости", "12 с · большая школа до 30 с", true),
        "MAXIMUM" => new("Максимум", "~30 сек", "Больше времени и вариантов", "лимит ~30 сек", false),
        "TOP" => new("Топ-качество", "~5–10 мин", "5 запусков + доводка grind-lite", "5 запусков + доводка ~5–10 мин", false),
        "EXPERT" => new("Эксперт", "Свои правила", "Полные настройки перед запуском", "свои правила · ~12 с", false),
        _ => new(code ?? "", "", "", "", false),
    };

    /// <summary>
    /// Форматирует название кабинета без дублей («каб. Каб 7А» → «Каб 7А»,
    /// «Спортзал» → «Спортзал», «204» → «каб. 204», пусто/«—» → «—»).
    /// </summary>
    public static string Room(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "—";
        string trimmed = name.Trim();
        if (trimmed == "—") return "—";
        if (trimmed.StartsWith("каб", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        if (char.IsDigit(trimmed[0]))
            return "каб. " + trimmed;
        return trimmed;
    }
}
