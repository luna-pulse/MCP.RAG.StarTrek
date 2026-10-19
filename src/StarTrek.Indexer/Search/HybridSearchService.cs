using Microsoft.Extensions.Options;
using StarTrek.Indexer.Bm25;
using StarTrek.Indexer.Chroma;
using StarTrek.Indexer.Configuration;
using StarTrek.Indexer.Embeddings;

namespace StarTrek.Indexer.Search;

/// <summary>
/// Гибридный результат: чанк с RRF-оценкой и вкладом каждого источника.
/// VectorRank/Bm25Rank — ранг с 1 или null, если чанка нет в списке источника.
/// </summary>
public sealed record HybridSearchHit(
    string Id,
    double Score,
    int? VectorRank,
    double? VectorDistance,
    int? Bm25Rank,
    double? Bm25Score,
    string? Document,
    IReadOnlyDictionary<string, object?> Metadata);

public sealed record HybridSearchResult(
    string Query,
    IReadOnlyList<HybridSearchHit> Hits,
    int VectorCandidates,
    int Bm25Candidates);

/// <summary>
/// Поиск для этапа запроса. Его вызывают Corrective RAG и инструмент find_relevant_docs:
/// запрос → эмбеддинг с префиксом query: → top-N из Chroma → top-N из BM25 → слияние RRF.
/// Консольной команды у него нет: это внутренний API.
/// </summary>
public sealed class HybridSearchService(
    ChromaHttpClient chroma,
    IQueryEmbeddingGenerator queryEmbedder,
    Bm25IndexHolder bm25,
    IOptions<IndexerOptions> options) : IHybridSearch
{
    public async Task<HybridSearchResult> SearchAsync(
        string query,
        int? resultCount = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var settings = options.Value;
        var search = settings.Search;
        var take = resultCount ?? search.ResultCount;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);

        var index = bm25.GetOrLoad(settings.Chroma.Collection);
        var collection = await chroma.TryGetCollectionAsync(settings.Chroma.Collection, cancellationToken) ??
            throw new InvalidOperationException(
                $"Коллекция {settings.Chroma.Collection} не найдена в ChromaDB. Сначала выполните команду embed.");

        var embedding = await queryEmbedder.GenerateQueryAsync(query, cancellationToken);
        var neighbours = await chroma.QueryAsync(
            collection.Id,
            embedding.Vector.ToArray(),
            search.VectorTopN,
            cancellationToken);
        var lexical = index.Search(query, search.Bm25TopN);

        var fused = ReciprocalRankFusion.Fuse(
            neighbours.Select(hit => hit.Id).ToList(),
            lexical.Select(hit => hit.Id).ToList(),
            search.RrfK);

        var neighbourById = neighbours.ToDictionary(hit => hit.Id, StringComparer.Ordinal);
        var lexicalById = lexical.ToDictionary(hit => hit.Id, StringComparer.Ordinal);
        var records = neighbours
            .ToDictionary(
                hit => hit.Id,
                hit => new ChromaDocumentRecord(hit.Id, hit.Document, hit.Metadata),
                StringComparer.Ordinal);

        // BM25-чанки, которых нет среди соседей Chroma, дочитываются по id одним запросом.
        // Id из старого корпуса, которых уже нет в коллекции, отбрасываются.
        var missing = fused.Select(entry => entry.Id).Where(id => !records.ContainsKey(id)).ToList();
        var stale = new HashSet<string>(StringComparer.Ordinal);
        if (missing.Count > 0)
        {
            foreach (var record in await chroma.GetByIdsAsync(collection.Id, missing, cancellationToken))
            {
                records[record.Id] = record;
            }

            stale.UnionWith(missing.Where(id => !records.ContainsKey(id)));
        }

        var hits = fused
            .Where(entry => !stale.Contains(entry.Id))
            .Take(take)
            .Select(entry =>
            {
                var record = records[entry.Id];
                return new HybridSearchHit(
                    entry.Id,
                    entry.Score,
                    entry.Ranks[0],
                    neighbourById.TryGetValue(entry.Id, out var vector) ? vector.Distance : null,
                    entry.Ranks[1],
                    lexicalById.TryGetValue(entry.Id, out var lexicalHit) ? lexicalHit.Score : null,
                    record.Document,
                    record.Metadata);
            })
            .ToList();
        return new HybridSearchResult(query, hits, neighbours.Count, lexical.Count);
    }
}
