using StarTrek.Indexer.Search;

namespace StarTrek.Indexer.Rag;

/// <summary>
/// Вход Corrective RAG до оценки чанков и генерации ответа:
/// вопрос → переписанный запрос → <see cref="HybridSearchService"/>.
/// </summary>
public sealed record CorrectiveRetrieval(
    string Question,
    string SearchQuery,
    HybridSearchResult Result);

public sealed class CorrectiveRagRetrieval(IQueryRewriter rewriter, HybridSearchService hybrid)
{
    public async Task<CorrectiveRetrieval> RetrieveAsync(
        string question,
        int? resultCount = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        var original = question.Trim();
        var query = await rewriter.RewriteAsync(original, cancellationToken);
        if (string.IsNullOrWhiteSpace(query))
        {
            query = original;
        }

        var result = await hybrid.SearchAsync(query, resultCount, cancellationToken);
        return new CorrectiveRetrieval(original, query, result);
    }
}
