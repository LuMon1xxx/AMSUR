using Amsur.Application;

namespace Amsur.Tests;

// D-54 (продуктовая поверхность): канон из 23 официальных РБ-названий
// (SUBJECTS_RB.md §1). Списки диалога — union(строки, сущности, канон),
// свободный ввод сохранён (D-34). Полные карточки — backlog P3.
public sealed class CanonicalSubjectsTests
{
    // --- 1. Ровно 23, без дублей и пустых ---
    [Fact]
    public void Count_Is23_NoDupes()
    {
        Assert.Equal(23, CanonicalSubjects.All.Count);
        Assert.Equal(23, CanonicalSubjects.All
            .Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(CanonicalSubjects.All, s => Assert.False(string.IsNullOrWhiteSpace(s)));
    }

    // --- 2. Ключевые предметы есть, алиасов нет ---
    [Fact]
    public void Contains_KeySubjects_NoAliases()
    {
        Assert.Contains("Физическая культура и здоровье", CanonicalSubjects.All);
        Assert.Contains("Иностранный язык", CanonicalSubjects.All);
        Assert.Contains("Допризывная и медицинская подготовка", CanonicalSubjects.All);
        Assert.Contains("Трудовое обучение", CanonicalSubjects.All);
        // РФ-сокращения — НЕ канон (см. SUBJECTS_RB.md §1).
        Assert.DoesNotContain("Физра", CanonicalSubjects.All);
        Assert.DoesNotContain("ОБЖ", CanonicalSubjects.All);
        Assert.DoesNotContain("ИЗО", CanonicalSubjects.All);
        Assert.DoesNotContain("Труд", CanonicalSubjects.All);
    }

    // --- 3. Проверка без учёта регистра ---
    [Fact]
    public void IsCanonical_CaseInsensitive()
    {
        Assert.True(CanonicalSubjects.IsCanonical("математика"));
        Assert.False(CanonicalSubjects.IsCanonical("Физра"));
        Assert.False(CanonicalSubjects.IsCanonical(""));
    }
}
