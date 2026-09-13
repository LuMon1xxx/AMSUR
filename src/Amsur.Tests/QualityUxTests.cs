using Amsur.Application;
using Amsur.Scheduling.Core;

namespace Amsur.Tests;

// Этап C/I: режимы, рейтинг качества, редактор настроек (без STA).
public sealed class QualityUxTests
{
    [Fact]
    public void Modes_Mapping()
    {
        Assert.Equal(4, GenerateModes.All.Count);
        Assert.Equal(3, GenerateModes.ByCode("QUICK").BudgetSeconds);
        Assert.Single(GenerateModes.ByCode("QUICK").Seeds);
        Assert.Equal(12, GenerateModes.ByCode("STANDARD").BudgetSeconds);
        Assert.Equal(3, GenerateModes.ByCode("STANDARD").Seeds.Count);
        Assert.Equal(30, GenerateModes.ByCode("MAXIMUM").BudgetSeconds);
        Assert.True(GenerateModes.ByCode("MAXIMUM").Seeds.Count >= 5);
        Assert.Equal("STANDARD", GenerateModes.ByCode("nope").Code); // дефолт
        Assert.DoesNotContain("seed", GenerateModes.ByCode("QUICK").Description,
            StringComparison.OrdinalIgnoreCase);
    }

    private static PenaltyBreakdown Bd(params (string Code, long Value)[] comps) =>
        new()
        {
            Total = comps.Sum(c => c.Value),
            Components = comps.Select(c => new PenaltyComponent { Code = c.Code, Value = c.Value }).ToList(),
        };

    [Fact]
    public void Rating_Levels()
    {
        var perfect = QualityRating.FromBreakdown(Bd());
        Assert.Equal("Отличное", perfect.Label);
        Assert.Equal(0, perfect.Level);
        Assert.Empty(perfect.Improvements);
        var good = QualityRating.FromBreakdown(Bd(
            ("teacher-gap", 120), ("teacher-cross-shift-gap", 14), ("subject-maxperday", 45)));
        Assert.Equal("Хорошее", good.Label);
        Assert.Equal(3, good.Improvements.Count);
        Assert.Contains(good.Improvements, s => s.Contains("12"));
        var bad = QualityRating.FromBreakdown(Bd(("student-gap", 100)));
        Assert.Equal("Требует внимания", bad.Label);
        Assert.Equal(2, bad.Level);
    }

    [Fact]
    public void Editor_MappingAndValidation()
    {
        var ed = QualitySettingsEditor.FromRules(EffectiveRuleSet.Default);
        Assert.Equal(7, ed.Options.Count); // 2 строгих + 5 пожеланий
        Assert.All(ed.Options.Where(o => o.IsStrict), o => Assert.False(o.Tunable));
        var tg = ed.Options.First(o => o.Code == "teacher-gap");
        Assert.Equal(2, tg.Level); // дефолт = Стандарт
        ed.SetLevel("teacher-gap", 4);
        Assert.Equal(RuleCatalog.WeightRange("teacher-gap").Max, tg.Weight);
        Assert.True(ed.IsModified);
        var ov = ed.GetOverrides();
        Assert.Single(ov);
        var rs = RuleResolver.Resolve("CUSTOM", ov);
        Assert.Equal(tg.Weight, rs.Weight("teacher-gap"));
        Assert.Throws<InvalidOperationException>(() => ed.SetLevel("student-gap", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ed.SetWeight("teacher-gap", 9999));
        // Roundtrip уровень→вес→уровень.
        foreach (int lv in new[] { 0, 1, 2, 3, 4 })
            Assert.Equal(lv, QualitySettingsEditor.WeightToLevel("teacher-gap",
                QualitySettingsEditor.LevelToWeight("teacher-gap", lv)));
    }
}
