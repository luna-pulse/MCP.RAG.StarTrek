using StarTrek.Indexer.Bm25;

namespace StarTrek.Indexer.Tests;

public sealed class Bm25TokenizerTests
{
    [Fact]
    public void RegistryCodeStaysOneLowercaseToken()
    {
        var tokens = Bm25Tokenizer.Tokenize("Звездолёт USS Enterprise (NCC-1701-D), капитан Пикар.");

        Assert.Equal(["звездолёт", "uss", "enterprise", "ncc-1701-d", "капитан", "пикар"], tokens);
    }

    [Theory]
    [InlineData("NX-01", "nx-01")]
    [InlineData("NCC-74656", "ncc-74656")]
    [InlineData("NCC\u20111701", "ncc-1701")]
    public void CodesWithDigitsAreNotSplit(string text, string expected) =>
        Assert.Equal([expected], Bm25Tokenizer.Tokenize(text));

    [Fact]
    public void WordsWithoutDigitsAreSplitByHyphen() =>
        Assert.Equal(["научно", "исследовательский", "корабль"], Bm25Tokenizer.Tokenize("научно-исследовательский корабль"));

    [Fact]
    public void PunctuationAndWhitespaceSeparateTokens()
    {
        var tokens = Bm25Tokenizer.Tokenize("Кирк — капитан; Спок: «первый офицер»!\n- пункт - ещё");

        Assert.Equal(["кирк", "капитан", "спок", "первый", "офицер", "пункт", "ещё"], tokens);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ,.;- — ")]
    public void EmptyOrPunctuationOnlyTextHasNoTokens(string? text) =>
        Assert.Empty(Bm25Tokenizer.Tokenize(text));

    [Fact]
    public void TrailingHyphenIsNotPartOfToken() =>
        Assert.Equal(["ncc", "1701"], Bm25Tokenizer.Tokenize("NCC- 1701"));
}

public sealed class Bm25IndexTests
{
    [Fact]
    public void ScoreMatchesOkapiFormulaWithDefaultParameters()
    {
        var index = Bm25Index.Create(
        [
            new Bm25Document("d1", ["a", "b", "b"]),
            new Bm25Document("d2", ["a", "c"]),
            new Bm25Document("d3", ["c", "c", "c", "c"])
        ]);

        var hit = Assert.Single(index.Search(["b"], topN: 5));

        const double k1 = 1.5;
        const double b = 0.75;
        var idf = Math.Log(1 + ((3 - 1 + 0.5) / (1 + 0.5)));
        var averageLength = 9.0 / 3;
        var expected = idf * 2 * (k1 + 1) / (2 + (k1 * (1 - b + (b * 3 / averageLength))));
        Assert.Equal("d1", hit.Id);
        Assert.Equal(expected, hit.Score, precision: 10);
        Assert.Equal(1.5, index.Parameters.K1);
        Assert.Equal(0.75, index.Parameters.B);
    }

    [Fact]
    public void TinyCorpusRanksByTermRarityFrequencyAndLength()
    {
        var index = Bm25Index.Create(
            Documents(
                ("enterprise", "Звездолёт Энтерпрайз NCC-1701-D, капитан Пикар, экипаж Федерации"),
                ("voyager", "Звездолёт Вояджер NCC-74656, капитан Джейнвей, экипаж Федерации"),
                ("vulcan", "Вулкан, планета Спока: Спок, логика, Спок и логика")));

        Assert.Equal(["enterprise"], index.Search("NCC-1701-D", 5).Select(hit => hit.Id));
        Assert.Equal(["vulcan"], index.Search("спок", 5).Select(hit => hit.Id));
        Assert.Equal(["enterprise", "voyager"], index.Search("капитан экипаж", 5).Select(hit => hit.Id));
        Assert.Equal(["voyager"], index.Search("джейнвей", 5).Select(hit => hit.Id));

        var ranking = index.Search("звездолёт спок", 5);
        Assert.Equal("vulcan", ranking[0].Id);
        Assert.Equal(3, ranking.Count);
        Assert.True(ranking[0].Score > ranking[1].Score);
    }

