using System.Text.Json.Serialization;

namespace StarTrek.Indexer.Search;

public sealed record SearchChunk
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    [JsonPropertyName("bm25Score")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Bm25Score { get; init; }

    [JsonPropertyName("distance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Distance { get; init; }

    [JsonPropertyName("rrfScore")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RrfScore { get; init; }

    [JsonPropertyName("vectorRank")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? VectorRank { get; init; }

    [JsonPropertyName("bm25Rank")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Bm25Rank { get; init; }

    [JsonPropertyName("sourceFile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceFile { get; init; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    [JsonPropertyName("originalTitle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OriginalTitle { get; init; }

    [JsonPropertyName("entityType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntityType { get; init; }

    [JsonPropertyName("sectionPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SectionPath { get; init; }

    [JsonPropertyName("position")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Position { get; init; }

    [JsonPropertyName("chunkIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ChunkIndex { get; init; }
}

public sealed record ChunkSearchResponse
{
    [JsonPropertyName("query")]
    public required string Query { get; init; }

    [JsonPropertyName("chunks")]
    public required IReadOnlyList<SearchChunk> Chunks { get; init; }
}
