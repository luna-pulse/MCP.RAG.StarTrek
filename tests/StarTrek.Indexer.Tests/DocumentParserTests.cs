using StarTrek.Indexer.Configuration;
using StarTrek.Indexer.Parsing;

namespace StarTrek.Indexer.Tests;

public sealed class DocumentParserTests
{
    private readonly DocumentParser parser = new();

    [Fact]
    public void ParsesRaceMarkerOnlyDialect()
    {
        const string text =
            "# Талосианцы\r\n" +
            "Оригинальное название: Talosian\r\n\r\n" +
            "####\r\n" +
            "Общие сведения:\r\n" +
            "Описание расы.\r\n";

        var document = parser.Parse(text, "SourceFiles/Races/Талосианцы.txt");

        Assert.Equal("Талосианцы", document.Title);
        Assert.Equal("Talosian", document.OriginalTitle);
        Assert.Equal("race", document.EntityType);
        Assert.Null(document.Introduction);
        var section = Assert.Single(document.Sections);
        Assert.Equal(4, section.Level);
        Assert.Equal("Общие сведения", section.Title);
        Assert.Equal(["Общие сведения"], section.Path);
        Assert.Equal(4, section.StartLine);
        Assert.Equal(6, section.EndLine);
        Assert.Equal("Описание расы.", section.Body);
    }

    [Fact]
    public void ParsesPlanetAndSeriesMarkdownDialect()
    {
        const string text =
            "# Баджор\n\n" +
            "Вводный текст.\n\n" +
            "## История\n" +
            "Обзор.\n\n" +
            "### Ранняя история\n" +
            "Древние события.\n";

        var document = parser.Parse(text, "SourceFiles/Planets/баджор.txt");

        Assert.Equal("planet", document.EntityType);
        Assert.Equal("Вводный текст.", document.Introduction?.Body);
        Assert.Collection(
            document.Sections,
            section =>
            {
                Assert.Equal(["История"], section.Path);
                Assert.Equal("Обзор.", section.Body);
            },
            section =>
            {
                Assert.Equal(["История", "Ранняя история"], section.Path);
                Assert.Equal("Древние события.", section.Body);
            });
    }

    [Fact]
    public void HeadingOfSameOrHigherLevelClosesPreviousPath()
    {
        const string text =
            "# Документ\n" +
            "## A\nа\n" +
            "### B\nб\n" +
            "#### C\nв\n" +
            "### D\nг\n" +
            "## E\nд\n";

        var document = parser.Parse(text, "SourceFiles/Planets/doc.txt");

        Assert.Equal(
            [["A"], ["A", "B"], ["A", "B", "C"], ["A", "D"], ["E"]],
            document.Sections.Select(section => section.Path.ToArray()).ToArray());
    }

    [Theory]
    [InlineData("##")]
    [InlineData("###")]
    [InlineData("####")]
    public void TreatsFirstHeadingAsDocumentTitleDespiteCorpusLevelVariation(string marker)
    {
        var document = parser.Parse(
            $"{marker} Планета\n\n### Раздел\nТекст.",
            "SourceFiles/Planets/planet.txt");

        Assert.Equal("Планета", document.Title);
        Assert.Single(document.Sections);
        Assert.DoesNotContain(document.Sections, section => section.Title == "Планета");
    }

    [Fact]
    public void UsesCapitalizedFileNameWhenDocumentHasNoLeadingHeading()
    {
        var document = parser.Parse(
            "**Тип:** планета.\n\n#### История\nТекст.",
            "SourceFiles/Planets/теллар.txt");

        Assert.Equal("Теллар", document.Title);
        Assert.Equal("**Тип:** планета.", document.Introduction?.Body);
        Assert.Equal(["История"], Assert.Single(document.Sections).Path);
    }

    [Fact]
    public void ContentBlocksStartWithIntroductionSection()
    {
        var document = parser.Parse(
            "## Марс\n\n**Также известен как:** Сол IV.\n\n### Класс:\nM",
            "SourceFiles/Planets/марс.txt");

        var blocks = document.GetContentBlocks();

        Assert.Collection(
            blocks,
            introduction =>
            {
                Assert.Equal(0, introduction.Level);
                Assert.Equal(ParsedDocument.IntroductionTitle, introduction.Title);
                Assert.Equal([ParsedDocument.IntroductionTitle], introduction.Path);
                Assert.Equal(3, introduction.StartLine);
                Assert.Equal(3, introduction.EndLine);
                Assert.Equal("**Также известен как:** Сол IV.", introduction.Body);
            },
            section => Assert.Equal(["Класс"], section.Path));
    }

    [Fact]
    public void ParsesStarshipRegistryWithoutMergingShips()
    {
        const string text =
            "# Реестр\n\n" +
            "## Класс Constitution\n\n" +
            "### USS Enterprise\n" +
            "NCC-1701\n\n" +
            "### USS Defiant\n" +
            "NX-74205\n";

        var document = parser.Parse(text, "SourceFiles/StarShips/registry.md");

        Assert.Equal("starship", document.EntityType);
        Assert.Collection(
            document.Sections,
            section => Assert.Equal(["Класс Constitution"], section.Path),
            section =>
            {
                Assert.Equal(["Класс Constitution", "USS Enterprise"], section.Path);
                Assert.Equal("NCC-1701", section.Body);
            },
            section =>
            {
                Assert.Equal(["Класс Constitution", "USS Defiant"], section.Path);
                Assert.Equal("NX-74205", section.Body);
            });
    }

