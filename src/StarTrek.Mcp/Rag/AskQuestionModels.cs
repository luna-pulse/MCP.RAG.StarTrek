using System.Text.Json.Serialization;

namespace StarTrek.Mcp.Rag;

public sealed record AskQuestionResponse
{
    [JsonPropertyName("answer")]
    public required string Answer { get; init; }

    [JsonPropertyName("sources")]
    public required IReadOnlyList<AskQuestionSource> Sources { get; init; }

    [JsonPropertyName("meta")]
    public AskQuestionMeta? Meta { get; init; }
}

public sealed record AskQuestionSource
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }

    [JsonPropertyName("sourceFile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceFile { get; init; }

    [JsonPropertyName("position")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Position { get; init; }

    [JsonPropertyName("chunkIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ChunkIndex { get; init; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    [JsonPropertyName("sectionPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SectionPath { get; init; }
}

public sealed record AskQuestionMeta
{
    [JsonPropertyName("rewrittenQuery")]
    public required string RewrittenQuery { get; init; }

    [JsonPropertyName("query")]
    public required string Query { get; init; }

    [JsonPropertyName("attempts")]
    public required int Attempts { get; init; }
}
