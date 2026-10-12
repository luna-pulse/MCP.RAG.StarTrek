using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StarTrek.Indexer.Tests;

internal sealed record FakeChromaRequest(HttpMethod Method, string Path, string Body);

internal sealed record FakeChromaRecord(string Id, string? Document, float[] Embedding, JsonObject Metadata);

internal sealed class FakeChromaCollection(string id, string name, int? dimension, string? space)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public int? Dimension { get; set; } = dimension;
    public string? Space { get; } = space;
    public SortedDictionary<string, FakeChromaRecord> Records { get; } = new(StringComparer.Ordinal);
}

internal sealed partial class FakeChromaServer : HttpMessageHandler
{
    public const string CollectionsPath = "/api/v2/tenants/default_tenant/databases/default_database/collections";

    private readonly Dictionary<string, FakeChromaCollection> collections = new(StringComparer.Ordinal);

    public bool Available { get; set; } = true;
    public bool RejectUpserts { get; set; }
    public List<FakeChromaRequest> Requests { get; } = [];
    public IReadOnlyDictionary<string, FakeChromaCollection> Collections => collections;

    public IEnumerable<FakeChromaRequest> Writes => Requests.Where(request =>
        request.Path.EndsWith("/upsert", StringComparison.Ordinal) ||
        request.Path.EndsWith("/delete", StringComparison.Ordinal));

    public FakeChromaCollection AddCollection(string name, int? dimension = null)
    {
        var collection = new FakeChromaCollection(Guid.NewGuid().ToString(), name, dimension, "cosine");
        collections[name] = collection;
        return collection;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var path = request.RequestUri!.AbsolutePath;
        Requests.Add(new FakeChromaRequest(request.Method, path, body));
        if (!Available)
        {
            throw new HttpRequestException(
                "Подключение не установлено.",
                new SocketException((int)SocketError.ConnectionRefused));
        }

        if (request.Method == HttpMethod.Get && path == "/api/v2/heartbeat")
        {
            return Json("""{"nanosecond heartbeat":1791763530112204789}""");
        }

        if (request.Method == HttpMethod.Post && path == CollectionsPath)
        {
            return CreateCollection(JsonNode.Parse(body)!.AsObject());
        }

        if (request.Method == HttpMethod.Get && path.StartsWith(CollectionsPath + "/", StringComparison.Ordinal) &&
            !path[(CollectionsPath.Length + 1)..].Contains('/'))
        {
            var name = Uri.UnescapeDataString(path[(CollectionsPath.Length + 1)..]);
            return collections.TryGetValue(name, out var existing)
                ? Json(CollectionJson(existing, metadata: null))
                : Error(HttpStatusCode.NotFound, "NotFoundError", $"Collection [{name}] does not exist");
        }

        var match = CollectionAction().Match(path);
        if (!match.Success)
        {
            return Error(HttpStatusCode.NotFound, "NotFoundError", $"Нет маршрута {path}");
        }

        var collection = collections.Values.SingleOrDefault(item => item.Id == match.Groups["id"].Value);
        if (collection is null)
        {
            return Error(
                HttpStatusCode.NotFound,
                "NotFoundError",
                $"Collection [{match.Groups["id"].Value}] does not exist");
        }

        return match.Groups["action"].Value switch
        {
            "upsert" when RejectUpserts => Error(
                HttpStatusCode.InternalServerError,
                "InternalError",
                "Имитирован отказ upsert"),
            "upsert" => Upsert(collection, JsonNode.Parse(body)!.AsObject()),
            "get" => Get(collection, JsonNode.Parse(body)!.AsObject()),
            "query" => Query(collection, JsonNode.Parse(body)!.AsObject()),
            "delete" => Delete(collection, JsonNode.Parse(body)!.AsObject()),
            _ => Json(collection.Records.Count.ToString())
        };
    }

    private HttpResponseMessage CreateCollection(JsonObject request)
    {
        var name = request["name"]!.GetValue<string>();
        if (!collections.TryGetValue(name, out var collection))
        {
            collection = new FakeChromaCollection(
                Guid.NewGuid().ToString(),
                name,
                dimension: null,
                request["configuration"]?["hnsw"]?["space"]?.GetValue<string>());
            collections[name] = collection;
        }
        else if (request["get_or_create"]?.GetValue<bool>() != true)
        {
            return Error(HttpStatusCode.Conflict, "UniqueConstraintError", $"Collection {name} already exists");
        }

        return Json(CollectionJson(collection, request["metadata"]));
    }

    private static string CollectionJson(FakeChromaCollection collection, JsonNode? metadata) =>
        new JsonObject
        {
            ["id"] = collection.Id,
            ["name"] = collection.Name,
            ["configuration_json"] = new JsonObject
            {
                ["hnsw"] = new JsonObject { ["space"] = collection.Space ?? "l2", ["ef_construction"] = 100 },
                ["spann"] = null,
                ["embedding_function"] = null
            },
            ["metadata"] = metadata?.DeepClone(),
            ["dimension"] = collection.Dimension,
            ["tenant"] = "default_tenant",
            ["database"] = "default_database",
            ["log_position"] = 0,
            ["version"] = 0
        }.ToJsonString();

