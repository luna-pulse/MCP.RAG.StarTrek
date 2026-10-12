using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using StarTrek.Indexer.Bm25;
using StarTrek.Indexer.Chroma;
using StarTrek.Indexer.Chunking;
using StarTrek.Indexer.Commands;
using StarTrek.Indexer.Configuration;

namespace StarTrek.Indexer.Tests;

public sealed class EmbedCommandTests : IDisposable
{
    private const string Collection = "startrek";

    private readonly string root = Path.Combine(Path.GetTempPath(), "startrek-embed-" + Guid.NewGuid().ToString("N"));
    private readonly RepositoryPaths paths;
    private readonly ChunkStagingStore store;
    private readonly FakeChromaServer server = new();
    private readonly FakeEmbeddingGenerator generator = new();
    private readonly RecordingBm25IndexRebuilder bm25 = new();
    private int generatorResolutions;

    public EmbedCommandTests()
    {
        paths = new RepositoryPaths(
            root,
            Path.Combine(root, "SourceFiles"),
            Path.Combine(root, "Data", "staging"),
            Path.Combine(root, "Models", "multilingual-e5-small"),
            Path.Combine(root, "Data", "bm25", "corpus.json"));
        store = new ChunkStagingStore(paths.Staging);
        Directory.CreateDirectory(paths.EmbeddingModel);
        File.WriteAllText(paths.EmbeddingModelFile, "fake onnx");
        File.WriteAllText(paths.TokenizerFile, "{}");
    }

