using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using StarTrek.Indexer.Configuration;

namespace StarTrek.Indexer.Chroma;

public sealed class ChromaHttpClient
{
    public const int MaxRecordsPerRequest = 100;
    public const int PageSize = 500;
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient httpClient;
    private readonly string databasePath;

    public ChromaHttpClient(HttpClient httpClient, ChromaOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Database);

        this.httpClient = httpClient;
        Endpoint = ParseEndpoint(options.Endpoint);
        databasePath =
            $"api/v2/tenants/{Uri.EscapeDataString(options.Tenant)}" +
            $"/databases/{Uri.EscapeDataString(options.Database)}/";
    }

    public Uri Endpoint { get; }

    public static Uri ParseEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) ||
            !Uri.TryCreate(endpoint.TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"Indexer:Chroma:Endpoint должен быть абсолютным http-адресом, получено: «{endpoint}».");
        }

        return uri;
    }

    public async Task EnsureAvailableAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HeartbeatTimeout);
        try
        {
            using var response = await httpClient.GetAsync(new Uri(Endpoint, "api/v2/heartbeat"), timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw Unavailable($"heartbeat вернул HTTP {(int)response.StatusCode}");
            }

            var heartbeat = await response.Content.ReadFromJsonAsync(
                ChromaJsonContext.Relaxed.ChromaHeartbeatResponse,
                timeout.Token);
            if (heartbeat?.NanosecondHeartbeat is not > 0)
            {
                throw Unavailable("по адресу отвечает не ChromaDB API v2");
            }
        }
        catch (JsonException exception)
        {
            throw Unavailable("по адресу отвечает не ChromaDB API v2", exception);
        }
        catch (HttpRequestException exception)
        {
            throw Unavailable(exception.Message, exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unavailable($"нет ответа за {HeartbeatTimeout.TotalSeconds:0} с", exception);
        }
    }

    public async Task<ChromaCollection> GetOrCreateCollectionAsync(
        string name,
        string distanceSpace,
        IReadOnlyDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(distanceSpace);
        if (metadata is not null)
        {
            EnsureFlatMetadata(metadata, $"коллекции {name}");
        }

        var request = new ChromaCreateCollectionRequest(
            name,
            GetOrCreate: true,
            new ChromaCollectionConfiguration(new ChromaHnswConfiguration(distanceSpace)),
            metadata);
        var response = await PostAsync(
            "создать или открыть коллекцию",
            databasePath + "collections",
            request,
            ChromaJsonContext.Relaxed.ChromaCreateCollectionRequest,
            ChromaJsonContext.Relaxed.ChromaCollectionResponse,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(response?.Id))
        {
            throw new InvalidDataException($"ChromaDB не вернула id коллекции {name}.");
        }

        return new ChromaCollection(response.Id, response.Name ?? name, response.Dimension);
    }

    /// <summary>Открывает существующую коллекцию по имени, не создавая её. Нет коллекции — null.</summary>
    public async Task<ChromaCollection?> TryGetCollectionAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        try
        {
            using var response = await SendAsync(
                "открыть коллекцию",
                new HttpRequestMessage(
                    HttpMethod.Get,
                    new Uri(Endpoint, databasePath + "collections/" + Uri.EscapeDataString(name))),
                cancellationToken);
            var collection = await response.Content.ReadFromJsonAsync(
                ChromaJsonContext.Relaxed.ChromaCollectionResponse,
                cancellationToken);
            return string.IsNullOrWhiteSpace(collection?.Id)
                ? null
                : new ChromaCollection(collection.Id, collection.Name ?? name, collection.Dimension);
        }
        catch (ChromaApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>Векторный поиск: n_results ближайших соседей к вектору запроса, по возрастанию расстояния.</summary>
    public async Task<IReadOnlyList<ChromaQueryHit>> QueryAsync(
        string collectionId,
        float[] queryEmbedding,
        int nResults,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentNullException.ThrowIfNull(queryEmbedding);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nResults);
        if (queryEmbedding.Length == 0 || !Array.TrueForAll(queryEmbedding, float.IsFinite))
        {
            throw new ArgumentException("Вектор запроса пуст или содержит NaN/бесконечность.", nameof(queryEmbedding));
        }

        var response = await PostAsync(
            "выполнить векторный запрос",
            CollectionPath(collectionId, "query"),
            new ChromaQueryRequest([queryEmbedding], nResults, ["documents", "metadatas", "distances"]),
            ChromaJsonContext.Relaxed.ChromaQueryRequest,
            ChromaJsonContext.Relaxed.ChromaQueryResponse,
            cancellationToken);
        var ids = response?.Ids?.FirstOrDefault() ?? [];
        var distances = response?.Distances?.FirstOrDefault();
        var documents = response?.Documents?.FirstOrDefault();
        var metadatas = response?.Metadatas?.FirstOrDefault();
        if (distances is null || distances.Count != ids.Count)
        {
            throw new InvalidDataException(
                $"ChromaDB вернула {ids.Count} id и {distances?.Count ?? 0} расстояний в ответе на векторный запрос.");
        }

        var hits = new List<ChromaQueryHit>(ids.Count);
        for (var index = 0; index < ids.Count; index++)
        {
            hits.Add(new ChromaQueryHit(
                ids[index],
                distances[index] ?? double.PositiveInfinity,
                documents is not null && index < documents.Count ? documents[index] : null,
                ConvertMetadata(metadatas is not null && index < metadatas.Count ? metadatas[index] : null)));
        }

        return hits;
    }

    /// <summary>Читает записи (текст и metadata) по списку id. Отсутствующие id пропускаются.</summary>
    public async Task<IReadOnlyList<ChromaDocumentRecord>> GetByIdsAsync(
        string collectionId,
        IReadOnlyCollection<string> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentNullException.ThrowIfNull(ids);

        var records = new List<ChromaDocumentRecord>(ids.Count);
        foreach (var batch in ids.Distinct(StringComparer.Ordinal).Chunk(PageSize))
        {
            var page = await PostAsync(
                "прочитать записи по id",
                CollectionPath(collectionId, "get"),
                new ChromaGetRequest(["documents", "metadatas"], batch.Length, 0, batch),
                ChromaJsonContext.Relaxed.ChromaGetRequest,
                ChromaJsonContext.Relaxed.ChromaGetResponse,
                cancellationToken);
            var found = page?.Ids ?? [];
            var documents = page?.Documents;
            var metadatas = page?.Metadatas;
            for (var index = 0; index < found.Count; index++)
            {
                records.Add(new ChromaDocumentRecord(
                    found[index],
                    documents is not null && index < documents.Count ? documents[index] : null,
                    ConvertMetadata(metadatas is not null && index < metadatas.Count ? metadatas[index] : null)));
            }
        }

        return records;
    }

    public async Task<IReadOnlyList<ChromaStoredRecord>> GetAllRecordsAsync(
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);

        var records = new List<ChromaStoredRecord>();
        for (var offset = 0; ; offset += PageSize)
        {
            var page = await PostAsync(
                "прочитать metadata коллекции",
                CollectionPath(collectionId, "get"),
                new ChromaGetRequest(["metadatas"], PageSize, offset),
                ChromaJsonContext.Relaxed.ChromaGetRequest,
                ChromaJsonContext.Relaxed.ChromaGetResponse,
                cancellationToken);
            var ids = page?.Ids ?? [];
            var metadatas = page?.Metadatas;
            if (metadatas is not null && metadatas.Count != ids.Count)
            {
                throw new InvalidDataException(
                    $"ChromaDB вернула {ids.Count} id и {metadatas.Count} metadata в одной странице.");
            }

            for (var index = 0; index < ids.Count; index++)
            {
                records.Add(new ChromaStoredRecord(ids[index], ConvertMetadata(metadatas?[index])));
            }

            if (ids.Count < PageSize)
            {
                return records;
            }
        }
    }

    public async Task UpsertAsync(
        string collectionId,
        IReadOnlyList<ChromaRecord> records,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentNullException.ThrowIfNull(records);
        ValidateRecords(records);

        foreach (var batch in records.Chunk(MaxRecordsPerRequest))
        {
            var request = new ChromaUpsertRequest(
                batch.Select(record => record.Id).ToArray(),
                batch.Select(record => record.Embedding).ToArray(),
                batch.Select(record => record.Document).ToArray(),
                batch.Select(record => record.Metadata).ToArray());
            await PostAsync(
                "записать чанки",
                CollectionPath(collectionId, "upsert"),
                request,
                ChromaJsonContext.Relaxed.ChromaUpsertRequest,
                cancellationToken);
        }
    }

    public async Task DeleteAsync(
        string collectionId,
        IReadOnlyCollection<string> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentNullException.ThrowIfNull(ids);

        foreach (var batch in ids.Chunk(MaxRecordsPerRequest))
        {
            await PostAsync(
                "удалить записи",
                CollectionPath(collectionId, "delete"),
                new ChromaDeleteRequest(batch),
                ChromaJsonContext.Relaxed.ChromaDeleteRequest,
                cancellationToken);
        }
    }

    public async Task<int> CountAsync(string collectionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);

        const string operation = "посчитать записи коллекции";
        using var response = await SendAsync(
            operation,
            new HttpRequestMessage(HttpMethod.Get, new Uri(Endpoint, CollectionPath(collectionId, "count"))),
            cancellationToken);
        return await response.Content.ReadFromJsonAsync(ChromaJsonContext.Relaxed.Int32, cancellationToken);
    }

    public static void EnsureFlatMetadata(IReadOnlyDictionary<string, object> metadata, string owner)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        foreach (var (key, value) in metadata)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException($"Пустой ключ metadata у {owner}.");
            }

            if (value is not (string or int or long or double or bool))
            {
                throw new ArgumentException(
                    $"Metadata «{key}» у {owner} должна быть строкой, числом или bool, " +
                    $"получено: {value?.GetType().Name ?? "null"}.");
            }

            if (value is double number && !double.IsFinite(number))
            {
                throw new ArgumentException($"Metadata «{key}» у {owner} не является конечным числом.");
            }
        }
    }

    private static void ValidateRecords(IReadOnlyList<ChromaRecord> records)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int? dimensions = null;
        foreach (var record in records)
        {
            ArgumentNullException.ThrowIfNull(record);
            ArgumentException.ThrowIfNullOrWhiteSpace(record.Id);
            if (!ids.Add(record.Id))
            {
                throw new ArgumentException($"Запись {record.Id} повторяется в одном upsert.");
            }

            if (record.Embedding is not { Length: > 0 })
            {
                throw new ArgumentException($"У записи {record.Id} нет эмбеддинга.");
            }

            dimensions ??= record.Embedding.Length;
            if (record.Embedding.Length != dimensions)
            {
                throw new ArgumentException(
                    $"У записи {record.Id} эмбеддинг длины {record.Embedding.Length}, ожидалось {dimensions}.");
            }

            if (!Array.TrueForAll(record.Embedding, float.IsFinite))
            {
                throw new ArgumentException($"Эмбеддинг записи {record.Id} содержит NaN или бесконечность.");
            }

            ArgumentNullException.ThrowIfNull(record.Document);
            EnsureFlatMetadata(record.Metadata, $"записи {record.Id}");
        }
    }

    private static Dictionary<string, object?> ConvertMetadata(Dictionary<string, JsonElement>? metadata)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (metadata is null)
        {
            return result;
        }

        foreach (var (key, value) in metadata)
        {
            result[key] = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
                JsonValueKind.Number => value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => value.GetRawText()
            };
        }

        return result;
    }

    private string CollectionPath(string collectionId, string action) =>
        $"{databasePath}collections/{Uri.EscapeDataString(collectionId)}/{action}";

    private async Task<TResponse?> PostAsync<TRequest, TResponse>(
        string operation,
        string relativePath,
        TRequest request,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken)
    {
        using var response = await SendPostAsync(operation, relativePath, request, requestTypeInfo, cancellationToken);
        return await response.Content.ReadFromJsonAsync(responseTypeInfo, cancellationToken);
    }

    private async Task PostAsync<TRequest>(
        string operation,
        string relativePath,
        TRequest request,
        JsonTypeInfo<TRequest> requestTypeInfo,
        CancellationToken cancellationToken)
    {
        using var response = await SendPostAsync(operation, relativePath, request, requestTypeInfo, cancellationToken);
    }

    private Task<HttpResponseMessage> SendPostAsync<TRequest>(
        string operation,
        string relativePath,
        TRequest request,
        JsonTypeInfo<TRequest> requestTypeInfo,
        CancellationToken cancellationToken) =>
        SendAsync(
            operation,
            new HttpRequestMessage(HttpMethod.Post, new Uri(Endpoint, relativePath))
            {
                Content = JsonContent.Create(request, requestTypeInfo)
            },
            cancellationToken);

    private async Task<HttpResponseMessage> SendAsync(
        string operation,
        HttpRequestMessage message,
        CancellationToken cancellationToken)
    {
        using (message)
        {
            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(message, cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                throw Unavailable(exception.Message, exception);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw Unavailable($"нет ответа за {httpClient.Timeout.TotalSeconds:0} с", exception);
            }

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                ChromaErrorResponse? error = null;
                try
                {
                    error = JsonSerializer.Deserialize(body, ChromaJsonContext.Relaxed.ChromaErrorResponse);
                }
                catch (JsonException)
                {
                }

                throw new ChromaApiException(
                    operation,
                    response.StatusCode,
                    error?.Error,
                    error?.Message ?? (string.IsNullOrWhiteSpace(body) ? null : body));
            }
        }
    }

    private ChromaUnavailableException Unavailable(string reason, Exception? innerException = null) =>
        new(
            $"ChromaDB не отвечает по адресу {Endpoint} ({reason}). " +
            "Запустите её командой «docker compose up -d» из корня репозитория.",
            innerException);
}
