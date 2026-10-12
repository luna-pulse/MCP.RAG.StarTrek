using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StarTrek.Indexer.Chroma;
using StarTrek.Indexer.Configuration;

namespace StarTrek.Mcp.Tests;

internal sealed record ScriptedChromaRequest(HttpMethod Method, string Path, string Body);

internal sealed class ScriptedChromaHandler : HttpMessageHandler
{
    private const string CollectionsPath =
        "/api/v2/tenants/default_tenant/databases/default_database/collections";

    private readonly Dictionary<string, ScriptedCollection> collections = new(StringComparer.Ordinal);

    public bool Available { get; set; } = true;

    public List<ScriptedChromaRequest> Requests { get; } = [];

    public void AddCollection(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        collections.TryAdd(name, new ScriptedCollection(Guid.NewGuid().ToString("N"), name));
    }

    public void Seed(
        string collectionName,
        string id,
        string? document,
        IReadOnlyDictionary<string, object?>? metadata,
        double distance)
    {
        if (!collections.TryGetValue(collectionName, out var collection))
        {
            throw new InvalidOperationException($"Коллекция {collectionName} не посеяна в двойнике Chroma.");
        }

        collection.Records.Add(new ScriptedRecord(id, document, metadata, distance));
    }

    public ChromaHttpClient CreateClient(string endpoint = "http://127.0.0.1:9/")
    {
        var httpClient = new HttpClient(this, disposeHandler: false);
        return new ChromaHttpClient(
            httpClient,
            new ChromaOptions
            {
                Endpoint = endpoint,
                Collection = "startrek",
                Tenant = "default_tenant",
                Database = "default_database"
            });
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        var path = request.RequestUri!.AbsolutePath;
        Requests.Add(new ScriptedChromaRequest(request.Method, path, body));
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
            return Error(HttpStatusCode.InternalServerError, "InternalError", "Создание коллекции отклонено двойником.");
        }

        if (request.Method == HttpMethod.Get &&
            path.StartsWith(CollectionsPath + "/", StringComparison.Ordinal) &&
            !path[(CollectionsPath.Length + 1)..].Contains('/'))
        {
            var name = Uri.UnescapeDataString(path[(CollectionsPath.Length + 1)..]);
            return collections.TryGetValue(name, out var existing)
                ? Json(CollectionJson(existing))
                : Error(HttpStatusCode.NotFound, "NotFoundError", $"Collection [{name}] does not exist");
        }

        if (!TryParseAction(path, out var collectionId, out var action))
        {
            return Error(HttpStatusCode.NotFound, "NotFoundError", $"Нет маршрута {path}");
        }

        var collection = collections.Values.SingleOrDefault(item => item.Id == collectionId);
        if (collection is null)
        {
            return Error(HttpStatusCode.NotFound, "NotFoundError", $"Collection [{collectionId}] does not exist");
        }

        return action switch
        {
            "upsert" => Error(HttpStatusCode.InternalServerError, "InternalError", "Upsert отклонён двойником."),
            "get" => Get(collection, JsonNode.Parse(body)!.AsObject()),
            "query" => Query(collection, JsonNode.Parse(body)!.AsObject()),
            _ => Error(HttpStatusCode.NotFound, "NotFoundError", $"Нет маршрута {path}")
        };
    }

    private static bool TryParseAction(string path, out string collectionId, out string action)
    {
        collectionId = string.Empty;
        action = string.Empty;
        var prefix = CollectionsPath + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = path[prefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1 || rest[(slash + 1)..].Contains('/'))
        {
            return false;
        }

        collectionId = Uri.UnescapeDataString(rest[..slash]);
        action = rest[(slash + 1)..];
        return true;
    }

    private static string CollectionJson(ScriptedCollection collection) =>
        new JsonObject
        {
            ["id"] = collection.Id,
            ["name"] = collection.Name,
            ["dimension"] = null,
            ["tenant"] = "default_tenant",
            ["database"] = "default_database"
        }.ToJsonString();

    private static HttpResponseMessage Get(ScriptedCollection collection, JsonObject request)
    {
        var requestedIds = request["ids"]?.AsArray().Select(id => id!.GetValue<string>()).ToArray();
        var page = new List<ScriptedRecord>();
        if (requestedIds is null)
        {
            page.AddRange(collection.Records);
        }
        else
        {
            foreach (var id in requestedIds)
            {
                var found = collection.Records.FirstOrDefault(record => record.Id == id);
                if (found is not null)
                {
                    page.Add(found);
                }
            }
        }

        return Json(new JsonObject
        {
            ["ids"] = new JsonArray(page.Select(record => (JsonNode)record.Id).ToArray()),
            ["documents"] = new JsonArray(page.Select(record => (JsonNode?)record.Document).ToArray()),
            ["metadatas"] = new JsonArray(page.Select(record => MetadataNode(record.Metadata)).ToArray()),
            ["embeddings"] = null,
            ["uris"] = null,
            ["include"] = new JsonArray("documents", "metadatas")
        }.ToJsonString());
    }

    private static HttpResponseMessage Query(ScriptedCollection collection, JsonObject request)
    {
        var count = request["n_results"]!.GetValue<int>();
        var page = collection.Records.Take(count).ToList();
        return Json(new JsonObject
        {
            ["ids"] = new JsonArray(new JsonArray(page.Select(record => (JsonNode)record.Id).ToArray())),
            ["distances"] = new JsonArray(new JsonArray(page.Select(record => (JsonNode)record.Distance).ToArray())),
            ["documents"] = new JsonArray(new JsonArray(page.Select(record => (JsonNode?)record.Document).ToArray())),
            ["metadatas"] = new JsonArray(new JsonArray(page.Select(record => MetadataNode(record.Metadata)).ToArray())),
            ["embeddings"] = null,
            ["uris"] = null,
            ["include"] = new JsonArray("documents", "metadatas", "distances")
        }.ToJsonString());
    }

    private static JsonNode? MetadataNode(IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata is null)
        {
            return null;
        }

        var node = new JsonObject();
        foreach (var (key, value) in metadata)
        {
            node[key] = value switch
            {
                null => null,
                string text => text,
                bool flag => flag,
                int number => number,
                long number => number,
                double number => number,
                float number => number,
                _ => value.ToString()
            };
        }

        return node;
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(HttpStatusCode statusCode, string error, string message) =>
        Json(JsonSerializer.Serialize(new { error, message }), statusCode);

    private sealed class ScriptedCollection(string id, string name)
    {
        public string Id { get; } = id;

        public string Name { get; } = name;

        public List<ScriptedRecord> Records { get; } = [];
    }

    private sealed record ScriptedRecord(
        string Id,
        string? Document,
        IReadOnlyDictionary<string, object?>? Metadata,
        double Distance);
}
