using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using StarTrek.Indexer.Bm25;
using StarTrek.Indexer.Search;

namespace StarTrek.Mcp.Tests;

public sealed class LexicalSearchServiceTests
{
    private readonly ScriptedChromaHandler handler;
    private readonly LexicalSearchService service;

    public LexicalSearchServiceTests()
    {
        handler = new ScriptedChromaHandler();
        handler.AddCollection("startrek");
        handler.Seed("startrek", "ships:1", "Энтерпрайз", null, 0.2);
        handler.Seed("startrek", "ships:0", "старый корабль", null, 0.2);
        handler.Seed("startrek", "races:1", "вулканцы телепаты", null, 0.2);
        handler.Seed("startrek", "races:2", "вулканцы", null, 0.2);
        handler.Seed("startrek", "names:1", "Таль Шанар", null, 0.2);
        handler.Seed("startrek", "b:0", "Спок", null, 0.2);
        handler.Seed("startrek", "a:0", "Спок", null, 0.2);

        var path = UnusedCorpusPath();
        var holder = new Bm25IndexHolder(new Bm25CorpusStore(path), new Bm25Parameters(1.5, 0.75));
        holder.Build(SampleCorpus());
        service = CreateService(holder, handler, path);
    }

    [Fact]
    public async Task RanksRussianPhraseAndRegistryCode()
    {
        var phrase = await service.SearchAsync("Вулканцы телепаты", 10);
        Assert.Equal(["races:1", "races:2"], phrase.Chunks.Select(chunk => chunk.Id).ToArray());
        var code = await service.SearchAsync("NCC-1701-D", 10);
        Assert.Equal("ships:1", Assert.Single(code.Chunks).Id);
        Assert.Equal("Энтерпрайз", code.Chunks[0].Text);
    }

    [Fact]
    public async Task SplitsHyphenatedWordWithoutDigits()
    {
        var result = await service.SearchAsync("Таль-Шанар", 10);
        Assert.Equal("names:1", Assert.Single(result.Chunks).Id);
    }

    [Fact]
    public async Task TiesBreakById()
    {
        var result = await service.SearchAsync("Спок", 10);
        Assert.Equal(["a:0", "b:0"], result.Chunks.Select(chunk => chunk.Id).ToArray());
    }

    [Fact]
    public async Task EmptyMatchIsSuccess()
    {
        var result = await service.SearchAsync("ксикс", 10);
        Assert.Empty(result.Chunks);
    }

