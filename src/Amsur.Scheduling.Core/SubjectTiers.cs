namespace Amsur.Scheduling.Core;

using Amsur.Domain;

// P-PE-FLAG (D-52): порядок «сначала физра, потом ин.яз» — как ВЫБОР лучших клеток,
// а не перестановка очереди юнитов (очередь bit-v-bit, покрытие свято).
// Этот класс — только public-хелперы (тиры + распознавание имён) для тестов
// и будущих режимов; сам выбор клеток живёт в GreedyPlacer (пакет P2).
public static class SubjectTiers
{
    public const int PhysicalEducationTier = 0;
    public const int ForeignLanguageTier = 1;
    public const int OrdinaryTier = 2;

    /// <summary>Тир предмета: физра → ин.яз → остальные.</summary>
    public static int TierOf(Subject subject) =>
        subject.IsPhysicalEducation ? PhysicalEducationTier
        : subject.IsForeignLanguage ? ForeignLanguageTier
        : OrdinaryTier;

    /// <summary>
    /// Официальное имя иностранного языка — факт, не эвристика (ср. P-PE-FLAG):
    /// «Иностранный язык» (SUBJECTS_RB.md §1) или «Английский язык» с необязательным
    /// уточнением профиля « (проф)»/« (база)». Произвольные строки не трогаем.
    /// </summary>
    public static bool IsForeignLanguageName(string name)
    {
        var n = (name ?? "").Trim();
        if (string.Equals(n, "Иностранный язык", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(n, "Английский язык", StringComparison.OrdinalIgnoreCase))
            return true;
        return n.StartsWith("Английский язык ", StringComparison.OrdinalIgnoreCase);
    }
}
