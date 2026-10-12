using System.Text.Json.Serialization;

namespace StarTrek.Indexer.Chunking;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(ChunkedDocument))]
internal sealed partial class ChunkJsonContext : JsonSerializerContext;