    [Fact]
    public async Task RejectsQueryWithNoTokens()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchAsync("???", 10));
        Assert.Equal(LexicalSearchService.NoTokensMessage, error.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task OmitsBm25HitMissingFromChroma()
    {
        var localHandler = new ScriptedChromaHandler();
        localHandler.AddCollection("startrek");
        localHandler.Seed("startrek", "races:2", "вулканцы", null, 0.2);
        var path = UnusedCorpusPath();
        var holder = new Bm25IndexHolder(new Bm25CorpusStore(path), new Bm25Parameters(1.5, 0.75));
        holder.Build(new Bm25Corpus(
            Bm25Corpus.CurrentFormatVersion,
            Bm25Tokenizer.Version,
            "startrek",
            "fingerprint",
            [
                new Bm25CorpusChunk("missing:1", ["вулканцы"]),
                new Bm25CorpusChunk("races:2", ["вулканцы"])
            ]));
        var local = CreateService(holder, localHandler, path);

        var result = await local.SearchAsync("вулканцы", 10);

        Assert.Equal("races:2", Assert.Single(result.Chunks).Id);
        Assert.DoesNotContain(
            localHandler.Requests,
            request => request.Path.EndsWith("/upsert", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingCorpusNamesEmbed()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "corpus.json");
        var holder = new Bm25IndexHolder(new Bm25CorpusStore(path), new Bm25Parameters(1.5, 0.75));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LexicalSearchService(holder, handler.CreateClient(), Options.Create(TestIndexerOptions.Create(path)))
                .SearchAsync("вулканцы", 10));
        Assert.Contains("Корпус BM25 не найден", error.Message);
        Assert.Contains("embed", error.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task OldCorpusFormatNamesEmbed()
    {
        var path = await SaveCorpusAsync(new Bm25Corpus(
            0,
            Bm25Tokenizer.Version,
            "startrek",
            "fingerprint",
            [new Bm25CorpusChunk("races:2", ["вулканцы"])]));
        var error = await SearchMissingDiskCorpusAsync(path);
        Assert.Contains("собран по старым правилам", error.Message);
        Assert.Contains("embed", error.Message);
    }

    [Fact]
    public async Task ForeignCollectionNamesEmbed()
    {
        var path = await SaveCorpusAsync(new Bm25Corpus(
            Bm25Corpus.CurrentFormatVersion,
            Bm25Tokenizer.Version,
            "other",
            "fingerprint",
            [new Bm25CorpusChunk("races:2", ["вулканцы"])]));
        var error = await SearchMissingDiskCorpusAsync(path);
        Assert.Contains("other", error.Message);
        Assert.Contains("startrek", error.Message);
        Assert.Contains("embed", error.Message);
    }

    [Fact]
    public async Task MissingCollectionNamesEmbed()
    {
        var localHandler = new ScriptedChromaHandler();
        var path = UnusedCorpusPath();
        var holder = new Bm25IndexHolder(new Bm25CorpusStore(path), new Bm25Parameters(1.5, 0.75));
        holder.Build(SampleCorpus());
        var local = CreateService(holder, localHandler, path);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => local.SearchAsync("вулканцы", 10));

        Assert.Equal(
            "Коллекция startrek не найдена в ChromaDB. Сначала выполните команду embed.",
            error.Message);
        Assert.DoesNotContain(localHandler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task UnavailableChromaNamesTheAddress()
    {
        handler.Available = false;
        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => service.SearchAsync("вулканцы", 10));
        Assert.Contains("ChromaDB не отвечает по адресу", error.Message);
    }

    [Fact]
    public async Task SuccessJsonHasBm25ScoreWithoutDistance()
    {
        var result = await service.SearchAsync("NCC-1701-D", 10);
        var json = JsonNode.Parse(JsonSerializer.Serialize(Assert.Single(result.Chunks)))!.AsObject();
        Assert.True(json.ContainsKey("bm25Score"));
        Assert.False(json.ContainsKey("distance"));
    }

    private static Bm25Corpus SampleCorpus() => new(
        Bm25Corpus.CurrentFormatVersion,
        Bm25Tokenizer.Version,
        "startrek",
        "fingerprint",
        [
            new Bm25CorpusChunk("ships:1", ["ncc-1701-d"]),
            new Bm25CorpusChunk("ships:0", ["ncc-1701"]),
            new Bm25CorpusChunk("races:1", ["вулканцы", "телепаты"]),
            new Bm25CorpusChunk("races:2", ["вулканцы"]),
            new Bm25CorpusChunk("names:1", ["таль", "шанар"]),
            new Bm25CorpusChunk("b:0", ["спок"]),
            new Bm25CorpusChunk("a:0", ["спок"])
        ]);

    private static LexicalSearchService CreateService(
        Bm25IndexHolder holder,
        ScriptedChromaHandler chroma,
        string path) =>
        new(holder, chroma.CreateClient(), Options.Create(TestIndexerOptions.Create(path)));

    private static string UnusedCorpusPath() =>
        Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "corpus.json");

    private static async Task<string> SaveCorpusAsync(Bm25Corpus corpus)
    {
        var path = UnusedCorpusPath();
        new Bm25CorpusStore(path).Save(corpus);
        return await Task.FromResult(path);
    }

    private async Task<InvalidOperationException> SearchMissingDiskCorpusAsync(string path)
    {
        var holder = new Bm25IndexHolder(new Bm25CorpusStore(path), new Bm25Parameters(1.5, 0.75));
        return await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LexicalSearchService(holder, handler.CreateClient(), Options.Create(TestIndexerOptions.Create(path)))
                .SearchAsync("вулканцы", 10));
    }
}