    [Fact]
    public void ExtractsMarkdownOriginalTitleFromSeries()
    {
        const string text =
            "# Сериал\n\n" +
            "## Краткие сведения\n" +
            "- **Оригинальное название:** *Star Trek*\n";

        var document = parser.Parse(text, "SourceFiles/Series/series.md");

        Assert.Equal("series", document.EntityType);
        Assert.Equal("Star Trek", document.OriginalTitle);
    }

    [Theory]
    [InlineData("SourceFiles/Races/x.txt", "race")]
    [InlineData("SourceFiles/Planets/x.txt", "planet")]
    [InlineData("SourceFiles/StarShips/x.md", "starship")]
    [InlineData("SourceFiles/Series/x.md", "series")]
    [InlineData("SourceFiles/Other/x.md", "other")]
    public void ResolvesEntityTypeByFolder(string sourceFile, string expected)
    {
        Assert.Equal(expected, DocumentParser.ResolveEntityType(sourceFile));
    }

    [Fact]
    public void ParsesRealRaceFileWithMarkerOnlyHeadings()
    {
        var race = ParseReal("Races", "Талосианцы.txt");

        Assert.Equal("SourceFiles/Races/Талосианцы.txt", race.SourceFile);
        Assert.Equal("Талосианцы", race.Title);
        Assert.Equal("Talosian", race.OriginalTitle);
        Assert.Equal("race", race.EntityType);
        Assert.Null(race.Introduction);
        Assert.Equal(
            [
                "Общие сведения",
                "История",
                "История (продолжение 2)",
                "История (продолжение 3)",
                "Зеркальная вселенная",
                "Представители"
            ],
            race.Sections.Select(section => section.Title).ToArray());
        Assert.All(race.Sections, section =>
        {
            Assert.Equal(4, section.Level);
            Assert.Equal([section.Title], section.Path);
            Assert.NotEmpty(section.Body);
            Assert.DoesNotContain(':', section.Title);
        });
        Assert.Equal(4, race.Sections[0].StartLine);
        Assert.StartsWith("Талосианцы — древняя", race.Sections[0].Body);
        Assert.DoesNotContain('\r', race.NormalizedText);
    }

    [Fact]
    public void ParsesRealPlanetFileThatStartsWithSecondLevelHeading()
    {
        var planet = ParseReal("Planets", "марс.txt");

        Assert.Equal("Марс", planet.Title);
        Assert.Equal("planet", planet.EntityType);
        var title = Assert.Single(planet.Headings, heading => heading.IsDocumentTitle);
        Assert.Equal(2, title.Level);
        Assert.NotNull(planet.Introduction);
        Assert.Equal(3, planet.Introduction.StartLine);
        Assert.StartsWith("**Также известен как:**", planet.Introduction.Body);
        Assert.Contains(planet.Sections, section => section.Path.SequenceEqual(["Класс"]));
        Assert.Contains(planet.Sections, section => section.Path.SequenceEqual(["История", "Далёкое прошлое"]));
        Assert.Contains(planet.Sections, section => section.Path.SequenceEqual(["Колонизация людьми"]));
    }

    [Fact]
    public void ParsesRealSeriesFile()
    {
        var series = ParseReal("Series", "Описание серий.md");

        Assert.Equal("Звёздный путь: Странные новые миры", series.Title);
        Assert.Equal("Star Trek: Strange New Worlds", series.OriginalTitle);
        Assert.Equal("series", series.EntityType);
        Assert.Contains(series.Sections, section => section.Path.SequenceEqual(["Эпизоды", "Сезон 1 (2022)"]));
        Assert.Contains(series.Sections, section => section.Path.SequenceEqual(["Приём", "Награды"]));
    }

    [Fact]
    public void ParsesRealStarshipRegistryOneShipPerSection()
    {
        var starships = ParseReal("StarShips", "СписокКораблейФедерацииЗвездногоФлота.md");

        Assert.Equal("Подробный реестр ключевых кораблей Звездного флота Федерации", starships.Title);
        Assert.Equal("starship", starships.EntityType);
        Assert.NotNull(starships.Introduction);
        var ships = starships.Sections.Where(section => section.Level == 3).ToList();
        Assert.All(ships, ship => Assert.Equal(2, ship.Path.Count));
        var original = Assert.Single(ships, ship => ship.Title.EndsWith("USS Enterprise (NCC-1701)"));
        var refit = Assert.Single(ships, ship => ship.Title.EndsWith("USS Enterprise (NCC-1701-A)"));
        Assert.Equal("Линейка судов Enterprise («Энтерпрайз»)", original.Path[0]);
        Assert.True(original.EndLine < refit.StartLine);
        Assert.DoesNotContain("NCC-1701-A", original.Body);
        Assert.DoesNotContain(
            ships,
            ship => ship.Body.Split('\n').Count(line => line.Contains("**Класс:**")) > 1);
    }

    private ParsedDocument ParseReal(string folder, string fileName)
    {
        var root = RepositoryPathResolver.FindRepositoryRoot(
            Environment.CurrentDirectory,
            AppContext.BaseDirectory);
        return parser.ParseFile(Path.Combine(root, "SourceFiles", folder, fileName), root);
    }
}
