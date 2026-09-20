using Amsur.Application;
using Amsur.Domain;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// B5 — fuzzy-матчинг сокращений при импорте: применяется, записывается в Notes,
// неизвестное не трогается, fail-loud пути целы.
public sealed class SubjectAliasTests
{
    private static List<LoadRow> Rows(params LoadRow[] rows) => rows.ToList();

    // --- 1. Сокращения применяются + видны в Notes ---
    [Fact]
    public void Aliases_AppliedAndNoted()
    {
        var rows = Rows(
            new LoadRow("5А", "Матем", 4, "Иванов", false, null, null),
            new LoadRow("5А", "Физра", 2, "Сидоров", false, null, null),
            new LoadRow("5А", "ИЗО", 1, "Петрова", false, null, null));
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows);
        var names = data.Subjects.Select(s => s.Name).OrderBy(n => n).ToList();
        Assert.Equal(
            ["Изобразительное искусство", "Математика", "Физическая культура и здоровье"],
            names);
        Assert.Contains(data.Notes, n => n.Contains("Матем") && n.Contains("Математика"));
        Assert.Contains(data.Notes, n => n.Contains("Физра") && n.Contains("Физическая культура и здоровье"));
        var (problem, errors) = ProblemBuilder.Build(data.ToProblemInput());
        Assert.Empty(errors);
    }

    // --- 2. Регистр не важен ---
    [Fact]
    public void Alias_CaseInsensitive()
    {
        var rows = Rows(new LoadRow("5А", "матем", 1, "Иванов", false, null, null));
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows);
        Assert.Equal("Математика", Assert.Single(data.Subjects).Name);
    }

    // --- 3. Неизвестное (в т.ч. короткие «Мат») — как есть, без заметок ---
    [Fact]
    public void Unknown_KeptAsIs()
    {
        var rows = Rows(
            new LoadRow("5А", "Мат", 3, "Иванов", false, null, null),
            new LoadRow("5А", "Рус", 2, "Иванов", false, null, null));
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows);
        Assert.Equal(2, data.Subjects.Count);
        Assert.DoesNotContain(data.Notes, n => n.Contains("распознано"));
    }

    // --- 4. Официальное название — без заметок ---
    [Fact]
    public void Official_NoNote()
    {
        var rows = Rows(new LoadRow("5А", "Математика", 1, "Иванов", false, null, null));
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows);
        Assert.Equal("Математика", Assert.Single(data.Subjects).Name);
        Assert.DoesNotContain(data.Notes, n => n.Contains("распознано"));
    }

    // --- 5. Fail-loud цел: пусто — честная ошибка ---
    [Fact]
    public void Empty_StillThrows()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SchoolDataImporter.Import(Guid.NewGuid(), []));
        Assert.Contains("не содержит строк", ex.Message);
    }

    // --- 6. P-PE-FLAG: физкультура помечается явным флагом ---
    [Fact]
    public void PhysicalEducation_FlaggedFromOfficialName()
    {
        var rows = Rows(
            new LoadRow("5А", "Физра", 2, "Сидоров", false, null, null),
            new LoadRow("5А", "Мат", 2, "Иванов", false, null, null));
        var data = SchoolDataImporter.Import(Guid.NewGuid(), rows);
        var pe = data.Subjects.Single(s => s.Name == "Физическая культура и здоровье");
        Assert.True(pe.IsPhysicalEducation);
        Assert.All(data.Subjects.Where(s => s.Name != "Физическая культура и здоровье"),
            s => Assert.False(s.IsPhysicalEducation));
    }
}
