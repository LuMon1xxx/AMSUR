using Amsur.Wpf;

namespace Amsur.Tests;

public sealed class UiFormatTests
{
    [Theory]
    [InlineData("Каб 7А", "Каб 7А")]
    [InlineData("каб. 12", "каб. 12")]
    [InlineData("204", "каб. 204")]
    [InlineData("12А", "каб. 12А")]
    [InlineData("Спортзал", "Спортзал")]
    [InlineData("ИТ", "ИТ")]
    [InlineData("—", "—")]
    [InlineData("", "—")]
    [InlineData("   ", "—")]
    [InlineData(null, "—")]
    public void Room_FormatsWithoutDuplication(string? input, string expected)
    {
        Assert.Equal(expected, UiFormat.Room(input));
    }
}
