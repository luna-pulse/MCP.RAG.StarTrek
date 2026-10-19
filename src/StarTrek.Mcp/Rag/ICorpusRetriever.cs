using StarTrek.Indexer.Search;

namespace StarTrek.Mcp.Rag;

public interface ICorpusRetriever
{
    Task<HybridSearchResult> SearchAsync(string query, int resultCount, CancellationToken cancellationToken);
}
