using Microsoft.Extensions.Options;
using StarTrek.Indexer.Configuration;
using StarTrek.Indexer.Search;

namespace StarTrek.Indexer.Rag;

public sealed class RrfScoreRelevanceEvaluator(IOptions<RagOptions> options) : IChunkRelevanceEvaluator
{
    public Task<IReadOnlyList<HybridSearchHit>> SelectRelevantAsync(
        string question,
        string searchQuery,
        IReadOnlyList<HybridSearchHit> hits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hits);
        var minimum = options.Value.MinRrfScore;
        IReadOnlyList<HybridSearchHit> relevant = hits.Where(hit => hit.Score >= minimum).ToArray();
        return Task.FromResult(relevant);
    }
}