    [Fact]
    public async Task FirstRunUpsertsEveryChunkWithVectorFlatMetadataAndStableIds()
    {
        var race = StagedDocuments.Race(chunkCount: 2);
        var planet = StagedDocuments.Planet(chunkCount: 1);
        store.Write(race);
        store.Write(planet);

        var output = await RunAsync();

        Assert.Contains("Записано файлов: 2 (новых: 2, со сменой content_hash: 0, перезаписано целиком: 0)", output);
        Assert.Contains("Записано чанков: 3", output);
        Assert.Contains("Пропущено файлов (тот же source_file и content_hash): 0", output);
        Assert.Contains("Записей в коллекции: 3", output);
        var collection = server.Collections[Collection];
        Assert.Equal("cosine", collection.Space);
        Assert.Equal(384, collection.Dimension);
        Assert.Equal(
            new[] { $"{race.DocumentId}:1", $"{race.DocumentId}:2", $"{planet.DocumentId}:1" }.Order(StringComparer.Ordinal),
            collection.Records.Keys);
        foreach (var record in collection.Records.Values)
        {
            Assert.Equal(384, record.Embedding.Length);
            Assert.All(record.Metadata, pair => Assert.Contains(
                pair.Value!.GetValueKind(),
                new[] { JsonValueKind.String, JsonValueKind.Number }));
            Assert.DoesNotContain("#", record.Document);
            Assert.False(record.Document!.StartsWith("passage:", StringComparison.Ordinal));
        }

        var first = collection.Records[$"{race.DocumentId}:1"];
        Assert.Equal(race.Chunks[0].EmbeddingText, first.Document);
        Assert.Equal(FakeEmbeddingGenerator.CreateVector(race.Chunks[0].EmbeddingText), first.Embedding);
        Assert.Equal(race.ContentHash, first.Metadata["content_hash"]!.GetValue<string>());
        Assert.Equal(string.Empty, first.Metadata["original_title"]!.GetValue<string>());
        Assert.Equal("История > Часть 1", first.Metadata["section_path"]!.GetValue<string>());
        Assert.Equal(1, first.Metadata["chunk_index"]!.GetValue<int>());
        Assert.All(generator.Inputs, input => Assert.False(input.StartsWith("passage:", StringComparison.Ordinal)));
        Assert.Equal(1, generatorResolutions);
        var rebuild = Assert.Single(bm25.Requests);
        Assert.True(rebuild.CollectionChanged);
        Assert.Equal(Collection, rebuild.Collection);
        Assert.Equal(collection.Records.Keys, rebuild.Chunks.Select(chunk => chunk.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task SameContentHashSkipsFileWithoutComputingVectorsOrWriting()
    {
        store.Write(StagedDocuments.Race());
        store.Write(StagedDocuments.Planet());
        await RunAsync();
        var snapshot = SnapshotRecords();
        var requestsBefore = server.Requests.Count;

        var output = await RunAsync();

        Assert.Contains("Записано файлов: 0", output);
        Assert.Contains("Записано чанков: 0", output);
        Assert.Contains("Пропущено файлов (тот же source_file и content_hash): 2", output);
        Assert.Contains("Пропущено чанков: 3", output);
        Assert.DoesNotContain(server.Requests.Skip(requestsBefore), request =>
            request.Path.EndsWith("/upsert", StringComparison.Ordinal) ||
            request.Path.EndsWith("/delete", StringComparison.Ordinal));
        Assert.Equal(1, generatorResolutions);
        Assert.Equal(snapshot, SnapshotRecords());
        Assert.Equal(2, bm25.Requests.Count);
        Assert.False(bm25.Requests[1].CollectionChanged);
        Assert.Equal(3, bm25.Requests[1].Chunks.Count);
    }

    [Fact]
    public async Task ChangedContentHashWritesNewRecordsBeforeDeletingOnlyStaleIds()
    {
        var oldRace = StagedDocuments.Race(chunkCount: 3, version: "v1");
        var planet = StagedDocuments.Planet();
        store.Write(oldRace);
        store.Write(planet);
        await RunAsync();
        var requestsBefore = server.Requests.Count;
        var newRace = StagedDocuments.Race(chunkCount: 2, version: "v2");
        store.Write(newRace);

        var output = await RunAsync();

        Assert.Contains("Записано файлов: 1 (новых: 0, со сменой content_hash: 1, перезаписано целиком: 0)", output);
        Assert.Contains("Записано чанков: 2", output);
        Assert.Contains("Пропущено файлов (тот же source_file и content_hash): 1", output);
        Assert.Contains("Заменено записей из-за смены content_hash: 3", output);
        Assert.Contains("Удалено устаревших id: 1", output);
        var newRequests = server.Requests.Skip(requestsBefore).ToList();
        var delete = Assert.Single(newRequests, request => request.Path.EndsWith("/delete", StringComparison.Ordinal));
        Assert.Equal(
            [$"{oldRace.DocumentId}:3"],
            JsonNode.Parse(delete.Body)!["ids"]!.AsArray().Select(id => id!.GetValue<string>()).Order(StringComparer.Ordinal));
        var upsert = Assert.Single(newRequests, request => request.Path.EndsWith("/upsert", StringComparison.Ordinal));
        Assert.True(newRequests.IndexOf(upsert) < newRequests.IndexOf(delete));
        var records = server.Collections[Collection].Records;
        Assert.DoesNotContain($"{oldRace.DocumentId}:3", records.Keys);
        Assert.Equal(3, records.Count);
        Assert.All(
            records.Values.Where(record => record.Id.StartsWith(newRace.DocumentId, StringComparison.Ordinal)),
            record => Assert.Equal(newRace.ContentHash, record.Metadata["content_hash"]!.GetValue<string>()));
        Assert.Equal(planet.ContentHash, records[$"{planet.DocumentId}:1"].Metadata["content_hash"]!.GetValue<string>());
    }

    [Fact]
    public async Task FailedUpsertKeepsExistingCardAndDoesNotRebuildBm25()
    {
        var oldRace = StagedDocuments.Race(chunkCount: 3, version: "v1");
        store.Write(oldRace);
        await RunAsync();
        var snapshot = SnapshotRecords();
        var requestsBefore = server.Requests.Count;
        store.Write(StagedDocuments.Race(chunkCount: 2, version: "v2"));
        server.RejectUpserts = true;

        await Assert.ThrowsAsync<ChromaApiException>(() => RunAsync());

        var newRequests = server.Requests.Skip(requestsBefore).ToList();
        Assert.Single(newRequests, request => request.Path.EndsWith("/upsert", StringComparison.Ordinal));
        Assert.DoesNotContain(newRequests, request => request.Path.EndsWith("/delete", StringComparison.Ordinal));
        Assert.Equal(snapshot, SnapshotRecords());
        Assert.Single(bm25.Requests);
    }

    [Fact]
    public async Task ForceRewritesCardsWithSameContentHash()
    {
        store.Write(StagedDocuments.Race());
        await RunAsync();

        var requestsBefore = server.Requests.Count;
        var output = await RunAsync(force: true);

        Assert.Contains("Записано файлов: 1 (новых: 0, со сменой content_hash: 0, перезаписано целиком: 1)", output);
        Assert.Contains("Перезаписано записей (--force или неполная карточка): 2", output);
        Assert.Contains("Удалено устаревших id: 0", output);
        Assert.DoesNotContain(
            server.Requests.Skip(requestsBefore),
            request => request.Path.EndsWith("/delete", StringComparison.Ordinal));
        Assert.Equal(2, server.Collections[Collection].Records.Count);
    }

    [Fact]
    public async Task DeadChromaStopsBeforeAnyWriteAndKeepsCollection()
    {
        store.Write(StagedDocuments.Race());
        SeedExistingRecord();
        var snapshot = SnapshotRecords();
        server.Available = false;

        var exception = await Assert.ThrowsAsync<ChromaUnavailableException>(() => RunAsync());

        Assert.Contains("http://127.0.0.1:8000/", exception.Message);
        Assert.Contains("docker compose up -d", exception.Message);
        Assert.Contains("не изменена", exception.Message);
        Assert.All(server.Requests, request => Assert.Equal("/api/v2/heartbeat", request.Path));
        Assert.Empty(server.Writes);
        Assert.Equal(0, generatorResolutions);
        Assert.Empty(bm25.Requests);
        Assert.Equal(snapshot, SnapshotRecords());
    }

    [Fact]
    public async Task MissingModelStopsBeforeContactingChroma()
    {
        store.Write(StagedDocuments.Race());
        SeedExistingRecord();
        var snapshot = SnapshotRecords();
        File.Delete(paths.EmbeddingModelFile);

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() => RunAsync());

        Assert.Contains("model.onnx", exception.Message);
        Assert.Empty(server.Requests);
        Assert.Equal(0, generatorResolutions);
        Assert.Empty(bm25.Requests);
        Assert.Equal(snapshot, SnapshotRecords());
    }

    [Fact]
    public async Task EmptyStagingStopsWithoutTouchingCollection()
    {
        SeedExistingRecord();
        var snapshot = SnapshotRecords();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync());

        Assert.Contains("chunk", exception.Message);
        Assert.DoesNotContain(server.Requests, request => request.Method == HttpMethod.Post);
        Assert.Equal(snapshot, SnapshotRecords());
    }

    [Fact]
    public async Task CollectionWithOtherDimensionStopsBeforeWrites()
    {
        store.Write(StagedDocuments.Race());
        server.AddCollection(Collection, dimension: 768);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync());

        Assert.Contains("768", exception.Message);
        Assert.Empty(server.Writes);
        Assert.Equal(0, generatorResolutions);
    }