    private static HttpResponseMessage Upsert(FakeChromaCollection collection, JsonObject request)
    {
        var ids = request["ids"]!.AsArray().Select(id => id!.GetValue<string>()).ToArray();
        var embeddings = request["embeddings"]!.AsArray()
            .Select(vector => vector!.AsArray().Select(value => value!.GetValue<float>()).ToArray())
            .ToArray();
        var documents = request["documents"]?.AsArray();
        var metadatas = request["metadatas"]?.AsArray();
        if (embeddings.Length != ids.Length ||
            (documents is not null && documents.Count != ids.Length) ||
            (metadatas is not null && metadatas.Count != ids.Length))
        {
            return Error(HttpStatusCode.BadRequest, "InvalidArgumentError", "Arrays must have the same length");
        }

        foreach (var embedding in embeddings)
        {
            collection.Dimension ??= embedding.Length;
            if (embedding.Length != collection.Dimension)
            {
                return Error(
                    HttpStatusCode.BadRequest,
                    "InvalidArgumentError",
                    $"Collection expecting embedding with dimension of {collection.Dimension}, got {embedding.Length}");
            }
        }

        if (metadatas is not null &&
            metadatas.Any(metadata => metadata is JsonObject values &&
                values.Any(pair => pair.Value is null or JsonObject or JsonArray)))
        {
            return Error(
                (HttpStatusCode)422,
                "ChromaError",
                "data did not match any variant of untagged enum UpdateMetadataValue");
        }

        for (var index = 0; index < ids.Length; index++)
        {
            collection.Records[ids[index]] = new FakeChromaRecord(
                ids[index],
                documents?[index]?.GetValue<string>(),
                embeddings[index],
                (metadatas?[index]?.DeepClone() as JsonObject) ?? []);
        }

        return Json("{}");
    }

    private static HttpResponseMessage Get(FakeChromaCollection collection, JsonObject request)
    {
        var include = request["include"]?.AsArray().Select(value => value!.GetValue<string>()).ToHashSet() ??
            ["documents", "metadatas"];
        var offset = request["offset"]?.GetValue<int>() ?? 0;
        var limit = request["limit"]?.GetValue<int>() ?? int.MaxValue;
        var requestedIds = request["ids"]?.AsArray().Select(id => id!.GetValue<string>()).ToHashSet();
        var page = collection.Records.Values
            .Where(record => requestedIds is null || requestedIds.Contains(record.Id))
            .Skip(offset)
            .Take(limit)
            .ToList();
        return Json(new JsonObject
        {
            ["ids"] = new JsonArray(page.Select(record => (JsonNode)record.Id).ToArray()),
            ["embeddings"] = null,
            ["documents"] = include.Contains("documents")
                ? new JsonArray(page.Select(record => (JsonNode?)record.Document).ToArray())
                : null,
            ["uris"] = null,
            ["metadatas"] = include.Contains("metadatas")
                ? new JsonArray(page.Select(record => record.Metadata.DeepClone()).ToArray())
                : null,
            ["include"] = new JsonArray(include.Select(value => (JsonNode)value).ToArray())
        }.ToJsonString());
    }

    private static HttpResponseMessage Query(FakeChromaCollection collection, JsonObject request)
    {
        var queryVector = request["query_embeddings"]!.AsArray()[0]!.AsArray()
            .Select(value => value!.GetValue<float>())
            .ToArray();
        var count = request["n_results"]!.GetValue<int>();
        var neighbours = collection.Records.Values
            .Select(record => (Record: record, Distance: 1d - record.Embedding.Zip(queryVector, (a, b) => (double)a * b).Sum()))
            .OrderBy(pair => pair.Distance)
            .ThenBy(pair => pair.Record.Id, StringComparer.Ordinal)
            .Take(count)
            .ToList();
        return Json(new JsonObject
        {
            ["ids"] = new JsonArray(new JsonArray(neighbours.Select(pair => (JsonNode)pair.Record.Id).ToArray())),
            ["distances"] = new JsonArray(new JsonArray(neighbours.Select(pair => (JsonNode)pair.Distance).ToArray())),
            ["documents"] = new JsonArray(new JsonArray(neighbours.Select(pair => (JsonNode?)pair.Record.Document).ToArray())),
            ["metadatas"] = new JsonArray(new JsonArray(neighbours.Select(pair => pair.Record.Metadata.DeepClone()).ToArray())),
            ["embeddings"] = null,
            ["uris"] = null,
            ["include"] = new JsonArray("documents", "metadatas", "distances")
        }.ToJsonString());
    }

    private static HttpResponseMessage Delete(FakeChromaCollection collection, JsonObject request)
    {
        var ids = request["ids"]!.AsArray().Select(id => id!.GetValue<string>()).ToArray();
        foreach (var id in ids)
        {
            collection.Records.Remove(id);
        }

        return Json($$"""{"deleted":{{ids.Length}}}""");
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(HttpStatusCode statusCode, string error, string message) =>
        Json(JsonSerializer.Serialize(new { error, message }), statusCode);

    [GeneratedRegex(@"^/api/v2/tenants/default_tenant/databases/default_database/collections/(?<id>[^/]+)/(?<action>upsert|get|query|delete|count)$")]
    private static partial Regex CollectionAction();
}
