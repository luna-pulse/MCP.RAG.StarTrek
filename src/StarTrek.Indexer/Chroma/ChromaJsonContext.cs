using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StarTrek.Indexer.Chroma;

internal sealed record ChromaHeartbeatResponse(
    [property: JsonPropertyName("nanosecond heartbeat")] long? NanosecondHeartbeat);

internal sealed record ChromaCreateCollectionRequest(
    string Name,
    bool GetOrCreate,
    ChromaCollectionConfiguration? Configuration,
    IReadOnlyDictionary<string, object>? Metadata);

internal sealed record ChromaCollectionConfiguration(ChromaHnswConfiguration? Hnsw);

internal sealed record ChromaHnswConfiguration(string? Space);

internal sealed record ChromaCollectionResponse(string? Id, string? Name, int? Dimension);

internal sealed record ChromaUpsertRequest(
    IReadOnlyList<string> Ids,
    IReadOnlyList<float[]> Embeddings,
    IReadOnlyList<string> Documents,
    IReadOnlyList<IReadOnlyDictionary<string, object>> Metadatas);

internal sealed record ChromaGetRequest(
    IReadOnlyList<string> Include,
    int Limit,
    int Offset,
    IReadOnlyList<string>? Ids = null);

internal sealed record ChromaGetResponse(
    IReadOnlyList<string>? Ids,
    IReadOnlyList<string?>? Documents,
    IReadOnlyList<Dictionary<string, JsonElement>?>? Metadatas);

internal sealed record ChromaQueryRequest(
    IReadOnlyList<float[]> QueryEmbeddings,
    [property: JsonPropertyName("n_results")] int NResults,
    IReadOnlyList<string> Include);

internal sealed record ChromaQueryResponse(
    IReadOnlyList<IReadOnlyList<string>>? Ids,
    IReadOnlyList<IReadOnlyList<double?>>? Distances,
    IReadOnlyList<IReadOnlyList<string?>>? Documents,
    IReadOnlyList<IReadOnlyList<Dictionary<string, JsonElement>?>>? Metadatas);

internal sealed record ChromaDeleteRequest(IReadOnlyList<string> Ids);

internal sealed record ChromaErrorResponse(string? Error, string? Message);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ChromaHeartbeatResponse))]
[JsonSerializable(typeof(ChromaCreateCollectionRequest))]
[JsonSerializable(typeof(ChromaCollectionResponse))]
[JsonSerializable(typeof(ChromaUpsertRequest))]
[JsonSerializable(typeof(ChromaGetRequest))]
[JsonSerializable(typeof(ChromaGetResponse))]
[JsonSerializable(typeof(ChromaQueryRequest))]
[JsonSerializable(typeof(ChromaQueryResponse))]
[JsonSerializable(typeof(ChromaQueryRequest))]
[JsonSerializable(typeof(ChromaQueryResponse))]
[JsonSerializable(typeof(ChromaDeleteRequest))]
[JsonSerializable(typeof(ChromaErrorResponse))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(bool))]
internal sealed partial class ChromaJsonContext : JsonSerializerContext
{
    public static ChromaJsonContext Relaxed { get; } = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
}