    [Fact]
    public async Task EmbedProcessWithDeadChromaExitsWithMessage()
    {
        store.Write(StagedDocuments.Race());
        var endpoint = ClosedPort.CreateEndpoint();
        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = TestEnvironment.RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "StarTrek.Indexer.dll"));
        startInfo.ArgumentList.Add("embed");
        startInfo.Environment["STARTREK_Indexer__Paths__Staging"] = paths.Staging;
        startInfo.Environment["STARTREK_Indexer__Paths__EmbeddingModel"] = paths.EmbeddingModel;
        startInfo.Environment["STARTREK_Indexer__Chroma__Endpoint"] = endpoint.ToString();

        using var process = Process.Start(startInfo)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);

        var text = await errorTask + await outputTask;
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("ChromaDB не отвечает", text);
        Assert.Contains(endpoint.ToString(), text);
        Assert.Single(store.EnumerateFiles());
    }

    public void Dispose()
    {
        server.Dispose();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RealBm25HookWritesCorpusThenLoadsItFromDiskWhenNothingChanged()
    {
        var race = StagedDocuments.Race(chunkCount: 2);
        store.Write(race);
        store.Write(StagedDocuments.Planet());
        var rebuilder = new Bm25IndexRebuilder(new Bm25IndexHolder(new Bm25CorpusStore(paths.Bm25Corpus), Bm25Parameters.Default));

        var first = await RunAsync(rebuilder: rebuilder);

        Assert.Contains("Индекс BM25 пересобран и сохранён: чанков 3", first);
        var corpus = new Bm25CorpusStore(paths.Bm25Corpus).Load();
        Assert.Equal(
            server.Collections[Collection].Records.Keys,
            corpus.Chunks.Select(chunk => chunk.Id).Order(StringComparer.Ordinal));
        Assert.All(corpus.Chunks, chunk => Assert.Contains("ncc-1701-d", chunk.Tokens));
        var requestsBefore = server.Requests.Count;

        var holder = new Bm25IndexHolder(new Bm25CorpusStore(paths.Bm25Corpus), Bm25Parameters.Default);
        var second = await RunAsync(rebuilder: new Bm25IndexRebuilder(holder));

        Assert.Contains("Индекс BM25 загружен с диска (корпус актуален): чанков 3", second);
        Assert.Equal(3, holder.Current!.DocumentCount);
        Assert.DoesNotContain(
            server.Requests.Skip(requestsBefore),
            request => request.Path.EndsWith("/upsert", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RealBm25HookRebuildsCorpusWhenStagedChunkChanges()
    {
        store.Write(StagedDocuments.Race(chunkCount: 3, version: "v1"));
        var holder = new Bm25IndexHolder(new Bm25CorpusStore(paths.Bm25Corpus), Bm25Parameters.Default);
        await RunAsync(rebuilder: new Bm25IndexRebuilder(holder));
        Assert.Equal(3, holder.Current!.DocumentCount);

        store.Write(StagedDocuments.Race(chunkCount: 2, version: "v2"));
        var output = await RunAsync(rebuilder: new Bm25IndexRebuilder(holder));

        Assert.Contains("Индекс BM25 пересобран и сохранён: чанков 2", output);
        Assert.Equal(2, new Bm25CorpusStore(paths.Bm25Corpus).Load().Chunks.Count);
        Assert.Equal(2, holder.Current.DocumentCount);
    }

    [Fact]
    public async Task HookReceivesFullChunkListWithTextAndIdsOnEveryRun()
    {
        var race = StagedDocuments.Race(chunkCount: 2);
        store.Write(race);

        await RunAsync();

        var request = Assert.Single(bm25.Requests);
        Assert.Equal(
            [$"{race.DocumentId}:1", $"{race.DocumentId}:2"],
            request.Chunks.Select(chunk => chunk.Id));
        Assert.Equal(race.Chunks[0].EmbeddingText, request.Chunks[0].Text);
        Assert.DoesNotContain(
            server.Requests,
            request => request.Path.EndsWith("/get", StringComparison.Ordinal) &&
                JsonNode.Parse(request.Body)!["ids"] is JsonArray);
    }

    [Fact]
    public async Task UnstagedRecordsLeftInCollectionStayInBm25Corpus()
    {
        var race = StagedDocuments.Race(chunkCount: 2);
        store.Write(race);
        SeedExistingRecord();
        var holder = new Bm25IndexHolder(new Bm25CorpusStore(paths.Bm25Corpus), Bm25Parameters.Default);

        var first = await RunAsync(rebuilder: new Bm25IndexRebuilder(holder));
        var second = await RunAsync(
            rebuilder: new Bm25IndexRebuilder(
                new Bm25IndexHolder(new Bm25CorpusStore(paths.Bm25Corpus), Bm25Parameters.Default)));

        Assert.Contains("Карточек в коллекции без файла подготовки (не тронуты): 1, записей: 1", first);
        Assert.Contains("Индекс BM25 пересобран и сохранён: чанков 3", first);
        Assert.Contains("Индекс BM25 загружен с диска (корпус актуален): чанков 3", second);
        Assert.Equal(
            server.Collections[Collection].Records.Keys,
            new Bm25CorpusStore(paths.Bm25Corpus).Load().Chunks.Select(chunk => chunk.Id).Order(StringComparer.Ordinal));
        Assert.Equal(["existing:1"], holder.Current!.Search("лежащая запись", 5).Select(hit => hit.Id));
    }

    private async Task<string> RunAsync(bool force = false, IBm25IndexRebuilder? rebuilder = null)
    {
        var chromaOptions = new ChromaOptions { Endpoint = "http://127.0.0.1:8000", Collection = Collection };
        using var httpClient = new HttpClient(server, disposeHandler: false);
        var command = new EmbedCommand(
            paths,
            Options.Create(new IndexerOptions { Chroma = chromaOptions }),
            new ChromaHttpClient(httpClient, chromaOptions),
            () =>
            {
                generatorResolutions++;
                return generator;
            },
            rebuilder ?? bm25);
        using var output = new StringWriter();
        Assert.Equal(0, await command.ExecuteAsync(force, output));
        return output.ToString();
    }

    private void SeedExistingRecord()
    {
        var collection = server.AddCollection(Collection, dimension: 384);
        collection.Records["existing:1"] = new FakeChromaRecord(
            "existing:1",
            "Уже лежащая запись",
            FakeEmbeddingGenerator.CreateVector("existing"),
            new JsonObject { ["source_file"] = "SourceFiles/Races/Вулканцы.txt", ["content_hash"] = "old" });
    }

    private List<string> SnapshotRecords() =>
        server.Collections.Values
            .SelectMany(collection => collection.Records.Values)
            .Select(record =>
                $"{record.Id}|{record.Document}|{record.Metadata.ToJsonString()}|{string.Join(',', record.Embedding)}")
            .ToList();
}
