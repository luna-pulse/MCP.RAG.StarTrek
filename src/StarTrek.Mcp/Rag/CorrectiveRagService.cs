using System.Text;
using Microsoft.Extensions.Options;
using StarTrek.Indexer.Search;

namespace StarTrek.Mcp.Rag;

/// <summary>
/// Corrective RAG: Rewrite → Retrieve → Grade, затем Generate либо Broaden и повтор.
/// Цикл явный: у графа один условный повтор, его и проверяют тесты.
/// Шаги Rewrite, Grade, Broaden и Generate — функции Semantic Kernel.
/// </summary>
public sealed class CorrectiveRagService(ICorpusRetriever retriever, IRagChat chat, IOptions<RagOptions> options)
{
    public const string NotFoundAnswer = "В базе знаний Star Trek об этом ничего не найдено.";

    public async Task<AskQuestionResponse> AskAsync(string question, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        var settings = RequireSettings();

        var rewritten = FirstLine(await chat.InvokeAsync(
            PromptCatalog.Rewrite,
            Variables(("question", question.Trim())),
            cancellationToken));
        if (string.IsNullOrWhiteSpace(rewritten))
        {
            rewritten = question.Trim();
        }

        var query = rewritten;
        var attempts = 0;
        var broadens = 0;
        List<HybridSearchHit> relevant = [];

        while (true)
        {
            attempts++;
            var found = await retriever.SearchAsync(query, settings.RetrieveCount, cancellationToken);
            relevant = await SelectRelevantAsync(question, found.Hits, cancellationToken);
            if (relevant.Count >= settings.MinRelevantChunks || broadens >= settings.MaxBroadenAttempts)
            {
                break;
            }

            var broadened = FirstLine(await chat.InvokeAsync(
                PromptCatalog.Broaden,
                Variables(("question", question.Trim()), ("query", query)),
                cancellationToken));
            broadens++;
            if (string.IsNullOrWhiteSpace(broadened))
            {
                break;
            }

            query = broadened;
        }

        var meta = new AskQuestionMeta
        {
            RewrittenQuery = rewritten,
            Query = query,
            Attempts = attempts
        };

        if (relevant.Count == 0)
        {
            return new AskQuestionResponse
            {
                Answer = NotFoundAnswer,
                Sources = [],
                Meta = meta
            };
        }

        var answer = (await chat.InvokeAsync(
            PromptCatalog.Generate,
            Variables(("question", question.Trim()), ("context", FormatContext(relevant))),
            cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(answer))
        {
            answer = NotFoundAnswer;
        }

        return new AskQuestionResponse
        {
            Answer = answer,
            Sources = relevant.Select(ToSource).ToArray(),
            Meta = meta
        };
    }

    private RagOptions RequireSettings()
    {
        var settings = options.Value;
        if (settings.MinRelevantChunks < 1 || settings.MaxBroadenAttempts < 0 || settings.RetrieveCount < 1)
        {
            throw new RagDependencyException(
                "В секции Rag задайте MinRelevantChunks >= 1, MaxBroadenAttempts >= 0 и RetrieveCount >= 1.");
        }

        return settings;
    }

    private async Task<List<HybridSearchHit>> SelectRelevantAsync(
        string question,
        IReadOnlyList<HybridSearchHit> hits,
        CancellationToken cancellationToken)
    {
        var relevant = new List<HybridSearchHit>();
        foreach (var hit in hits)
        {
            if (string.IsNullOrWhiteSpace(hit.Document))
            {
                continue;
            }

            var verdict = await chat.InvokeAsync(
                PromptCatalog.Grade,
                Variables(("question", question.Trim()), ("chunk", hit.Document)),
                cancellationToken);
            if (GradeVerdict.IsRelevant(verdict))
            {
                relevant.Add(hit);
            }
        }

        return relevant;
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

    private static string FormatContext(IReadOnlyList<HybridSearchHit> hits)
    {
        var builder = new StringBuilder();
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
                builder.Append(", раздел ").Append(chunk.SectionPath);
            }

            builder.Append('\n').Append(hits[index].Document).Append("\n\n");
        }

        return builder.ToString().TrimEnd();
    }

    internal static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var line = text.Trim();
        var breakAt = line.IndexOfAny(['\r', '\n']);
        if (breakAt >= 0)
        {
            line = line[..breakAt].Trim();
        }

        line = line.Trim('"', '\'', '«', '»', '`');
        const string prefix = "Запрос:";
        if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            line = line[prefix.Length..].Trim();
        }

        return line;
    }

    private static Dictionary<string, string> Variables(params (string Key, string Value)[] pairs)
    {
        var dictionary = new Dictionary<string, string>(pairs.Length, StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            dictionary[key] = value;
        }

        return dictionary;
    }
}
