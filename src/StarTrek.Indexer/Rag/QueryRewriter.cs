using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StarTrek.Indexer.Configuration;

namespace StarTrek.Indexer.Rag;

public sealed class QueryRewriter(
    IChatClient chat,
    IOptions<LlmOptions> options,
    ILogger<QueryRewriter> logger) : IQueryRewriter
{
    private const string SystemPrompt =
        """
        You rewrite one user question into a single concise search query for a Russian Star Trek knowledge base.
        Keep characters, ships, registry numbers, episodes, places, and domain terms exactly as written.
        Drop greetings, politeness, and words that do not help search. Do not answer the question.
        The question may be Russian or English. Keep the query in that same language.
        Return only the query: no quotes, no label such as "Query:" or "Запрос:", and no reasoning.
        """;

    public async Task<string> RewriteAsync(string question, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(question);
        if (string.IsNullOrWhiteSpace(question))
        {
            logger.LogWarning("Вопрос пуст. Переписывание пропущено, в поиск уйдёт исходная строка.");
            return question;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var original = question.Trim();
        var settings = options.Value;

        try
        {
            var response = await chat.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, SystemPrompt),
                    new ChatMessage(ChatRole.User, original)
                ],
                LlmChat.CreateOptions(settings),
                cancellationToken);
            var rewritten = LlmChat.ReadAnswer(response);
            if (string.IsNullOrWhiteSpace(rewritten))
            {
                logger.LogWarning("Модель вернула пустой поисковый запрос. Используется исходный вопрос.");
                return original;
            }

            if (rewritten.Length > settings.MaxRewrittenCharacters)
            {
                logger.LogWarning(
                    "Переписанный запрос длиннее {MaxRewrittenCharacters} символов. Используется исходный вопрос.",
                    settings.MaxRewrittenCharacters);
                return original;
            }

            logger.LogDebug("Поисковый запрос после переписывания: {Query}", rewritten);
            return rewritten;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Ollama не переписала запрос. Используется исходный вопрос.");
            return original;
        }
    }
}

internal static partial class QueryRewriteText
{
    private static readonly string[] Prefixes =
    [
        "broadened query:",
        "search query:",
        "rewritten query:",
        "расширенный запрос:",
        "поисковый запрос:",
        "переписанный запрос:",
        "query:",
        "запрос:"
    ];

    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var text = ThinkBlock().Replace(raw, string.Empty);
        var open = ThinkOpen().Match(text);
        if (open.Success)
        {
            text = text[..open.Index];
        }

        text = ThinkClose().Replace(text, string.Empty).Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var line = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        return StripDecorations(line);
    }

    private static string StripDecorations(string value)
    {
        var current = value.Trim();
        for (var pass = 0; pass < 4; pass++)
        {
            var next = RemovePrefix(current.Trim().Trim('"', '\'', '«', '»', '`', '“', '”', '„'));
            if (next == current)
            {
                return current.Trim();
            }

            current = next.Trim();
        }

        return current.Trim();
    }

    private static string RemovePrefix(string value)
    {
        foreach (var prefix in Prefixes)
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return value[prefix.Length..].Trim();
            }
        }

        return value;
    }

    [GeneratedRegex("<think\\b[^>]*>.*?</think>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ThinkBlock();

    [GeneratedRegex("<think\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ThinkOpen();

    [GeneratedRegex("</think>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ThinkClose();
}