    [Fact]
    public void RepeatedTermInDocumentScoresHigherButSaturates()
    {
        var index = Bm25Index.Create(
        [
            new Bm25Document("one", ["warp", "x", "y", "z"]),
            new Bm25Document("three", ["warp", "warp", "warp", "z"]),
            new Bm25Document("many", ["warp", "warp", "warp", "warp"]),
            new Bm25Document("none", ["x", "y", "z", "q"])
        ]);

        var hits = index.Search("warp", 10);

        Assert.Equal(["many", "three", "one"], hits.Select(hit => hit.Id));
        Assert.True(hits[0].Score - hits[1].Score < hits[1].Score - hits[2].Score);
    }

    [Fact]
    public void TieBreaksByIdAndTopNLimitsResults()
    {
        var index = Bm25Index.Create(
        [
            new Bm25Document("b", ["x"]),
            new Bm25Document("a", ["x"]),
            new Bm25Document("c", ["y"])
        ]);

        Assert.Equal(["a", "b"], index.Search("x", 10).Select(hit => hit.Id));
        Assert.Equal(["a"], index.Search("x", 1).Select(hit => hit.Id));
    }

    [Fact]
    public void UnknownOrEmptyQueryAndEmptyIndexReturnNothing()
    {
        var index = Bm25Index.Create([new Bm25Document("a", ["x"])]);

        Assert.Empty(index.Search("неизвестное", 5));
        Assert.Empty(index.Search(" ,.", 5));
        Assert.Empty(Bm25Index.Create([]).Search("x", 5));
    }

    [Fact]
    public void DuplicateQueryTermsAreCountedOnce()
    {
        var index = Bm25Index.Create([new Bm25Document("a", ["x", "y"]), new Bm25Document("b", ["y"])]);

        Assert.Equal(index.Search("x", 5)[0].Score, index.Search("x x x", 5)[0].Score);
    }

    [Fact]
    public void DuplicateDocumentIdsAreRejected() =>
        Assert.Throws<ArgumentException>(() =>
            Bm25Index.Create([new Bm25Document("a", ["x"]), new Bm25Document("a", ["y"])]));

    [Theory]
    [InlineData(-1, 0.75)]
    [InlineData(1.5, 1.1)]
    [InlineData(1.5, -0.1)]
    public void InvalidParametersAreRejected(double k1, double b) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Bm25Index.Create([], new Bm25Parameters(k1, b)));

    internal static IEnumerable<Bm25Document> Documents(params (string Id, string Text)[] chunks) =>
        chunks.Select(chunk => new Bm25Document(chunk.Id, Bm25Tokenizer.Tokenize(chunk.Text)));
}

