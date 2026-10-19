using StarTrek.Mcp.Rag;

namespace StarTrek.Mcp.Tests;

public sealed class GradeVerdictTests
{
    [Theory]
    [InlineData("да", true)]
    [InlineData("Да.", true)]
    [InlineData("  ДА  ", true)]
    [InlineData("да\nпотому что во фрагменте есть имя", true)]
    [InlineData("Yes", true)]
    [InlineData("Yes, the chunk is relevant", true)]
    [InlineData("relevant", true)]
    [InlineData("релевантен", true)]
    [InlineData("«да»", true)]
    [InlineData("Ответ: да", true)]
    [InlineData("нет", false)]
    [InlineData("Нет, фрагмент не относится к вопросу", false)]
    [InlineData("No", false)]
    [InlineData("No.", false)]
    [InlineData("не уверен", false)]
    [InlineData("возможно", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void ParsesYesAndNo(string? response, bool relevant)
    {
        Assert.Equal(relevant, GradeVerdict.IsRelevant(response));
    }
}
