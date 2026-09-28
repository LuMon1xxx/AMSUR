using Amsur.Application;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// D-52 (продуктовая поверхность пакета P1): флаги предметов + тиры.
// Сам разнос физры по клеткам — выбор клеток в GreedyPlacer (пакет P2),
// здесь — только вход: флаги из официальных имён, MaxPerDay=1 у физры, тиры.
// Каталог весов не тронут (новых кодов нет).
public sealed class PeSpreadTests
{
    private static List<LoadRow> Rows(params LoadRow[] rows) => rows.ToList();

    // --- 1. Флаги из официальных имён при импорте ---
    [Fact]
    public void Flags_FromOfficialNames()
    {
        var rows = Rows(
            new LoadRow("5А", "Физическая культура и здоровье", 3, "Иванов", false, null, null),
            new LoadRow("5А", "Английский язык", 3, "Петрова", true, "Сидорова", null),
            new LoadRow("5А", "Математика", 5, "Козлова", false, null, null));
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows);
        var byName = data.Subjects.ToDictionary(s => s.Name);
        Assert.True(byName["Физическая культура и здоровье"].IsPhysicalEducation);
        Assert.True(byName["Английский язык"].IsForeignLanguage);
        Assert.False(byName["Математика"].IsPhysicalEducation);
        Assert.False(byName["Математика"].IsForeignLanguage);
    }

    // --- 2. Алиас физры тоже флагуется (после нормализации имени) ---
    [Fact]
    public void Alias_Pe_Flagged()
    {
        var rows = Rows(new LoadRow("5А", "Физра", 2, "Иванов", false, null, null));
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows);
        var pe = Assert.Single(data.Subjects);
        Assert.Equal("Физическая культура и здоровье", pe.Name);
        Assert.True(pe.IsPhysicalEducation);
    }

    // --- 3. У физры повтор за день — soft: дефолт MaxPerDay=1 ---
    [Fact]
    public void Pe_MaxPerDay_IsOne()
    {
        var rows = Rows(
            new LoadRow("5А", "Физическая культура и здоровье", 3, "Иванов", false, null, null),
            new LoadRow("5А", "Математика", 5, "Козлова", false, null, null));
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows);
        var byName = data.Subjects.ToDictionary(s => s.Name);
        Assert.Equal(1, byName["Физическая культура и здоровье"].MaxPerDay);
        Assert.Equal(2, byName["Математика"].MaxPerDay); // остальные — как раньше
    }

    // --- 4. Тиры: физра → ин.яз → остальные ---
    [Fact]
    public void Tiers_Order()
    {
        var pe = new Amsur.Domain.Subject { Name = "Физическая культура и здоровье", IsPhysicalEducation = true };
        var fl = new Amsur.Domain.Subject { Name = "Английский язык", IsForeignLanguage = true };
        var other = new Amsur.Domain.Subject { Name = "Математика" };
        Assert.True(SubjectTiers.TierOf(pe) < SubjectTiers.TierOf(fl));
        Assert.True(SubjectTiers.TierOf(fl) < SubjectTiers.TierOf(other));
    }

    // --- 5. Распознавание иностранного: официальные имена, не префиксы ---
    [Fact]
    public void IsForeignLanguageName_Cases()
    {
        Assert.True(SubjectTiers.IsForeignLanguageName("Иностранный язык"));
        Assert.True(SubjectTiers.IsForeignLanguageName("Английский язык"));
        Assert.True(SubjectTiers.IsForeignLanguageName("Английский язык (проф)"));
        Assert.True(SubjectTiers.IsForeignLanguageName("Английский язык (база)"));
        Assert.False(SubjectTiers.IsForeignLanguageName("Математика"));
        Assert.False(SubjectTiers.IsForeignLanguageName("Английский")); // не официальное
        Assert.False(SubjectTiers.IsForeignLanguageName(""));
    }
}
