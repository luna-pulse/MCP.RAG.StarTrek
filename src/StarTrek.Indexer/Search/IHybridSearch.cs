namespace StarTrek.Indexer.Search;

/// <summary>Гибридный поиск, который вызывает шаг Corrective RAG. Реализация — <see cref="HybridSearchService"/>.</summary>
public interface IHybridSearch
{
    Task<HybridSearchResult> SearchAsync(
        string query,
        int? resultCount = null,
        CancellationToken cancellationToken = default);
}
