using StarTrek.Indexer.Chunking;
using StarTrek.Indexer.Parsing;

namespace StarTrek.Indexer.Tests;

public sealed class DocumentChunkerTests
{
    private const int Limit = 480;
    private readonly DocumentParser parser = new();
    private readonly DocumentChunker chunker = TestEnvironment.CreateChunker();

    [Fact]
    public void RaceDialectTurnsHeaderIntoDocumentFields()
    {
        const string text =
            "# Талосианцы\n" +
            "Оригинальное название: Talosian\n\n" +
            "####\nОбщие сведения:\nТелепатическая раса с Талоса IV.\n\n" +
            "####\nПредставители:\n- Хранитель\n";

        var document = Chunk(text, "SourceFiles/Races/Талосианцы.txt");

        Assert.Equal("SourceFiles/Races/Талосианцы.txt", document.SourceFile);
        Assert.Equal("Талосианцы", document.Title);
        Assert.Equal("Talosian", document.OriginalTitle);
        Assert.Equal("race", document.EntityType);
        Assert.Equal("Star Trek", document.Franchise);
        Assert.Equal("ru", document.Language);
        Assert.Equal("txt", document.FileType);
        Assert.Collection(
            document.Chunks,
            chunk =>
            {
                Assert.Equal(1, chunk.ChunkIndex);
                Assert.Equal(4, chunk.HeadingLevel);
                Assert.Equal("Общие сведения", chunk.SectionTitle);
                Assert.Equal(["Общие сведения"], chunk.SectionPath);
                Assert.Equal("lines 4-6", chunk.Position);
                Assert.Equal(4, chunk.StartLine);
                Assert.Equal(6, chunk.EndLine);
                Assert.Equal("Телепатическая раса с Талоса IV.", chunk.Body);
                Assert.Equal(
                    "Документ: Талосианцы\nТип сущности: раса\nРаздел: Общие сведения\n\n" +
                    "Телепатическая раса с Талоса IV.",
                    chunk.EmbeddingText);
            },
            chunk =>
            {
                Assert.Equal(2, chunk.ChunkIndex);
                Assert.Equal("Представители", chunk.SectionTitle);
                Assert.Equal("lines 8-10", chunk.Position);
                Assert.Equal("- Хранитель", chunk.Body);
            });
        Assert.DoesNotContain(document.Chunks, chunk => chunk.Body.Contains("Оригинальное название"));
    }

    [Fact]
    public void PlanetDialectKeepsIntroductionAndNestedPathAndSkipsEmptySection()
    {
        const string text =
            "## Марс\n\n" +
            "**Также известен как:** Сол IV.\n\n" +
            "## История\n\n" +
            "### Далёкое прошлое\nМарсиане жили на планете.\n\n" +
            "## Колонизация\nЛюди основали колонии.\n";

        var document = Chunk(text, "SourceFiles/Planets/марс.txt");

        Assert.Equal("Марс", document.Title);
        Assert.Equal("planet", document.EntityType);
        Assert.Collection(
            document.Chunks,
            chunk =>
            {
                Assert.Equal(0, chunk.HeadingLevel);
                Assert.Equal([ParsedDocument.IntroductionTitle], chunk.SectionPath);
                Assert.Equal("lines 3-3", chunk.Position);
                Assert.Equal("**Также известен как:** Сол IV.", chunk.Body);
            },
            chunk =>
            {
                Assert.Equal(3, chunk.HeadingLevel);
                Assert.Equal(["История", "Далёкое прошлое"], chunk.SectionPath);
                Assert.Equal("lines 7-8", chunk.Position);
                Assert.StartsWith("Документ: Марс\nТип сущности: планета\nРаздел: История > Далёкое прошлое\n\n", chunk.EmbeddingText);
            },
            chunk =>
            {
                Assert.Equal(2, chunk.HeadingLevel);
                Assert.Equal(["Колонизация"], chunk.SectionPath);
                Assert.Equal("Люди основали колонии.", chunk.Body);
            });
    }

    [Fact]
    public void FileWithoutLeadingHeadingTakesTitleFromFileName()
    {
        var document = Chunk("**Тип:** планета.\n\n#### История\nТекст истории.\n", "SourceFiles/Planets/теллар.txt");

        Assert.Equal("Теллар", document.Title);
        Assert.Null(document.OriginalTitle);
        Assert.Equal([ParsedDocument.IntroductionTitle], document.Chunks[0].SectionPath);
        Assert.Equal("**Тип:** планета.", document.Chunks[0].Body);
        Assert.Equal(["История"], document.Chunks[1].SectionPath);
    }

