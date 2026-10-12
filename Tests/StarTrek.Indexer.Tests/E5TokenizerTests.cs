using StarTrek.Indexer.Configuration;
using StarTrek.Indexer.Tokenization;

namespace StarTrek.Indexer.Tests;

public sealed class E5TokenizerTests
{
    [Theory]
    [MemberData(nameof(ReferenceEncodings))]
    public void ProducesReferenceHuggingFaceIds(string text, long[] expectedIds)
    {
        using var tokenizer = CreateTokenizer();

        var actual = tokenizer.Encode(text).InputIds;

        Assert.Equal(expectedIds, actual);
    }

    [Fact]
    public void MapsSpecialTokensToXlmRobertaIds()
    {
        using var tokenizer = CreateTokenizer();

        var ids = tokenizer.Encode("<s><pad></s><unk>", addSpecialTokens: false).InputIds;

        Assert.Equal(
            [
                E5Tokenizer.StartTokenId,
                E5Tokenizer.PaddingTokenId,
                E5Tokenizer.EndTokenId,
                E5Tokenizer.UnknownTokenId
            ],
            ids);
        Assert.Equal([0L, 1L, 2L, 3L], ids);
    }

    [Fact]
    public void CanEncodeWithoutSpecialTokens()
    {
        using var tokenizer = CreateTokenizer();

        var withSpecialTokens = tokenizer.Encode("Звёздный путь").InputIds;
        var withoutSpecialTokens = tokenizer.Encode("Звёздный путь", addSpecialTokens: false).InputIds;

        Assert.Equal(0, withSpecialTokens[0]);
        Assert.Equal(2, withSpecialTokens[^1]);
        Assert.Equal(withSpecialTokens.Skip(1).SkipLast(1), withoutSpecialTokens);
    }

    [Fact]
    public void PassageApiAddsRequiredPrefix()
    {
        using var tokenizer = CreateTokenizer();

        var passage = tokenizer.EncodePassage("Талосианцы").InputIds;
        var explicitPrefix = tokenizer.Encode("passage: Талосианцы", maxLength: 512).InputIds;

        Assert.Equal(explicitPrefix, passage);
    }

    [Fact]
    public void TruncatesToRequestedLengthAndPreservesEndToken()
    {
        using var tokenizer = CreateTokenizer();
        var text = string.Join(' ', Enumerable.Repeat("Enterprise NCC-1701-D", 200));

        var encoding = tokenizer.Encode(text, maxLength: 512);

        Assert.True(tokenizer.CountTokens(text) > 512);
        Assert.Equal(512, encoding.InputIds.Count);
        Assert.Equal(0, encoding.InputIds[0]);
        Assert.Equal(2, encoding.InputIds[^1]);
        Assert.All(encoding.AttentionMask, value => Assert.Equal(1, value));
    }

    [Fact]
    public void PassageEncodingTruncatesToModelWindowAndPreservesEndToken()
    {
        using var tokenizer = CreateTokenizer();
        var text = string.Join(' ', Enumerable.Repeat("Талосианцы", 600));

        var encoding = tokenizer.EncodePassage(text);

        Assert.True(tokenizer.CountPassageTokens(text) > E5Tokenizer.ModelMaximumLength);
        Assert.Equal(E5Tokenizer.ModelMaximumLength, encoding.InputIds.Count);
        Assert.Equal(E5Tokenizer.StartTokenId, encoding.InputIds[0]);
        Assert.Equal(E5Tokenizer.EndTokenId, encoding.InputIds[^1]);
    }

    [Fact]
    public void MissingTokenizerHasActionableError()
    {
        var exception = Assert.Throws<FileNotFoundException>(
            () => new E5Tokenizer(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));

        Assert.Contains("tokenizer.json", exception.Message);
        Assert.Contains(E5Tokenizer.DownloadUrl, exception.Message);
    }

    public static TheoryData<string, long[]> ReferenceEncodings => new()
    {
        { "", [0, 2] },
        { "Hello, world!", [0, 35378, 4, 8999, 38, 2] },
        { "Звёздный путь", [0, 77779, 95419, 526, 2192, 87054, 2] },
        {
            "passage: Талосианцы — телепатическая раса.",
            [0, 46692, 12, 3374, 1219, 3007, 59, 90832, 292, 18293, 19008, 57715, 3433, 59, 5, 2]
        },
        {
            "Документ: Талосианцы\nТип сущности: раса\nРаздел: История\n\n" +
            "В 2254 году талосианцы захватили капитана «Энтерпрайз» Кристофера Пайка.",
            [
                0, 87956, 12, 3374, 1219, 3007, 59, 90832, 108551, 6, 113727, 4544, 12, 3433, 59, 7720,
                10347, 12, 128972, 417, 1039, 12338, 8165, 489, 1219, 3007, 59, 90832, 121320, 3107,
                165361, 59, 94, 9223, 303, 2757, 11447, 312, 1316, 167, 128155, 328, 15997, 59, 3512,
                57213, 5, 2
            ]
        },
        {
            "query: Какой реестровый номер у USS Enterprise NCC-1701-D?",
            [
                0, 41, 1294, 12, 11930, 312, 149520, 69088, 18315, 84, 7082, 294, 102247, 541, 13709,
                13521, 6746, 9, 397, 32, 2
            ]
        }
    };

    private static E5Tokenizer CreateTokenizer()
    {
        var root = RepositoryPathResolver.FindRepositoryRoot(
            Environment.CurrentDirectory,
            AppContext.BaseDirectory);
        return new E5Tokenizer(
            Path.Combine(root, "Models", "multilingual-e5-small", "tokenizer.json"));
    }
}
