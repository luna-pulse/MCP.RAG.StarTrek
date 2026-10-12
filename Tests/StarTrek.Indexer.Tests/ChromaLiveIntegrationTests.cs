using Microsoft.Extensions.Options;
using StarTrek.Indexer.Bm25;
using StarTrek.Indexer.Chroma;
using StarTrek.Indexer.Chunking;
using StarTrek.Indexer.Commands;
using StarTrek.Indexer.Configuration;
using StarTrek.Indexer.Search;

namespace StarTrek.Indexer.Tests;

public sealed class ChromaLiveIntegrationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "startrek-live-" + Guid.NewGuid().ToString("N"));
    private readonly ChromaOptions chromaOptions = new()
    {
        Endpoint = ChromaServerFactAttribute.Endpoint,
        Collection = "startrek_test_" + Guid.NewGuid().ToString("N")
    };
    private readonly HttpClient httpClient = new();
    private readonly RepositoryPaths paths;
    private readonly ChunkStagingStore store;

    public ChromaLiveIntegrationTests()
    {
        paths = new RepositoryPaths(
            root,
            Path.Combine(root, "SourceFiles"),
            Path.Combine(root, "Data", "staging"),
            Path.Combine(root, "Models", "multilingual-e5-small"),
            Path.Combine(root, "Data", "bm25", "corpus.json"));
        store = new ChunkStagingStore(paths.Staging);
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(paths.EmbeddingModel);
        File.WriteAllText(paths.EmbeddingModelFile, "fake onnx");
        File.WriteAllText(paths.TokenizerFile, "{}");
        return Task.CompletedTask;
    }

    [ChromaServerFact]
    public async Task EmbedWritesSkipsAndReplacesInRealChroma()
    {
        var chroma = new ChromaHttpClient(httpClient, chromaOptions);
        var oldRace = StagedDocuments.Race(chunkCount: 3, version: "v1");
        store.Write(oldRace);
        store.Write(StagedDocuments.Planet());

        var first = await RunAsync(chroma);
        var collection = await chroma.GetOrCreateCollectionAsync(chromaOptions.Collection, EmbedCommand.DistanceSpace);
        var stored = await chroma.GetAllRecordsAsync(collection.Id);

        Assert.Contains("Записано чанков: 4", first);
        Assert.Equal(384, collection.Dimension);
        Assert.Equal(4, stored.Count);
        Assert.All(stored, record =>
        {
            Assert.Matches("^[0-9a-f]{64}:[0-9]+$", record.Id);
            Assert.All(record.Metadata.Values, value => Assert.True(value is string or long));
        });

        var second = await RunAsync(chroma);
        Assert.Contains("Пропущено файлов (тот же source_file и content_hash): 2", second);
        Assert.Contains("Записано чанков: 0", second);

        var newRace = StagedDocuments.Race(chunkCount: 2, version: "v2");
        store.Write(newRace);
        var third = await RunAsync(chroma);
        stored = await chroma.GetAllRecordsAsync(collection.Id);

        Assert.Contains("Заменено записей из-за смены content_hash: 3", third);
        Assert.Contains("Удалено устаревших id: 1", third);
        Assert.Equal(3, stored.Count);
        Assert.DoesNotContain(stored, record => record.Id == $"{oldRace.DocumentId}:3");
        Assert.All(
            stored.Where(record => record.GetString(ChromaMetadataKeys.DocumentId) == newRace.DocumentId),
            record => Assert.Equal(newRace.ContentHash, record.GetString(ChromaMetadataKeys.ContentHash)));
    }

    [ChromaServerFact]
    public async Task HybridSearchQueriesRealChromaAndFusesWithBm25()
    {
        var chroma = new ChromaHttpClient(httpClient, chromaOptions);
        var race = StagedDocuments.Race(chunkCount: 2);
        var planet = StagedDocuments.Planet();
        store.Write(race);
        store.Write(planet);
        var holder = new Bm25IndexHolder(new Bm25CorpusStore(paths.Bm25Corpus), Bm25Parameters.Default);
        var command = new EmbedCommand(
            paths,
            Options.Create(new IndexerOptions { Chroma = chromaOptions }),
            chroma,
            () => new FakeEmbeddingGenerator(),
            new Bm25IndexRebuilder(holder));
        using var output = new StringWriter();
        Assert.Equal(0, await command.ExecuteAsync(output: output));

        var embedder = new FakeQueryEmbedder();
        var planetChunk = planet.Chunks[0];
        embedder.Map("теллар", ChromaChunkRecords.CreateDocumentText(planetChunk));
        var service = new HybridSearchService(
            chroma,
            embedder,
            holder,
            Options.Create(new IndexerOptions
            {
                Chroma = chromaOptions,
                Search = new SearchOptions { VectorTopN = 2, Bm25TopN = 5, ResultCount = 5 }
            }));

        var result = await service.SearchAsync("теллар");

        var planetId = ChromaChunkRecords.CreateId(planet.DocumentId, planetChunk.ChunkIndex);
        Assert.Equal(planetId, result.Hits[0].Id);
        Assert.Equal(1, result.Hits[0].VectorRank);
        Assert.Equal(1, result.Hits[0].Bm25Rank);
        Assert.Equal(0, result.Hits[0].VectorDistance!.Value, precision: 4);
        Assert.Equal(2, result.VectorCandidates);
        Assert.All(result.Hits, hit => Assert.False(string.IsNullOrEmpty(hit.Document)));
        Assert.All(result.Hits, hit => Assert.Equal(
            hit.Id.Split(':')[0],
            hit.Metadata[ChromaMetadataKeys.DocumentId]));
    }

    [ChromaServerFact]
    public async Task Bm25CorpusKeepsRecordsWhoseStagingFileWasRemoved()
    {
        var chroma = new ChromaHttpClient(httpClient, chromaOptions);
        var race = StagedDocuments.Race(chunkCount: 2);
        var planet = StagedDocuments.Planet();
        store.Write(race);
        store.Write(planet);
        var corpusStore = new Bm25CorpusStore(paths.Bm25Corpus);
        await RunAsync(chroma, new Bm25IndexRebuilder(new Bm25IndexHolder(corpusStore, Bm25Parameters.Default)));
        File.Delete(store.GetPath(planet.DocumentId));

        var holder = new Bm25IndexHolder(corpusStore, Bm25Parameters.Default);
        var output = await RunAsync(chroma, new Bm25IndexRebuilder(holder));

        var planetId = ChromaChunkRecords.CreateId(planet.DocumentId, planet.Chunks[0].ChunkIndex);
        Assert.Contains("Карточек в коллекции без файла подготовки (не тронуты): 1, записей: 1", output);
        Assert.Contains("Индекс BM25 загружен с диска (корпус актуален): чанков 3", output);
        Assert.Contains(planetId, corpusStore.Load().Chunks.Select(chunk => chunk.Id));
        Assert.Equal(planetId, holder.Current!.Search("теллар", 5)[0].Id);
    }

    public async Task DisposeAsync()
    {
        if (ChromaServerFactAttribute.IsAvailable)
        {
            using var _ = await httpClient.DeleteAsync(
                $"{ChromaServerFactAttribute.Endpoint}/api/v2/tenants/default_tenant/databases/default_database/" +
                $"collections/{chromaOptions.Collection}");
        }

        httpClient.Dispose();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private async Task<string> RunAsync(ChromaHttpClient chroma, IBm25IndexRebuilder? rebuilder = null)
    {
        var command = new EmbedCommand(
            paths,
            Options.Create(new IndexerOptions { Chroma = chromaOptions }),
            chroma,
            () => new FakeEmbeddingGenerator(),
            rebuilder ?? new RecordingBm25IndexRebuilder());
        using var output = new StringWriter();
        Assert.Equal(0, await command.ExecuteAsync(output: output));
        return output.ToString();
    }
}

public sealed class ChromaServerFactAttribute : FactAttribute
{
    public const string Endpoint = "http://127.0.0.1:8000";

    private static readonly Lazy<bool> Availability = new(() =>
    {
        try
        {
            using var httpClient = new HttpClient();
            new ChromaHttpClient(httpClient, new ChromaOptions { Endpoint = Endpoint, Collection = "probe" })
                .EnsureAvailableAsync()
                .GetAwaiter()
                .GetResult();
            return true;
        }
        catch (ChromaUnavailableException)
        {
            return false;
        }
    });

    public ChromaServerFactAttribute()
    {
        if (!IsAvailable)
        {
            Skip = $"Интеграционный тест ChromaDB пропущен: {Endpoint} не отвечает (docker compose up -d).";
        }
    }

    public static bool IsAvailable => Availability.Value;
}