    [Fact]
    public void StarshipDialectKeepsEveryShipInOwnChunk()
    {
        const string text =
            "# Реестр кораблей\n\nСправочник.\n\n---\n\n" +
            "## Класс Constitution\n\n" +
            "### USS Enterprise (NCC-1701)\n* **Класс:** Constitution\n* **Реестр:** NCC-1701\n\n" +
            "### USS Enterprise (NCC-1701-A)\n* **Класс:** Constitution (Refit)\n* **Реестр:** NCC-1701-A\n\n---\n";

        var document = Chunk(text, "SourceFiles/StarShips/registry.md");

        Assert.Equal("starship", document.EntityType);
        Assert.Equal("md", document.FileType);
        Assert.Collection(
            document.Chunks,
            chunk =>
            {
                Assert.Equal([ParsedDocument.IntroductionTitle], chunk.SectionPath);
                Assert.Equal("Справочник.", chunk.Body);
                Assert.Equal("lines 3-3", chunk.Position);
            },
            chunk =>
            {
                Assert.Equal(["Класс Constitution", "USS Enterprise (NCC-1701)"], chunk.SectionPath);
                Assert.Equal("* **Класс:** Constitution\n* **Реестр:** NCC-1701", chunk.Body);
                Assert.Equal("lines 9-11", chunk.Position);
            },
            chunk =>
            {
                Assert.Equal(["Класс Constitution", "USS Enterprise (NCC-1701-A)"], chunk.SectionPath);
                Assert.Equal("* **Класс:** Constitution (Refit)\n* **Реестр:** NCC-1701-A", chunk.Body);
                Assert.Equal("lines 13-15", chunk.Position);
            });
        Assert.DoesNotContain(document.Chunks, chunk => chunk.Body.Contains("---"));
    }

    [Fact]
    public void ShortSectionsStaySeparate()
    {
        const string text =
            "# Раса\n" +
            "####\nОбщие сведения:\nКороткий факт А.\n\n" +
            "####\nИстория:\nКороткий факт Б.\n\n" +
            "####\nКультура:\nКороткий факт В.\n";

        var document = Chunk(text, "SourceFiles/Races/Раса.txt");

        Assert.Equal(["Короткий факт А.", "Короткий факт Б.", "Короткий факт В."], document.Chunks.Select(chunk => chunk.Body));
        Assert.Equal(["Общие сведения", "История", "Культура"], document.Chunks.Select(chunk => chunk.SectionTitle));
        Assert.All(document.Chunks, chunk => Assert.True(chunk.TokenCount < TestEnvironment.ChunkingOptions.ShortSectionThreshold));
    }

    [Fact]
    public void LongSectionIsSplitBySentencesWithContinuationTitlesAndOverlap()
    {
        var sentences = Enumerable.Range(1, 45)
            .Select(index => $"Факт номер {index} описывает рейс звездолёта NCC-1701-D к планете Вулкан.")
            .ToList();
        var text = "# Вулканцы\nОригинальное название: Vulcan\n\n####\nИстория:\n" + string.Join(' ', sentences) + "\n";

        var document = Chunk(text, "SourceFiles/Races/Вулканцы.txt");

        Assert.True(document.Chunks.Count >= 3);
        Assert.Equal("История", document.Chunks[0].SectionTitle);
        Assert.Equal(4, document.Chunks[0].StartLine);
        for (var index = 1; index < document.Chunks.Count; index++)
        {
            var previous = document.Chunks[index - 1];
            var current = document.Chunks[index];
            Assert.Equal($"История (продолжение {index + 1})", current.SectionTitle);
            Assert.Equal([current.SectionTitle], current.SectionPath);
            Assert.Contains($"Раздел: История (продолжение {index + 1})\n", current.EmbeddingText);
            Assert.Equal(6, current.StartLine);
            var carried = SentenceSplitter.Split(current.Body)[0];
            Assert.Contains(carried, SentenceSplitter.Split(previous.Body).TakeLast(2));
        }

        AssertWithinLimit(document);
        var pieces = document.Chunks.SelectMany(chunk => SentenceSplitter.Split(chunk.Body)).ToHashSet();
        Assert.True(pieces.SetEquals(sentences));
    }

