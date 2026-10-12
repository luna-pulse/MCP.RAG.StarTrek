using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using StarTrek.Indexer.Bm25;
using StarTrek.Indexer.Chroma;
using StarTrek.Indexer.Configuration;
using StarTrek.Indexer.Embeddings;
using StarTrek.Indexer.Search;

namespace StarTrek.Indexer.Tests;

public sealed class ReciprocalRankFusionTests
{
    [Fact]
    public void FusesTwoFixedListsWithKSixty()
    {
        IReadOnlyList<string> vector = ["a", "b", "c", "d"];
        IReadOnlyList<string> bm25 = ["c", "e", "a"];

        var fused = ReciprocalRankFusion.Fuse(vector, bm25, k: 60);

        // a: 1/61 + 1/63; c: 1/63 + 1/61 (равны, выигрывает лучший ранг у a); b: 1/62; e: 1/62; d: 1/64.
        Assert.Equal(["a", "c", "b", "e", "d"], fused.Select(entry => entry.Id));
        Assert.Equal((1.0 / 61) + (1.0 / 63), fused[0].Score, precision: 12);
        Assert.Equal((1.0 / 61) + (1.0 / 63), fused[1].Score, precision: 12);
        Assert.Equal(1.0 / 62, fused[2].Score, precision: 12);
        Assert.Equal(1.0 / 64, fused[4].Score, precision: 12);
        Assert.Equal([1, 3], fused[0].Ranks);
        Assert.Equal([3, 1], fused[1].Ranks);
        Assert.Equal([2, null], fused[2].Ranks);
        Assert.Equal([null, 2], fused[3].Ranks);
    }

    [Fact]
    public void ItemInBothListsBeatsTopItemOfOneList()
    {
        var fused = ReciprocalRankFusion.Fuse(["x", "shared"], ["y", "shared"]);

        Assert.Equal("shared", fused[0].Id);
    }

    [Fact]
    public void KChangesScoresAndLimitCutsTail()
    {
        var small = ReciprocalRankFusion.Fuse(["a"], ["b"], k: 1, limit: 1);
        var large = ReciprocalRankFusion.Fuse(["a"], ["b"], k: 100);

        Assert.Single(small);
        Assert.Equal(1.0 / 2, small[0].Score, precision: 12);
        Assert.Equal(1.0 / 101, large[0].Score, precision: 12);
        Assert.Equal(["a", "b"], large.Select(entry => entry.Id));
    }

    [Fact]
    public void EmptyListsAndDuplicatesAreHandled()
    {
        Assert.Empty(ReciprocalRankFusion.Fuse([], []));
        Assert.Equal(["a", "b"], ReciprocalRankFusion.Fuse(["a", "b"], []).Select(entry => entry.Id));

        var duplicated = ReciprocalRankFusion.Fuse(["a", "a"], []);
        Assert.Equal(1.0 / 61, Assert.Single(duplicated).Score, precision: 12);
    }

    [Fact]
    public void SupportsMoreThanTwoLists()
    {
        var fused = ReciprocalRankFusion.Fuse([["a", "b"], ["b"], ["b", "a"]]);

        Assert.Equal("b", fused[0].Id);
        Assert.Equal([2, 1, 1], fused[0].Ranks);
    }

    [Fact]
    public void InvalidArgumentsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReciprocalRankFusion.Fuse(["a"], ["b"], k: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReciprocalRankFusion.Fuse(["a"], ["b"], limit: 0));
    }
}

public sealed class HybridSearchServiceTests : IDisposable
{
    private const string Collection = "startrek";
    private const string EnterpriseText = "Звездолёт Энтерпрайз NCC-1701-D, капитан Пикар";
    private const string VulcanText = "Планета Вулкан, родина Спока и логики";
    private const string VoyagerText = "Звездолёт Вояджер NCC-74656, капитан Джейнвей";

    private readonly string root = Path.Combine(Path.GetTempPath(), "startrek-search-" + Guid.NewGuid().ToString("N"));
    private readonly FakeChromaServer server = new();
    private readonly FakeQueryEmbedder embedder = new();

    public HybridSearchServiceTests()
    {
        var collection = server.AddCollection(Collection, dimension: 384);
        foreach (var (id, text) in Chunks())
        {
            collection.Records[id] = new FakeChromaRecord(
                id,
                text,
                FakeEmbeddingGenerator.CreateVector(text),
                new JsonObject { ["source_file"] = $"SourceFiles/{id}.txt", ["chunk_index"] = 1 });
        }
    }

    [Fact]
    public async Task EmbedsQueryTakesBothTopNListsAndFusesWithRrf()
    {
        var service = await CreateServiceAsync();
        embedder.Map("ncc-74656", VulcanText);

        var result = await service.SearchAsync("ncc-74656");

        Assert.Equal(["ncc-74656"], embedder.Queries);
        Assert.Equal(3, result.VectorCandidates);
        Assert.Equal(1, result.Bm25Candidates);
        Assert.Equal(3, result.Hits.Count);

        var best = result.Hits[0];
        Assert.Equal("voyager:1", best.Id);
        Assert.Equal(1, best.Bm25Rank);
        Assert.NotNull(best.Bm25Score);
        Assert.InRange(best.VectorRank!.Value, 2, 3);
        Assert.Equal((1.0 / 61) + (1.0 / (60 + best.VectorRank.Value)), best.Score, precision: 12);
        Assert.Equal(VoyagerText, best.Document);

        var vector = result.Hits.Single(hit => hit.Id == "vulcan:1");
        Assert.Equal(1, vector.VectorRank);
        Assert.Null(vector.Bm25Rank);
        Assert.Equal(1.0 / 61, vector.Score, precision: 12);
        Assert.Equal(0, vector.VectorDistance!.Value, precision: 5);
    }

