using StarTrek.Indexer.Parsing;
using StarTrek.Indexer.Tokenization;

namespace StarTrek.Indexer.Tests;

public sealed class EmbeddingTextBuilderTests
{
    private readonly DocumentParser parser = new();

    [Fact]
    public void BuildsMetadataHeaderWithSectionPathInWords()
    {
        var document = parser.Parse(
            "# Талосианцы\nОригинальное название: Talosian\n\n####\nИстория:\nТекст.",
            "SourceFiles/Races/Талосианцы.txt");

        var text = EmbeddingTextBuilder.ForSection(document, document.Sections[0]);

        Assert.Equal("Документ: Талосианцы\nТип сущности: раса\nРаздел: История\n\nТекст.", text);
    }

    [Fact]
    public void JoinsNestedPathAndRemovesHashMarks()
    {
        var document = parser.Parse(
            "## Теллар\n### Приложения\n#### Справочная информация\nсм. «Звёздные карты # Карта Земли».",
            "SourceFiles/Planets/теллар.txt");

        var text = EmbeddingTextBuilder.ForSection(document, document.Sections[1]);

        Assert.DoesNotContain('#', text);
        Assert.Contains("Тип сущности: планета\n", text);
        Assert.Contains("Раздел: Приложения > Справочная информация\n", text);
        Assert.EndsWith("см. «Звёздные карты Карта Земли».", text);
    }
}
