using System.Net;
using System.Text;
using System.Text.Json;
using StarTrek.Indexer.Chroma;
using StarTrek.Indexer.Configuration;

namespace StarTrek.Indexer.Tests;

public sealed class ChromaHttpClientTests
{
    private const string CollectionId = "65140ed9-53d2-4fc8-8305-fb289246ff9e";
    private const string CollectionPath =
        "/api/v2/tenants/default_tenant/databases/default_database/collections/" + CollectionId;

    private const string RealCollectionResponse =
        """
        {"id":"65140ed9-53d2-4fc8-8305-fb289246ff9e","name":"startrek",
         "configuration_json":{"hnsw":{"space":"cosine","ef_construction":100,"ef_search":100,"max_neighbors":16,
           "resize_factor":1.2,"sync_threshold":1000},"spann":null,"embedding_function":null},
         "schema":{"defaults":{"string":{"fts_index":{"enabled":false,"config":{}}}}},
         "metadata":{"embedding_model":"intfloat/multilingual-e5-small"},"dimension":null,
         "tenant":"default_tenant","database":"default_database","log_position":0,"version":0}
        """;

    [Fact]
    public async Task HeartbeatAcceptsChromaResponse()
    {
        var handler = new StubHandler((_, _) => Json("""{"nanosecond heartbeat":1791763530112204789}"""));

        await CreateClient(handler).EnsureAvailableAsync();

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("http://127.0.0.1:8000/api/v2/heartbeat", request.Uri.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "<html>not chroma</html>")]
    [InlineData(HttpStatusCode.OK, "{}")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "")]
    public async Task HeartbeatFromWrongServiceIsUnavailable(HttpStatusCode statusCode, string body)
    {
        var handler = new StubHandler((_, _) => Json(body, statusCode));

        var exception = await Assert.ThrowsAsync<ChromaUnavailableException>(
            () => CreateClient(handler).EnsureAvailableAsync());

        Assert.Contains("http://127.0.0.1:8000/", exception.Message);
        Assert.Contains("docker compose up -d", exception.Message);
    }

    [Fact]
    public async Task ClosedPortIsUnavailable()
    {
        var endpoint = ClosedPort.CreateEndpoint();
        using var httpClient = new HttpClient();
        var client = new ChromaHttpClient(httpClient, Options(endpoint.ToString()));

        var exception = await Assert.ThrowsAsync<ChromaUnavailableException>(() => client.EnsureAvailableAsync());

        Assert.Contains(endpoint.ToString(), exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("127.0.0.1:8000")]
    [InlineData("ftp://127.0.0.1:8000")]
    public void RejectsNonHttpEndpoint(string endpoint)
    {
        using var httpClient = new HttpClient();

        Assert.Throws<InvalidOperationException>(() => new ChromaHttpClient(httpClient, Options(endpoint)));
    }

    [Fact]
    public async Task GetOrCreateCollectionSendsCosineSpaceAndParsesRealResponse()
    {
        var handler = new StubHandler((_, _) => Json(RealCollectionResponse));

        var collection = await CreateClient(handler).GetOrCreateCollectionAsync(
            "startrek",
            "cosine",
            new Dictionary<string, object> { ["embedding_model"] = "intfloat/multilingual-e5-small" });

        Assert.Equal(new ChromaCollection(CollectionId, "startrek", null), collection);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v2/tenants/default_tenant/databases/default_database/collections", request.Uri.AbsolutePath);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("startrek", body.RootElement.GetProperty("name").GetString());
        Assert.True(body.RootElement.GetProperty("get_or_create").GetBoolean());
        Assert.Equal(
            "cosine",
            body.RootElement.GetProperty("configuration").GetProperty("hnsw").GetProperty("space").GetString());
        Assert.Equal(
            "intfloat/multilingual-e5-small",
            body.RootElement.GetProperty("metadata").GetProperty("embedding_model").GetString());
    }

    [Fact]
    public async Task UpsertSendsParallelArraysWithEmbeddingsAndFlatMetadata()
    {
        var handler = new StubHandler((_, _) => Json("{}"));
        var records = new[]
        {
            CreateRecord("doc:1", "Документ: Талосианцы", 1),
            CreateRecord("doc:2", "Документ: Теллар", 2)
        };

        await CreateClient(handler).UpsertAsync(CollectionId, records);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(CollectionPath + "/upsert", request.Uri.AbsolutePath);
        Assert.Contains("Документ: Талосианцы", request.Body);
        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.Equal(["doc:1", "doc:2"], root.GetProperty("ids").EnumerateArray().Select(id => id.GetString()));
        Assert.All(
            root.GetProperty("embeddings").EnumerateArray(),
            vector => Assert.Equal(384, vector.GetArrayLength()));
        Assert.Equal(records[0].Embedding[5], root.GetProperty("embeddings")[0][5].GetSingle());
        Assert.Equal(
            ["Документ: Талосианцы", "Документ: Теллар"],
            root.GetProperty("documents").EnumerateArray().Select(document => document.GetString()));
        foreach (var metadata in root.GetProperty("metadatas").EnumerateArray())
        {
            Assert.All(
                metadata.EnumerateObject(),
                property => Assert.Contains(property.Value.ValueKind, new[] { JsonValueKind.String, JsonValueKind.Number }));
        }

        Assert.Equal(2, root.GetProperty("metadatas")[1].GetProperty("chunk_index").GetInt32());
        Assert.Equal("Star Trek", root.GetProperty("metadatas")[0].GetProperty("franchise").GetString());
    }

    [Fact]
    public async Task UpsertSplitsLargeBatches()
    {
        var handler = new StubHandler((_, _) => Json("{}"));
        var records = Enumerable.Range(1, 250).Select(index => CreateRecord($"doc:{index}", "текст", index)).ToArray();

        await CreateClient(handler).UpsertAsync(CollectionId, records);

        Assert.Equal(
            [100, 100, 50],
            handler.Requests.Select(request => JsonDocument.Parse(request.Body).RootElement.GetProperty("ids").GetArrayLength()));
    }

    [Fact]
    public async Task UpsertRejectsNestedMetadataBeforeSending()
    {
        var handler = new StubHandler((_, _) => Json("{}"));
        var record = new ChromaRecord(
            "doc:1",
            "текст",
            FakeEmbeddingGenerator.CreateVector("текст"),
            new Dictionary<string, object> { ["section_path"] = new[] { "История", "Часть 1" } });

        await Assert.ThrowsAsync<ArgumentException>(() => CreateClient(handler).UpsertAsync(CollectionId, [record]));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task UpsertRejectsRecordWithoutEmbeddingBeforeSending()
    {
        var handler = new StubHandler((_, _) => Json("{}"));
        var record = new ChromaRecord("doc:1", "текст", [], new Dictionary<string, object>());

        await Assert.ThrowsAsync<ArgumentException>(() => CreateClient(handler).UpsertAsync(CollectionId, [record]));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetAllRecordsPagesThroughCollectionAndParsesMetadata()
    {
        var handler = new StubHandler((_, body) =>
        {
            using var request = JsonDocument.Parse(body);
            var offset = request.RootElement.GetProperty("offset").GetInt32();
            var count = offset == 0 ? ChromaHttpClient.PageSize : 1;
            var ids = Enumerable.Range(offset, count).Select(index => $"\"doc:{index}\"");
            var metadatas = Enumerable.Range(offset, count).Select(index =>
                $$"""{"content_hash":"h{{index}}","chunk_index":{{index}},"score":0.5,"flag":true}""");
            return Json(
                $$"""
                {"ids":[{{string.Join(',', ids)}}],"embeddings":null,"documents":null,"uris":null,
                 "metadatas":[{{string.Join(',', metadatas)}}],"include":["metadatas"]}
                """);
        });

        var records = await CreateClient(handler).GetAllRecordsAsync(CollectionId);

        Assert.Equal(ChromaHttpClient.PageSize + 1, records.Count);
        Assert.Equal("doc:500", records[^1].Id);
        Assert.Equal("h500", records[^1].GetString("content_hash"));
        Assert.Equal(500L, records[^1].Metadata["chunk_index"]);
        Assert.Equal(0.5d, records[^1].Metadata["score"]);
        Assert.Equal(true, records[^1].Metadata["flag"]);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal(CollectionPath + "/get", request.Uri.AbsolutePath));
        using var second = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal(ChromaHttpClient.PageSize, second.RootElement.GetProperty("offset").GetInt32());
        Assert.Equal(ChromaHttpClient.PageSize, second.RootElement.GetProperty("limit").GetInt32());
        Assert.Equal(["metadatas"], second.RootElement.GetProperty("include").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task ParsesRealGetResponse()
    {
        var handler = new StubHandler((_, _) => Json(
            """
            {"ids":["a:1","a:2"],"embeddings":null,"documents":null,"uris":null,
             "metadatas":[{"k":"v","n":1},{"n":2,"k":"w"}],"include":["metadatas"]}
            """));

        var records = await CreateClient(handler).GetAllRecordsAsync(CollectionId);

        Assert.Equal(["a:1", "a:2"], records.Select(record => record.Id));
        Assert.Equal("w", records[1].GetString("k"));
        Assert.Equal(2L, records[1].Metadata["n"]);
    }

    [Fact]
    public async Task ApiErrorKeepsChromaErrorAndMessage()
    {
        var handler = new StubHandler((_, _) => Json(
            """{"error":"InvalidArgumentError","message":"Collection expecting embedding with dimension of 3, got 2"}""",
            HttpStatusCode.BadRequest));

        var exception = await Assert.ThrowsAsync<ChromaApiException>(
            () => CreateClient(handler).UpsertAsync(CollectionId, [CreateRecord("doc:1", "текст", 1)]));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Equal("InvalidArgumentError", exception.Error);
        Assert.Contains("dimension of 3", exception.Message);
    }

    [Fact]
    public async Task DeleteSendsIdsAndCountParsesNumber()
    {
        var handler = new StubHandler((request, _) => request.Method == HttpMethod.Get
            ? Json("42")
            : Json("""{"deleted":2}"""));
        var client = CreateClient(handler);

        await client.DeleteAsync(CollectionId, ["doc:1", "doc:2"]);
        var count = await client.CountAsync(CollectionId);

        Assert.Equal(42, count);
        Assert.Equal(CollectionPath + "/delete", handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal("""{"ids":["doc:1","doc:2"]}""", handler.Requests[0].Body);
        Assert.Equal(CollectionPath + "/count", handler.Requests[1].Uri.AbsolutePath);
    }

    private static ChromaRecord CreateRecord(string id, string document, int chunkIndex) =>
        new(
            id,
            document,
            FakeEmbeddingGenerator.CreateVector(id),
            new Dictionary<string, object>
            {
                ["document_id"] = id.Split(':')[0],
                ["franchise"] = "Star Trek",
                ["chunk_index"] = chunkIndex
            });

    private static ChromaHttpClient CreateClient(StubHandler handler) =>
        new(new HttpClient(handler), Options("http://127.0.0.1:8000"));

    private static ChromaOptions Options(string endpoint) =>
        new() { Endpoint = endpoint, Collection = "startrek" };

    private static HttpResponseMessage Json(string json, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed record CapturedRequest(HttpMethod Method, Uri Uri, string Body);

    private sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri!, body));
            return respond(request, body);
        }
    }
}
