using StarTrek.Indexer.Search;

namespace StarTrek.Indexer.Rag;

/// <summary>
/// Решает, какие чанки ответа считать релевантными вопросу.
/// Сейчас это порог оценки RRF. Оценка моделью по каждому чанку подставляется той же регистрацией.
/// </summary>
public interface IChunkRelevanceEvaluator
{
    Task<IReadOnlyList<HybridSearchHit>> SelectRelevantAsync(
        string question,
        string searchQuery,
        IReadOnlyList<HybridSearchHit> hits,
        CancellationToken cancellationToken = default);
}
