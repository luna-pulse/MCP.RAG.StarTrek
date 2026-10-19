namespace StarTrek.Indexer.Search;

/// <summary>
/// Гибридный поиск для Corrective RAG и инструмента <c>find_relevant_docs</c>.
/// В регистрации MCP это <see cref="KernelHybridSearch"/>: шаги вызывает Semantic Kernel.
/// </summary>
public interface IHybridSearch
{
    Task<HybridSearchResult> SearchAsync(
        string query,
        int? resultCount = null,
        CancellationToken cancellationToken = default);
}