    [Fact]
    public void LongSectionIsSplitAtBlankLinesBeforeSentences()
    {
        var paragraphs = Enumerable.Range(1, 4)
            .Select(paragraph => string.Join(' ', Enumerable.Range(1, 8).Select(index =>
                $"Абзац {paragraph} факт {index} сообщает, что звездолёт NCC-1701-D прибыл к станции Глубокий космос 9.")))
            .ToList();
        var text = "# Бетазед\n\n## История\n" + string.Join("\n\n", paragraphs) + "\n";

        var document = Chunk(text, "SourceFiles/Planets/бетазэд.txt");

        Assert.True(document.Chunks.Count >= 2);
        AssertWithinLimit(document);
        Assert.All(paragraphs, paragraph => Assert.Contains(document.Chunks, chunk => chunk.Body.Contains(paragraph)));
    }

    [Fact]
    public void ListStaysWithItsIntroductionLine()
    {
        var prose = string.Join(' ', Enumerable.Range(1, 24).Select(index =>
            $"Предложение {index} рассказывает о службе крейсера класса Galaxy в секторе 001."));
        var items = Enumerable.Range(1, 6).Select(index => $"- USS Корабль-{index} (NCC-7{index}000)").ToList();
        var list = "Известные корабли класса:\n" + string.Join('\n', items);
        var text = "# Реестр\n\n## Класс Galaxy\n" + prose + "\n" + list + "\n";

        var document = Chunk(text, "SourceFiles/StarShips/galaxy.md");

        Assert.True(document.Chunks.Count >= 2);
        AssertWithinLimit(document);
        Assert.Contains(document.Chunks, chunk => chunk.Body.Contains(list));
        Assert.DoesNotContain(document.Chunks, chunk => chunk.Body.TrimEnd().EndsWith("Известные корабли класса:"));
    }

    [Fact]
    public void LongListRepeatsIntroductionInEveryContinuation()
    {
        var items = Enumerable.Range(1, 80)
            .Select(index => $"- Офицер номер {index} служил на мостике USS Enterprise (NCC-1701-D).")
            .ToList();
        var text = "# Реестр\n\n## Класс Galaxy\n\n### USS Enterprise (NCC-1701-D)\nЭкипаж корабля:\n" +
                   string.Join('\n', items) + "\n";

        var document = Chunk(text, "SourceFiles/StarShips/crew.md");

        Assert.True(document.Chunks.Count >= 3);
        AssertWithinLimit(document);
        Assert.All(document.Chunks, chunk => Assert.Equal("Экипаж корабля:", chunk.Body.Split('\n')[0]));
        var listed = document.Chunks.SelectMany(chunk => chunk.Body.Split('\n').Skip(1)).Distinct().ToList();
        Assert.Equal(items, listed);
    }

    [Fact]
    public void LongListRepeatsIntroductionEvenWhenItExceedsOverlapLimit()
    {
        var introduction = string.Join(' ', Enumerable.Repeat("Подробный контекст списка", 45)) + ":";
        var items = Enumerable.Range(1, 60)
            .Select(index => $"- Корабль номер {index} имеет регистрацию NCC-{index:0000}.")
            .ToList();
        var text = "# Реестр\n\n## Корабли\n" + introduction + "\n" + string.Join('\n', items) + "\n";

        var document = Chunk(text, "SourceFiles/StarShips/registry.md");

        Assert.True(
            TestEnvironment.Tokenizer.CountTokens(introduction, addSpecialTokens: false) >
            TestEnvironment.ChunkingOptions.OverlapMaxTokens);
        Assert.True(document.Chunks.Count >= 2);
        AssertWithinLimit(document);
        Assert.All(document.Chunks, chunk => Assert.StartsWith(introduction + "\n", chunk.Body));
        Assert.Equal(items, document.Chunks
            .SelectMany(chunk => chunk.Body.Split('\n').Skip(1))
            .Distinct()
            .ToList());
    }

    [Fact]
    public void LongFieldValueRepeatsItsLabelAndShortFieldsStayWhole()
    {
        var sentences = Enumerable.Range(1, 40)
            .Select(index => $"Эпизод {index} службы закончился возвращением на станцию Глубокий космос 9.")
            .ToList();
        var text = "# Реестр\n\n## Класс Galaxy\n\n### USS Enterprise (NCC-1701-D)\n" +
                   "* **Класс:** Galaxy\n* **Реестр:** NCC-1701-D\n" +
                   "* **Технические особенности и история:** " + string.Join(' ', sentences) + "\n";

        var document = Chunk(text, "SourceFiles/StarShips/enterprise.md");

        Assert.True(document.Chunks.Count >= 3);
        AssertWithinLimit(document);
        Assert.StartsWith("* **Класс:** Galaxy\n* **Реестр:** NCC-1701-D\n", document.Chunks[0].Body);
        Assert.All(
            document.Chunks.Skip(1),
            chunk => Assert.StartsWith("* **Технические особенности и история:** ", chunk.Body));
        Assert.All(
            document.Chunks,
            chunk => Assert.All(
                SentenceSplitter.Split(chunk.Body.Split('\n')[^1].Replace("* **Технические особенности и история:** ", "")),
                sentence => Assert.Contains(sentence, sentences)));
    }

