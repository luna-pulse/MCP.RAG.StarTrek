using System.Text.Json;
using System.Text.Json.Nodes;
using StarTrek.Indexer.Chroma;
using StarTrek.Indexer.Search;

namespace StarTrek.Mcp.Tests;

public sealed class SearchChunkMapperTests
{
    [Fact]
    public void OmitsMissingMetadataKey()
    {
        var metadata = new Dictionary<string, object?>
        {
            [ChromaMetadataKeys.SourceFile] = "SourceFiles/Races/Вулканцы.txt",
            [ChromaMetadataKeys.Title] = "Вулканцы",
            [ChromaMetadataKeys.EntityType] = "race",
            [ChromaMetadataKeys.SectionPath] = "Физиология > Мозг",
            [ChromaMetadataKeys.Position] = "lines 12-40",
            [ChromaMetadataKeys.ChunkIndex] = 3L
        };
        var chunk = SearchChunkMapper.FromDocument(
            "races:3",
            "Энтерпрайз без решёток",
            metadata,
            bm25Score: 1.25);
        var json = JsonNode.Parse(JsonSerializer.Serialize(chunk))!.AsObject();

        Assert.Equal("races:3", json["id"]!.GetValue<string>());
        Assert.Equal("Энтерпрайз без решёток", json["text"]!.GetValue<string>());
        Assert.Equal(1.25, json["bm25Score"]!.GetValue<double>());
        Assert.Equal("lines 12-40", json["position"]!.GetValue<string>());
        Assert.Equal(3, json["chunkIndex"]!.GetValue<int>());
        Assert.False(json.ContainsKey("originalTitle"));
        Assert.False(json.ContainsKey("distance"));
        Assert.False(json.ContainsKey("rrfScore"));
        Assert.False(json.ContainsKey("vectorRank"));
        Assert.False(json.ContainsKey("bm25Rank"));
        Assert.DoesNotContain("#", json["text"]!.GetValue<string>());
        Assert.False(json["text"]!.GetValue<string>().StartsWith("passage: ", StringComparison.Ordinal));
    }

    [Fact]
    public void KeepsPresentEmptyMetadataString()
    {
        var metadata = new Dictionary<string, object?>
        {
            [ChromaMetadataKeys.OriginalTitle] = ""
        };
        var json = JsonNode.Parse(JsonSerializer.Serialize(
            SearchChunkMapper.FromDocument("doc:0", "текст", metadata)))!.AsObject();
        Assert.Equal("", json["originalTitle"]!.GetValue<string>());
    }

    [Fact]
    public void MapsHybridHitScores()
    {
        var hit = new HybridSearchHit(
            "doc:1", 0.5, 2, 0.25, null, null, "чанк",
            new Dictionary<string, object?> { [ChromaMetadataKeys.Title] = "Заголовок" });
        var json = JsonNode.Parse(JsonSerializer.Serialize(SearchChunkMapper.FromHybridHit(hit)))!.AsObject();
        Assert.Equal(0.5, json["rrfScore"]!.GetValue<double>());
        Assert.Equal(0.25, json["distance"]!.GetValue<double>());
        Assert.Equal(2, json["vectorRank"]!.GetValue<int>());
        Assert.False(json.ContainsKey("bm25Score"));
        Assert.False(json.ContainsKey("bm25Rank"));
        Assert.Equal("Заголовок", json["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task ScriptedQueryKeepsSeedOrder()
    {
        var handler = new ScriptedChromaHandler();
        handler.AddCollection("startrek");
        handler.Seed("startrek", "far:0", "дальний", null, 0.4);
        handler.Seed("startrek", "near:0", "ближний", null, 0.1);
        var client = handler.CreateClient();
        var collection = await client.TryGetCollectionAsync("startrek");
        var hits = await client.QueryAsync(collection!.Id, new float[384], 2);
        Assert.Equal(["far:0", "near:0"], hits.Select(hit => hit.Id).ToArray());
        Assert.Equal(0.4, hits[0].Distance);
    }
}
