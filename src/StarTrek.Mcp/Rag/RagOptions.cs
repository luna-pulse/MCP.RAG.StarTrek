namespace StarTrek.Mcp.Rag;

public sealed class RagOptions
{
    public const string SectionName = "Rag";

    public OllamaOptions Ollama { get; init; } = new();

    /// <summary>Сколько релевантных чанков достаточно, чтобы перейти к ответу.</summary>
    public int MinRelevantChunks { get; init; } = 2;

    /// <summary>Сколько раз можно расширить запрос и повторить поиск.</summary>
    public int MaxBroadenAttempts { get; init; } = 2;

    /// <summary>Сколько чанков брать из гибридного поиска на одной попытке.</summary>
    public int RetrieveCount { get; init; } = 8;

    public string PromptsDirectory { get; init; } = "Prompts";
}

public sealed class OllamaOptions
{
    public string Endpoint { get; init; } = "http://127.0.0.1:11434";

    public string Model { get; init; } = "qwen2.5:3b";
}
