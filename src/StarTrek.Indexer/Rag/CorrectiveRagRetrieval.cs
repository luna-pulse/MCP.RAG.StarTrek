using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StarTrek.Indexer.Configuration;
using StarTrek.Indexer.Search;

namespace StarTrek.Indexer.Rag;

/// <summary>
/// Вход Corrective RAG до генерации ответа:
/// вопрос → переписанный запрос → гибридный поиск → при нехватке релевантных чанков расширение запроса.
/// </summary>
public sealed record CorrectiveRetrieval(
    string Question,
    string SearchQuery,
    int BroadenAttempts,
    IReadOnlyList<HybridSearchHit> Chunks,
    HybridSearchResult Result);

public sealed class CorrectiveRagRetrieval(
    IQueryRewriter rewriter,
    IQueryBroadener broadener,
    IChunkRelevanceEvaluator relevance,
    IHybridSearch search,
    IOptions<RagOptions> options,
    ILogger<CorrectiveRagRetrieval> logger)
{
    public async Task<CorrectiveRetrieval> RetrieveAsync(
        string question,
        int? resultCount = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        var original = question.Trim();
        var settings = options.Value;
        var query = await rewriter.RewriteAsync(original, cancellationToken);
        if (string.IsNullOrWhiteSpace(query))
        {
            query = original;
        }

        var searched = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            QueryIdentity.Normalize(query)
        };
        var merged = new List<HybridSearchHit>();
        var result = await SearchAndMergeAsync(query, resultCount, merged, cancellationToken);
        var attempts = 0;
        if (await HasEnoughRelevantAsync(original, query, merged, settings.MinRelevantChunks, cancellationToken))
        {
            return new CorrectiveRetrieval(original, query, attempts, merged, result);
        }

        for (var attempt = 1; attempt <= settings.MaxBroadenAttempts; attempt++)
        {
            var broadened = await broadener.BroadenAsync(original, query, attempt, cancellationToken);
            if (string.IsNullOrWhiteSpace(broadened) || !searched.Add(QueryIdentity.Normalize(broadened)))
            {
                logger.LogWarning("Расширенный запрос совпал с уже искавшимся. Повтор поиска остановлен.");
                break;
            }

            query = broadened.Trim();
            attempts++;
            result = await SearchAndMergeAsync(query, resultCount, merged, cancellationToken);
            if (await HasEnoughRelevantAsync(original, query, merged, settings.MinRelevantChunks, cancellationToken))
            {
                break;
            }
        }

        return new CorrectiveRetrieval(original, query, attempts, merged, result);
    }

    private async Task<HybridSearchResult> SearchAndMergeAsync(
        string query,
        int? resultCount,
        List<HybridSearchHit> merged,
        CancellationToken cancellationToken)
    {
        var result = await search.SearchAsync(query, resultCount, cancellationToken);
        ReplaceWith(merged, ChunkMerge.Merge(merged, result.Hits));
        return result;
    }

    private async Task<bool> HasEnoughRelevantAsync(
        string question,
        string searchQuery,
        IReadOnlyList<HybridSearchHit> hits,
        int minimum,
        CancellationToken cancellationToken)
    {
        var relevant = await relevance.SelectRelevantAsync(question, searchQuery, hits, cancellationToken);
        return relevant.Count >= minimum;
    }

    private static void ReplaceWith(List<HybridSearchHit> target, List<HybridSearchHit> source)
    {
        target.Clear();
        target.AddRange(source);
    }
}

internal static class ChunkMerge
{
    public static List<HybridSearchHit> Merge(
        IReadOnlyList<HybridSearchHit> accumulated,
        IReadOnlyList<HybridSearchHit> incoming)
    {
        var byId = new Dictionary<string, HybridSearchHit>(StringComparer.Ordinal);
        foreach (var hit in accumulated)
        {
            byId[hit.Id] = hit;
        }

        foreach (var hit in incoming)
        {
            if (!byId.TryGetValue(hit.Id, out var existing) || hit.Score > existing.Score)
            {
                byId[hit.Id] = hit;
            }
        }

        return byId.Values
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Id, StringComparer.Ordinal)
            .ToList();
    }
}
