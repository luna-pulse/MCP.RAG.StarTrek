using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using StarTrek.Indexer.Bm25;
using StarTrek.Indexer.Chroma;
using StarTrek.Indexer.Search;
using StarTrek.Indexer.Tokenization;

namespace StarTrek.Mcp.Tests;

public sealed class SemanticSearchServiceTests
{
    private readonly ScriptedChromaHandler handler = new();
    private readonly RecordingQueryEmbedder embedder = new();
    private readonly SemanticSearchService service;

    public SemanticSearchServiceTests()
    {
        service = new SemanticSearchService(
            handler.CreateClient(),
            embedder,
            Options.Create(TestIndexerOptions.Create(
                Path.Combine(Path.GetTempPath(), "missing-semantic.json"))));
    }

    [Fact]
    public async Task SendsRawQueryAndKeepsChromaOrder()
    {
        handler.AddCollection("startrek");
        handler.Seed("startrek", "far:0", "дальний чанк", Title("Дальний"), 0.4);
        handler.Seed("startrek", "near:0", "ближний чанк", Title("Ближний"), 0.1);
        var result = await service.SearchAsync("вулканская логика", 2);

        Assert.Equal("вулканская логика", Assert.Single(embedder.Queries));
        Assert.False(embedder.Queries[0].StartsWith("passage: ", StringComparison.Ordinal));
        Assert.False(embedder.Queries[0].StartsWith(E5Tokenizer.QueryPrefix, StringComparison.Ordinal));
        Assert.Equal(["far:0", "near:0"], result.Chunks.Select(chunk => chunk.Id).ToArray());
        Assert.Equal(0.4, result.Chunks[0].Distance);
        Assert.Equal(384, RecordedQueryLength(handler));
        Assert.Equal(2, RecordedResultCount(handler));
        Assert.DoesNotContain(
            handler.Requests,
            request => request.Body.Contains("get_or_create", StringComparison.Ordinal));
    }

    [Fact]
    public void DoesNotDependOnBm25()
    {
        var parameters = typeof(SemanticSearchService).GetConstructors().Single().GetParameters();
        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(Bm25IndexHolder));
        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(HybridSearchService));
    }

    [Fact]
    public async Task MissingCollectionNamesEmbed()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchAsync("логика", 2));
        Assert.Equal(SearchFailures.CollectionMissing("startrek").Message, error.Message);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task UnavailableChromaNamesTheAddress()
    {
        handler.Available = false;
        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => service.SearchAsync("логика", 2));
        Assert.Contains("ChromaDB не отвечает по адресу", error.Message);
    }

    [Fact]
    public async Task SuccessJsonHasDistanceWithoutBm25Score()
    {
        handler.AddCollection("startrek");
        handler.Seed("startrek", "far:0", "дальний чанк", Title("Дальний"), 0.4);
        var result = await service.SearchAsync("фраза", 1);
        var json = JsonNode.Parse(JsonSerializer.Serialize(Assert.Single(result.Chunks)))!.AsObject();
        Assert.True(json.ContainsKey("distance"));
        Assert.False(json.ContainsKey("bm25Score"));
    }

    private static Dictionary<string, object?> Title(string title) =>
        new() { [ChromaMetadataKeys.Title] = title };

    private static int RecordedQueryLength(ScriptedChromaHandler chroma)
    {
        var query = chroma.Requests.Single(request => request.Path.EndsWith("/query", StringComparison.Ordinal));
        return JsonNode.Parse(query.Body)!["query_embeddings"]!.AsArray()[0]!.AsArray().Count;
    }

    private static int RecordedResultCount(ScriptedChromaHandler chroma)
    {
        var query = chroma.Requests.Single(request => request.Path.EndsWith("/query", StringComparison.Ordinal));
        return JsonNode.Parse(query.Body)!["n_results"]!.GetValue<int>();
    }
}
