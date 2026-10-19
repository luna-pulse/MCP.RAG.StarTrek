namespace StarTrek.Indexer.Configuration;

/// <summary>Локальная чат-модель Ollama для шагов Corrective RAG.</summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>Адрес API Ollama, без платного провайдера.</summary>
    public string Endpoint { get; init; } = "http://localhost:11434";

    /// <summary>Тег модели. По умолчанию локальная Qwen 3.5 4B.</summary>
    public string Model { get; init; } = "qwen3.5:4b";

    /// <summary>Температура переписывания запроса: низкая, чтобы формулировка не плавала.</summary>
    public double Temperature { get; init; } = 0.1;

    /// <summary>Сколько секунд ждать ответ Ollama.</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>Потолок токенов одного ответа модели на шаге переписывания.</summary>
    public int MaxOutputTokens { get; init; } = 128;

    /// <summary>Длиннее этого переписанный запрос отбрасывается и берётся исходный вопрос.</summary>
    public int MaxRewrittenCharacters { get; init; } = 400;

    /// <summary>
    /// Просит модель не рассуждать вслух. У Qwen 3 блоки <c>&lt;think&gt;</c> всё равно вырезаются из ответа.
    /// </summary>
    public bool DisableThinking { get; init; } = true;
}
