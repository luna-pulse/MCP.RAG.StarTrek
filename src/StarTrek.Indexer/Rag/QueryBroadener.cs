using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StarTrek.Indexer.Configuration;

namespace StarTrek.Indexer.Rag;

public sealed class QueryBroadener(
    IChatClient chat,
    IOptions<LlmOptions> options,
    ILogger<QueryBroadener> logger) : IQueryBroadener
{
    private const string SystemPrompt =
        """
        You broaden a search query that found too few passages in a Russian Star Trek knowledge base.
        Add synonyms, more generic terms, alternative names and spellings, and Russian or English variants of the same entities.
        Do not repeat the previous query unchanged. Do not answer the question.
        The question may be Russian or English. The new query may mix both languages when that adds a real variant.
        Return only the new query: no quotes, no label such as "Query:" or "Запрос:", and no reasoning.
        """;

    public async Task<string> BroadenAsync(
        string originalQuestion,
        string previousQuery,
        int attempt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(originalQuestion);
        ArgumentNullException.ThrowIfNull(previousQuery);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);
        if (string.IsNullOrWhiteSpace(previousQuery))
        {
            logger.LogWarning("Прежний запрос пуст. Расширение пропущено.");
            return string.IsNullOrWhiteSpace(originalQuestion) ? previousQuery : originalQuestion.Trim();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var previous = previousQuery.Trim();
        var settings = options.Value;
        var question = string.IsNullOrWhiteSpace(originalQuestion) ? previous : originalQuestion.Trim();

        try
        {
            var response = await chat.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, SystemPrompt),
                    new ChatMessage(ChatRole.User, UserPrompt(question, previous, attempt))
                ],
                LlmChat.CreateOptions(settings),
                cancellationToken);
            var broadened = LlmChat.ReadAnswer(response);
            if (string.IsNullOrWhiteSpace(broadened))
            {
                return Fallback(previous, "Модель вернула пустой расширенный запрос.");
            }

            if (broadened.Length > settings.MaxRewrittenCharacters)
            {
                return Fallback(previous, $"Расширенный запрос длиннее {settings.MaxRewrittenCharacters} символов.");
            }

            if (QueryIdentity.Same(broadened, previous))
            {
                return Fallback(previous, "Модель повторила прежний запрос.");
            }

            logger.LogDebug("Расширенный запрос: {Query}", broadened);
            return broadened;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Fallback(previous, "Ollama не расширила запрос.", exception);
        }
    }

    private string Fallback(string previous, string reason, Exception? exception = null)
    {
        var shortened = DeterministicQuery.Broaden(previous);
        if (QueryIdentity.Same(shortened, previous))
        {
            logger.LogWarning(exception, "{Reason} Остаётся предыдущий запрос.", reason);
            return previous;
        }

        logger.LogWarning(exception, "{Reason} Используется укороченный запрос {Query}.", reason, shortened);
        return shortened;
    }

    private static string UserPrompt(string question, string previous, int attempt) =>
        $"""
        Question: {question}
        Previous query: {previous}
        Attempt: {attempt}
        """;
}

internal static class QueryIdentity
{
    public static string Normalize(string value) =>
        string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static bool Same(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
}

internal static class DeterministicQuery
{
    public static string Broaden(string previousQuery)
    {
        var tokens = previousQuery.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length <= 1)
        {
            return previousQuery.Trim();
        }

        return string.Join(' ', tokens[..^1]);
    }
}
