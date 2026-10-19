using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using StarTrek.Indexer.Configuration;

namespace StarTrek.Indexer.Search;

/// <summary>
/// Оркестратор гибридного поиска. Рабочий путь MCP (<c>find_relevant_docs</c> и Corrective RAG)
/// вызывает шаги только через <see cref="Kernel.InvokeAsync"/>:
/// <c>vector_candidates</c> → <c>lexical_candidates</c> → <c>fuse_ranks</c> → <c>assemble_hits</c>.
/// </summary>
public sealed class KernelHybridSearch(Kernel kernel, IOptions<IndexerOptions> options) : IHybridSearch
{
    public async Task<HybridSearchResult> SearchAsync(
        string query,
        int? resultCount = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var take = resultCount ?? options.Value.Search.ResultCount;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);

        var vector = await InvokeAsync<VectorCandidates>(
            SearchKernelNames.Vector,
            new KernelArguments { ["query"] = query },
            cancellationToken);
        var lexical = await InvokeAsync<LexicalCandidates>(
            SearchKernelNames.Lexical,
            new KernelArguments { ["query"] = query },
            cancellationToken);
        var fused = await InvokeAsync<IReadOnlyList<RrfEntry>>(
            SearchKernelNames.Fuse,
            new KernelArguments
            {
                ["vectorIds"] = vector.Hits.Select(hit => hit.Id).ToArray(),
                ["lexicalIds"] = lexical.Hits.Select(hit => hit.Id).ToArray()
            },
            cancellationToken);
        return await InvokeAsync<HybridSearchResult>(
            SearchKernelNames.Assemble,
            new KernelArguments
            {
                ["query"] = query,
                ["take"] = take,
                ["vector"] = vector,
                ["lexical"] = lexical,
                ["fused"] = fused
            },
            cancellationToken);
    }

    private async Task<T> InvokeAsync<T>(
        string functionName,
        KernelArguments arguments,
        CancellationToken cancellationToken)
        where T : class
    {
        var value = await kernel.InvokeAsync<T>(
            SearchKernelNames.Plugin,
            functionName,
            arguments,
            cancellationToken);
        return value ??
            throw new InvalidOperationException(
                $"Функция {SearchKernelNames.Plugin}.{functionName} не вернула результат.");
    }
}