    [Fact]
    public void LongPlainFieldValueRepeatsItsLabel()
    {
        const string label = "Описание: ";
        var sentences = Enumerable.Range(1, 40)
            .Select(index => $"Факт {index} описывает экспедицию корабля NCC-1701-D к планете Вулкан.")
            .ToList();
        var text = "# Реестр\n\n## Корабль\n" + label + string.Join(' ', sentences) + "\n";

        var document = Chunk(text, "SourceFiles/StarShips/registry.md");

        Assert.True(document.Chunks.Count >= 2);
        AssertWithinLimit(document);
        Assert.All(document.Chunks, chunk => Assert.StartsWith(label, chunk.Body));
        Assert.All(
            document.Chunks,
            chunk => Assert.All(
                SentenceSplitter.Split(chunk.Body[label.Length..]),
                sentence => Assert.Contains(sentence, sentences)));
    }

    [Fact]
    public void FieldValuePairsAreNeverCut()
    {
        var lines = Enumerable.Range(1, 50)
            .Select(index => $"- **Параметр {index}:** значение {index} для корабля NCC-1701-D, класс Galaxy.")
            .ToList();
        var text = "# Сериал\n\n## Краткие сведения\n" + string.Join('\n', lines) + "\n";

        var document = Chunk(text, "SourceFiles/Series/series.md");

        Assert.True(document.Chunks.Count >= 2);
        AssertWithinLimit(document);
        Assert.All(document.Chunks, chunk => Assert.All(chunk.Body.Split('\n'), line => Assert.Contains(line, lines)));
        Assert.All(lines, line => Assert.Contains(document.Chunks, chunk => chunk.Body.Split('\n').Contains(line)));
    }

    [Fact]
    public void SentenceLongerThanLimitIsSplitAtTokenBoundariesWithOverlap()
    {
        var words = Enumerable.Range(1, 600).Select(index => $"слово{index}").ToList();
        var text = "# Документ\n\n## Раздел\n" + string.Join(' ', words) + "\n";

        var document = Chunk(text, "SourceFiles/Other/long.md");

        Assert.True(document.Chunks.Count >= 3);
        AssertWithinLimit(document);
        var chunkWords = document.Chunks.Select(chunk => chunk.Body.Split(' ')).ToList();
        Assert.All(chunkWords.SelectMany(items => items), word => Assert.Contains(word, words));
        Assert.Equal(words, chunkWords.SelectMany(items => items).Distinct());
        for (var index = 1; index < chunkWords.Count; index++)
        {
            Assert.Contains(chunkWords[index][0], chunkWords[index - 1]);
        }
    }

    [Fact]
    public void ContinuationNumbersDoNotCollideWithSourceContinuations()
    {
        var longText = string.Join(' ', Enumerable.Range(1, 30)
            .Select(index => $"Факт {index} о торговле лиссепианцев с кардассианцами на станции Deep Space 9."));
        var text = "# Лиссепианцы\nОригинальное название: Lissepian\n\n" +
                   "####\nОбщие сведения:\n" + longText + "\n\n" +
                   "####\nОбщие сведения (продолжение 2):\nКороткий факт о корабле снабжения.\n";

        var document = Chunk(text, "SourceFiles/Races/Лиссепианцы.txt");

        var titles = document.Chunks.Select(chunk => chunk.SectionTitle).ToList();
        Assert.Equal("Общие сведения", titles[0]);
        Assert.Equal(
            Enumerable.Range(2, titles.Count - 1).Select(number => $"Общие сведения (продолжение {number})"),
            titles.Skip(1));
        Assert.Equal("Короткий факт о корабле снабжения.", document.Chunks[^1].Body);
        AssertWithinLimit(document);
    }