    [Fact]
    public async Task Bm25OnlyHitIsReadFromChromaByIdWithDocumentAndMetadata()
    {
        var service = await CreateServiceAsync(search: new SearchOptions { VectorTopN = 1, Bm25TopN = 5, ResultCount = 5 });
        embedder.Map("ncc-74656", VulcanText);

        var result = await service.SearchAsync("ncc-74656");

        Assert.Equal(["voyager:1", "vulcan:1"], result.Hits.Select(hit => hit.Id));
        var lexicalOnly = result.Hits.Single(hit => hit.Id == "voyager:1");
        Assert.Null(lexicalOnly.VectorRank);
        Assert.Equal(VoyagerText, lexicalOnly.Document);
        Assert.Equal("SourceFiles/voyager:1.txt", lexicalOnly.Metadata["source_file"]);
        Assert.Contains(
            server.Requests,
            request => request.Path.EndsWith("/get", StringComparison.Ordinal) &&
                JsonNode.Parse(request.Body)!["ids"] is JsonArray);
    }

    [Fact]
    public async Task ResultCountAndRrfConstantComeFromOptionsAndArgument()
    {
        var service = await CreateServiceAsync(search: new SearchOptions { RrfK = 10, ResultCount = 2 });

        var result = await service.SearchAsync("капитан звездолёт");
        var one = await service.SearchAsync("капитан звездолёт", resultCount: 1);

        Assert.Equal(2, result.Hits.Count);
        Assert.Single(one.Hits);
        Assert.All(result.Hits, hit => Assert.True(hit.Score > 1.0 / 71));
    }

    [Fact]
    public async Task ChunkMissingFromCollectionIsDroppedFromResults()
    {
        var service = await CreateServiceAsync();
        server.Collections[Collection].Records.Remove("voyager:1");

        var result = await service.SearchAsync("ncc-74656");

        Assert.DoesNotContain(result.Hits, hit => hit.Id == "voyager:1");
    }

    [Fact]
    public async Task MissingCollectionExplainsToRunEmbed()
    {
        var service = await CreateServiceAsync(collectionName: "absent");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchAsync("ncc"));

        Assert.Contains("absent", exception.Message);
        Assert.Contains("ChromaDB", exception.Message);
        Assert.Contains("embed", exception.Message);
        Assert.Empty(embedder.Queries);
    }

    [Fact]
    public async Task MissingBm25CorpusExplainsToRunEmbed()
    {
        var service = Create(new Bm25IndexHolder(new Bm25CorpusStore(Path.Combine(root, "none.json")), Bm25Parameters.Default));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchAsync("ncc"));

        Assert.Contains("embed", exception.Message);
    }

    [Fact]
    public async Task BlankQueryIsRejected()
    {
        var service = await CreateServiceAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync("  "));
    }

    [Fact]
    public async Task PunctuationOnlyQueryStillUsesVectorList()
    {
        var service = await CreateServiceAsync();

        var result = await service.SearchAsync("???");

        Assert.Equal(0, result.Bm25Candidates);
        Assert.Equal(3, result.Hits.Count);
        Assert.All(result.Hits, hit => Assert.Null(hit.Bm25Rank));
    }

    private static IEnumerable<(string Id, string Text)> Chunks() =>
    [
        ("enterprise:1", EnterpriseText),
        ("vulcan:1", VulcanText),
        ("voyager:1", VoyagerText)
    ];

    private async Task<HybridSearchService> CreateServiceAsync(
        SearchOptions? search = null,
        string collectionName = Collection)
    {
        var holder = new Bm25IndexHolder(
            new Bm25CorpusStore(Path.Combine(root, "corpus.json")),
            Bm25Parameters.Default);
        await new Bm25IndexRebuilder(holder).RebuildAsync(new Bm25RebuildRequest(
            collectionName,
            Chunks().Select(chunk => new Bm25Chunk(chunk.Id, chunk.Text)).ToList(),
            CollectionChanged: true));
        return Create(holder, search, collectionName);
    }

    private HybridSearchService Create(
        Bm25IndexHolder holder,
        SearchOptions? search = null,
        string collectionName = Collection)
    {
        var chromaOptions = new ChromaOptions { Endpoint = "http://127.0.0.1:8000", Collection = collectionName };
        var httpClient = new HttpClient(server, disposeHandler: false);
        return new HybridSearchService(
            new ChromaHttpClient(httpClient, chromaOptions),
            embedder,
            holder,
            Options.Create(new IndexerOptions { Chroma = chromaOptions, Search = search ?? new SearchOptions() }));
    }

    public void Dispose()
    {
        server.Dispose();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
