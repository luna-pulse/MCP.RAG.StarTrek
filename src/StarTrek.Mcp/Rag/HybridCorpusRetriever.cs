using StarTrek.Indexer.Search;

namespace StarTrek.Mcp.Rag;

public sealed class HybridCorpusRetriever(HybridSearchService search) : ICorpusRetriever
{
    public Task<HybridSearchResult> SearchAsync(string query, int resultCount, CancellationToken cancellationToken) =>
        search.SearchAsync(query, resultCount, cancellationToken);
}
