using System.ComponentModel;
using Microsoft.SemanticKernel;

namespace StarTrek.Indexer.Search;

/// <summary>
/// Плагин Semantic Kernel <c>Search</c>. Функции — шаги гибридного поиска.
/// Их вызывает <see cref="KernelHybridSearch"/> через <c>kernel.InvokeAsync</c>.
/// </summary>
public sealed class SearchKernelPlugin(HybridSearchService search)
{
    [KernelFunction(SearchKernelNames.Vector)]
    [Description("Эмбеддинг запроса с префиксом query: и top-N соседей из ChromaDB.")]
    public Task<VectorCandidates> VectorCandidatesAsync(
        string query,
        CancellationToken cancellationToken = default) =>
        search.CollectVectorAsync(query, cancellationToken);

    [KernelFunction(SearchKernelNames.Lexical)]
    [Description("Top-N чанков Okapi BM25 по токенам запроса.")]
    public LexicalCandidates LexicalCandidates(string query) =>
        search.CollectLexical(query);

    [KernelFunction(SearchKernelNames.Fuse)]
    [Description("Сливает ранги векторного и лексического списков через Reciprocal Rank Fusion.")]
    public IReadOnlyList<RrfEntry> FuseRanks(
        IReadOnlyList<string> vectorIds,
        IReadOnlyList<string> lexicalIds) =>
        ReciprocalRankFusion.Fuse(vectorIds, lexicalIds, search.RrfK);

    [KernelFunction(SearchKernelNames.Assemble)]
    [Description("Собирает чанки гибридного ответа: оценка RRF, вклады каналов и текст из ChromaDB.")]
    public Task<HybridSearchResult> AssembleHitsAsync(
        string query,
        int take,
        VectorCandidates vector,
        LexicalCandidates lexical,
        IReadOnlyList<RrfEntry> fused,
        CancellationToken cancellationToken = default) =>
        search.AssembleAsync(query, take, vector, lexical, fused, cancellationToken);
}