public sealed class Bm25CorpusStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "startrek-bm25-" + Guid.NewGuid().ToString("N"));

    private string CorpusPath => Path.Combine(root, "nested", "corpus.json");

    [Fact]
    public void SavedCorpusLoadsBackAndGivesSameRanking()
    {
        var chunks = SampleChunks();
        var store = new Bm25CorpusStore(CorpusPath);
        var corpus = Bm25CorpusStore.Create("startrek", chunks);

        store.Save(corpus);
        var loaded = store.Load();

        Assert.True(File.Exists(CorpusPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(CorpusPath)!, "*.tmp"));
        Assert.Equal(Bm25CorpusStore.ComputeFingerprint(chunks), loaded.Fingerprint);
        Assert.Equal("startrek", loaded.Collection);
        Assert.True(loaded.IsCurrentFormat);
        Assert.Equal(corpus.Chunks.Select(chunk => chunk.Id), loaded.Chunks.Select(chunk => chunk.Id));
        Assert.Equal(corpus.Chunks.Select(chunk => chunk.Tokens), loaded.Chunks.Select(chunk => chunk.Tokens));
        var fresh = Bm25Index.Create(corpus.ToDocuments());
        var restored = Bm25Index.Create(loaded.ToDocuments());
        Assert.Equal(
            fresh.Search("ncc-1701-d капитан", 5).Select(hit => (hit.Id, hit.Score)),
            restored.Search("ncc-1701-d капитан", 5).Select(hit => (hit.Id, hit.Score)));
    }

    [Fact]
    public void FileIsReadableUtf8JsonWithSnakeCaseFields()
    {
        new Bm25CorpusStore(CorpusPath).Save(Bm25CorpusStore.Create("startrek", SampleChunks()));

        var text = File.ReadAllText(CorpusPath);

        Assert.Contains("\"format_version\":1", text);
        Assert.Contains("\"tokenizer_version\":1", text);
        Assert.Contains("\"ncc-1701-d\"", text);
        Assert.Contains("\"звездолёт\"", text);
    }

    [Fact]
    public void SaveOverwritesPreviousCorpus()
    {
        var store = new Bm25CorpusStore(CorpusPath);
        store.Save(Bm25CorpusStore.Create("startrek", SampleChunks()));

        store.Save(Bm25CorpusStore.Create("startrek", [new Bm25Chunk("only:1", "один чанк")]));

        Assert.Equal(["only:1"], store.Load().Chunks.Select(chunk => chunk.Id));
    }

    [Fact]
    public void MissingFileThrowsFileNotFound() =>
        Assert.Throws<FileNotFoundException>(() => new Bm25CorpusStore(CorpusPath).Load());

    [Fact]
    public void CorruptFileThrowsInvalidData()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CorpusPath)!);
        File.WriteAllText(CorpusPath, "{ не json");

        Assert.Throws<InvalidDataException>(() => new Bm25CorpusStore(CorpusPath).Load());
    }

    [Fact]
    public void FingerprintDependsOnIdsAndTextsButNotOnOrder()
    {
        var chunks = SampleChunks();

        Assert.Equal(
            Bm25CorpusStore.ComputeFingerprint(chunks),
            Bm25CorpusStore.ComputeFingerprint(chunks.AsEnumerable().Reverse()));
        Assert.NotEqual(
            Bm25CorpusStore.ComputeFingerprint(chunks),
            Bm25CorpusStore.ComputeFingerprint([chunks[0] with { Text = chunks[0].Text + " ещё" }, chunks[1]]));
        Assert.NotEqual(
            Bm25CorpusStore.ComputeFingerprint(chunks),
            Bm25CorpusStore.ComputeFingerprint([chunks[0] with { Id = "other:1" }, chunks[1]]));
    }

    internal static List<Bm25Chunk> SampleChunks() =>
    [
        new("doc-a:1", "Звездолёт USS Enterprise NCC-1701-D, капитан Пикар"),
        new("doc-b:1", "Звездолёт USS Voyager NCC-74656, капитан Джейнвей")
    ];

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class Bm25IndexRebuilderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "startrek-bm25-hook-" + Guid.NewGuid().ToString("N"));

    private Bm25CorpusStore Store => new(Path.Combine(root, "bm25", "corpus.json"));

    [Fact]
    public async Task ChangedCollectionRebuildsSavesAndKeepsIndexInMemory()
    {
        var holder = NewHolder();
        var rebuilder = new Bm25IndexRebuilder(holder);

        var result = await rebuilder.RebuildAsync(Request(changed: true));

        Assert.True(result.Rebuilt);
        Assert.False(result.LoadedFromDisk);
        Assert.Equal(2, result.IndexedChunks);
        Assert.True(Store.Exists);
        Assert.Equal(2, holder.Current!.DocumentCount);
        Assert.Equal(["doc-a:1"], holder.Current.Search("ncc-1701-d", 5).Select(hit => hit.Id));
    }

    [Fact]
    public async Task UnchangedCollectionLoadsActualCorpusFromDiskWithoutRewriting()
    {
        await new Bm25IndexRebuilder(NewHolder()).RebuildAsync(Request(changed: true));
        var writtenAt = File.GetLastWriteTimeUtc(Store.Path);
        File.SetLastWriteTimeUtc(Store.Path, writtenAt.AddHours(-1));
        var marker = File.GetLastWriteTimeUtc(Store.Path);
        var holder = NewHolder();

        var result = await new Bm25IndexRebuilder(holder).RebuildAsync(Request(changed: false));

        Assert.False(result.Rebuilt);
        Assert.True(result.LoadedFromDisk);
        Assert.Equal(2, result.IndexedChunks);
        Assert.Equal(marker, File.GetLastWriteTimeUtc(Store.Path));
        Assert.Equal(["doc-b:1"], holder.Current!.Search("ncc-74656", 5).Select(hit => hit.Id));
    }

    [Fact]
    public async Task UnchangedCollectionWithoutCorpusRebuildsFromRequestedChunks()
    {
        var result = await new Bm25IndexRebuilder(NewHolder()).RebuildAsync(Request(changed: false));

        Assert.True(result.Rebuilt);
        Assert.True(Store.Exists);
    }

    [Fact]
    public async Task StaleCorpusIsRebuiltEvenIfCollectionWasReportedUnchanged()
    {
        await new Bm25IndexRebuilder(NewHolder()).RebuildAsync(Request(changed: true));
        var changedText = new Bm25RebuildRequest(
            "startrek",
            [new Bm25Chunk("doc-a:1", "Совсем другой текст"), new Bm25Chunk("doc-b:1", "Звездолёт Вояджер NCC-74656")],
            CollectionChanged: false);
        var holder = NewHolder();

        var result = await new Bm25IndexRebuilder(holder).RebuildAsync(changedText);

        Assert.True(result.Rebuilt);
        Assert.Equal(["doc-a:1"], holder.Current!.Search("другой", 5).Select(hit => hit.Id));
    }

    [Fact]
    public async Task CorpusOfAnotherCollectionOrCorruptFileIsRebuilt()
    {
        await new Bm25IndexRebuilder(NewHolder()).RebuildAsync(Request(changed: true, collection: "other"));
        var other = await new Bm25IndexRebuilder(NewHolder()).RebuildAsync(Request(changed: false));
        File.WriteAllText(Store.Path, "мусор");
        var corrupt = await new Bm25IndexRebuilder(NewHolder()).RebuildAsync(Request(changed: false));

        Assert.True(other.Rebuilt);
        Assert.True(corrupt.Rebuilt);
        Assert.Equal(2, Store.Load().Chunks.Count);
    }

    [Fact]
    public async Task HolderLoadsIndexFromDiskInNewProcessLikeInstance()
    {
        await new Bm25IndexRebuilder(NewHolder()).RebuildAsync(Request(changed: true));
        var freshHolder = NewHolder();

        var index = freshHolder.GetOrLoad("startrek");

        Assert.Same(index, freshHolder.Current);
        Assert.Same(index, freshHolder.GetOrLoad("startrek"));
        Assert.Equal(["doc-a:1"], index.Search("пикар", 5).Select(hit => hit.Id));
    }

    [Fact]
    public void HolderWithoutCorpusExplainsToRunEmbed()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => NewHolder().GetOrLoad("startrek"));

        Assert.Contains("embed", exception.Message);
    }

    [Fact]
    public async Task HolderRejectsCorpusOfAnotherCollection()
    {
        var holder = NewHolder();
        await new Bm25IndexRebuilder(holder).RebuildAsync(Request(changed: true, collection: "other"));

        var inMemory = Assert.Throws<InvalidOperationException>(() => holder.GetOrLoad("startrek"));
        var fromDisk = Assert.Throws<InvalidOperationException>(() => NewHolder().GetOrLoad("startrek"));

        Assert.All([inMemory, fromDisk], exception =>
        {
            Assert.Contains("other", exception.Message);
            Assert.Contains("embed", exception.Message);
        });
    }

    [Fact]
    public async Task HolderUsesConfiguredParametersForLoadedIndex()
    {
        await new Bm25IndexRebuilder(NewHolder()).RebuildAsync(Request(changed: true));
        var parameters = new Bm25Parameters(K1: 1.2, B: 0.5);

        var index = new Bm25IndexHolder(Store, parameters).GetOrLoad("startrek");

        Assert.Equal(parameters, index.Parameters);
    }

    [Fact]
    public async Task DuplicateChunkIdsAreRejected()
    {
        var request = new Bm25RebuildRequest(
            "startrek",
            [new Bm25Chunk("a:1", "x"), new Bm25Chunk("a:1", "y")],
            CollectionChanged: true);

        await Assert.ThrowsAsync<ArgumentException>(() => new Bm25IndexRebuilder(NewHolder()).RebuildAsync(request));
        Assert.False(Store.Exists);
    }

    private Bm25IndexHolder NewHolder() => new(Store, Bm25Parameters.Default);

    private static Bm25RebuildRequest Request(bool changed, string collection = "startrek") =>
        new(collection, Bm25CorpusStoreTests.SampleChunks(), changed);

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