    [Fact]
    public void RealTalosianFileIsChunkedWithinLimit()
    {
        var document = ChunkReal("Races", "Талосианцы.txt");

        Assert.Equal("Талосианцы", document.Title);
        Assert.Equal("Talosian", document.OriginalTitle);
        Assert.Equal(DocumentIdentity.ComputeDocumentId("SourceFiles/Races/Талосианцы.txt"), document.DocumentId);
        Assert.Equal(
            [
                "Общие сведения",
                "История",
                "История (продолжение 2)",
                "История (продолжение 3)",
                "История (продолжение 4)",
                "Зеркальная вселенная",
                "Представители"
            ],
            document.Chunks.Select(chunk => chunk.SectionTitle));
        Assert.Equal(Enumerable.Range(1, document.Chunks.Count), document.Chunks.Select(chunk => chunk.ChunkIndex));
        AssertWithinLimit(document);
        var firstPiece = document.Chunks[2];
        var secondPiece = document.Chunks[3];
        Assert.Equal(15, firstPiece.StartLine);
        Assert.Equal(19, secondPiece.EndLine);
        Assert.Contains(SentenceSplitter.Split(firstPiece.Body)[^1], secondPiece.Body.Split('\n')[0]);
        Assert.Contains("NCIA-93", secondPiece.Body);
    }

    [Fact]
    public void RealFederationRegistryKeepsShipsApart()
    {
        var document = ChunkReal("StarShips", "СписокКораблейФедерацииЗвездногоФлота.md");

        var ships = document.Chunks.Where(chunk => chunk.HeadingLevel == 3).ToList();
        Assert.NotEmpty(ships);
        Assert.All(ships, ship => Assert.Equal(2, ship.SectionPath.Count));
        Assert.DoesNotContain(
            document.Chunks,
            chunk => chunk.Body.Split('\n').Count(line => line.Contains("**Класс:**")) > 1);
        var original = Assert.Single(ships, ship => ship.SectionTitle.EndsWith("USS Enterprise (NCC-1701)"));
        Assert.DoesNotContain("NCC-1701-A", original.Body);
        Assert.Single(ships, ship => ship.SectionTitle.EndsWith("USS Enterprise (NCC-1701-D)"));
        Assert.DoesNotContain(document.Chunks, chunk => chunk.Body.Contains("---"));
        AssertWithinLimit(document);
    }

    [Fact]
    public void WholeCorpusFitsTokenLimit()
    {
        var corpus = Path.Combine(TestEnvironment.RepositoryRoot, "SourceFiles");
        var documents = CorpusFiles.Enumerate(corpus)
            .Select(path => chunker.Chunk(parser.ParseFile(path, TestEnvironment.RepositoryRoot)).Document)
            .ToList();

        Assert.NotEmpty(documents);
        Assert.All(documents, AssertWithinLimit);
        Assert.All(documents, document =>
        {
            Assert.Equal(Enumerable.Range(1, document.Chunks.Count), document.Chunks.Select(chunk => chunk.ChunkIndex));
            Assert.Equal(
                document.Chunks.Count,
                document.Chunks.Select(chunk => string.Join(" > ", chunk.SectionPath)).Distinct().Count());
        });
    }

    private static void AssertWithinLimit(ChunkedDocument document)
    {
        Assert.All(document.Chunks, chunk =>
        {
            Assert.True(chunk.TokenCount <= Limit, $"{document.SourceFile} #{chunk.ChunkIndex}: {chunk.TokenCount}");
            Assert.Equal(TestEnvironment.Tokenizer.CountPassageTokens(chunk.EmbeddingText), chunk.TokenCount);
            Assert.DoesNotContain("passage:", chunk.EmbeddingText);
            Assert.DoesNotContain('#', chunk.EmbeddingText);
            Assert.DoesNotContain('#', chunk.Body);
            Assert.EndsWith(chunk.Body, chunk.EmbeddingText);
            Assert.True(chunk.StartLine <= chunk.EndLine);
            Assert.Equal($"lines {chunk.StartLine}-{chunk.EndLine}", chunk.Position);
            Assert.Equal(chunk.SectionTitle, chunk.SectionPath[^1]);
        });
    }

    private ChunkedDocument Chunk(string text, string sourceFile) =>
        chunker.Chunk(parser.Parse(text, sourceFile)).Document;

    private ChunkedDocument ChunkReal(string folder, string fileName) =>
        chunker.Chunk(parser.ParseFile(
            Path.Combine(TestEnvironment.RepositoryRoot, "SourceFiles", folder, fileName),
            TestEnvironment.RepositoryRoot)).Document;
}
