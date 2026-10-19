using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using StarTrek.Indexer.Configuration;
using StarTrek.Indexer.Rag;
using StarTrek.Indexer.Search;

namespace StarTrek.Mcp.Rag;

/// <summary>
/// Ответ поверх уже собранного в индексаторе Corrective RAG:
/// <see cref="CorrectiveRagRetrieval"/> переписывает вопрос, ищет и при необходимости расширяет запрос.
/// Релевантность берётся из <see cref="IChunkRelevanceEvaluator"/> (сейчас порог RRF).
/// Этот класс только пишет ответ по оставшимся чанкам тем же <see cref="IChatClient"/>.
/// </summary>
public sealed class AskQuestionService(
    CorrectiveRagRetrieval retrieval,
    IChunkRelevanceEvaluator relevance,
    IChatClient chat,
    IOptions<LlmOptions> llm)
{
    public const string NotFoundAnswer = "В базе знаний Star Trek об этом ничего не найдено.";

    private const string SystemPrompt =
        """
        Answer the question using only the passages from a Russian Star Trek knowledge base.
        Do not add facts that are not in the passages. If the passages are incomplete, say what they do not contain.
        Name the verifiable fact and cite the source file and position from the passage header.
        Answer in the language of the question. No reasoning.
        """;

    public async Task<AskQuestionResponse> AskAsync(string question, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        var found = await retrieval.RetrieveAsync(question, cancellationToken: cancellationToken);
        var relevant = await relevance.SelectRelevantAsync(
            question,
            found.SearchQuery,
            found.Chunks,
            cancellationToken);
        var usable = relevant.Where(hit => !string.IsNullOrWhiteSpace(hit.Document)).ToList();
        var meta = new AskQuestionMeta
        {
            RewrittenQuery = found.SearchQuery,
            Query = found.SearchQuery,
            Attempts = found.BroadenAttempts + 1
        };
        if (usable.Count == 0)
        {
            return new AskQuestionResponse
            {
                Answer = NotFoundAnswer,
                Sources = [],
                Meta = meta
            };
        }

        string answer;
        try
        {
            var response = await chat.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, SystemPrompt),
                    new ChatMessage(ChatRole.User, UserPrompt(question.Trim(), usable))
                ],
                CreateOptions(llm.Value),
                cancellationToken);
            answer = ReadAnswer(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw MapChatFailure(exception, llm.Value);
        }

        if (string.IsNullOrWhiteSpace(answer))
        {
            answer = NotFoundAnswer;
        }

        return new AskQuestionResponse
        {
            Answer = answer.Trim(),
            Sources = usable.Select(ToSource).ToArray(),
            Meta = meta
        };
    }

    public static string UnavailableMessage(string endpoint) =>
        $"Ollama не отвечает по адресу {endpoint.TrimEnd('/')}. Запустите её: docker compose up -d ollama.";

    public static string ModelMissingMessage(string model) =>
        $"Модель {model} не загружена в Ollama. Выполните ollama pull {model}.";

    private static string UserPrompt(string question, IReadOnlyList<HybridSearchHit> hits)
    {
        var builder = new StringBuilder();
        builder.Append("Question: ").Append(question).Append("\n\nPassages:\n");
        for (var index = 0; index < hits.Count; index++)
        {
            var chunk = SearchChunkMapper.FromHybridHit(hits[index]);
            builder.Append('[').Append(index + 1).Append("] ");
            builder.Append(chunk.SourceFile ?? "файл не указан");
            if (!string.IsNullOrWhiteSpace(chunk.Position))
            {
                builder.Append(", ").Append(chunk.Position);
            }

            if (!string.IsNullOrWhiteSpace(chunk.SectionPath))
            {
                builder.Append(", ").Append(chunk.SectionPath);
            }

            builder.Append('\n').Append(hits[index].Document).Append("\n\n");
        }

        return builder.ToString().TrimEnd();
    }

    private static AskQuestionSource ToSource(HybridSearchHit hit)
    {
        var chunk = SearchChunkMapper.FromHybridHit(hit);
        return new AskQuestionSource
        {
            Id = chunk.Id,
            SourceFile = chunk.SourceFile,
            Position = chunk.Position,
            ChunkIndex = chunk.ChunkIndex,
            Title = chunk.Title,
            SectionPath = chunk.SectionPath
        };
    }

    private static ChatOptions CreateOptions(LlmOptions settings)
    {
        var chatOptions = new ChatOptions
        {
            ModelId = settings.Model,
            Temperature = (float)settings.Temperature,
            MaxOutputTokens = settings.MaxOutputTokens
        };
        if (settings.DisableThinking)
        {
            chatOptions.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None };
        }

        return chatOptions;
    }

    private static string ReadAnswer(ChatResponse response)
    {
        var visible = new List<string>();
        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is TextReasoningContent)
                {
                    continue;
                }

                if (content is TextContent text && !string.IsNullOrWhiteSpace(text.Text))
                {
                    visible.Add(text.Text.Trim());
                }
            }
        }

        return visible.Count > 0 ? string.Join("\n", visible) : response.Text?.Trim() ?? string.Empty;
    }

    private static InvalidOperationException MapChatFailure(Exception exception, LlmOptions settings)
    {
        var text = exception.ToString();
        if (text.Contains("not found", StringComparison.OrdinalIgnoreCase) &&
            text.Contains("model", StringComparison.OrdinalIgnoreCase))
        {
            return new InvalidOperationException(ModelMissingMessage(settings.Model), exception);
        }

        if (IsUnavailable(exception))
        {
            return new InvalidOperationException(UnavailableMessage(settings.Endpoint), exception);
        }

        return new InvalidOperationException(
            $"Ollama не выполнила запрос к модели {settings.Model} по адресу {settings.Endpoint}: {exception.Message}",
            exception);
    }

    private static bool IsUnavailable(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException or TimeoutException or TaskCanceledException)
            {
                return true;
            }

            var text = current.Message;
            if (text.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("actively refused", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("No connection could be made", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("No route to host", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Unable to connect", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
