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
/// Шаги гибридного поиска. На пути MCP их вызывает плагин Semantic Kernel <c>Search</c>
/// через <c>kernel.InvokeAsync</c>: <c>vector_candidates</c> (эмбеддинг с префиксом query:
/// и top-N из Chroma), <c>lexical_candidates</c> (top-N из BM25), <c>fuse_ranks</c>
/// (<see cref="ReciprocalRankFusion"/>) и <c>assemble_hits</c>.
/// Консольной команды у поиска нет.
/// </summary>
public sealed class HybridSearchService(
    ChromaHttpClient chroma,
    IQueryEmbeddingGenerator queryEmbedder,
    Bm25IndexHolder bm25,
    IOptions<IndexerOptions> options) : IHybridSearch
{
    public int RrfK => options.Value.Search.RrfK;

    public async Task<HybridSearchResult> SearchAsync(
        string query,
        int? resultCount = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var take = resultCount ?? options.Value.Search.ResultCount;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);

        var vector = await CollectVectorAsync(query, cancellationToken);
        var lexical = CollectLexical(query);
        var fused = ReciprocalRankFusion.Fuse(
            vector.Hits.Select(hit => hit.Id).ToArray(),
            lexical.Hits.Select(hit => hit.Id).ToArray(),
            RrfK);
        return await AssembleAsync(query, take, vector, lexical, fused, cancellationToken);
    }

    public async Task<VectorCandidates> CollectVectorAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var settings = options.Value;
        var collection = await chroma.TryGetCollectionAsync(settings.Chroma.Collection, cancellationToken) ??
            throw new InvalidOperationException(
                $"Коллекция {settings.Chroma.Collection} не найдена в ChromaDB. Сначала выполните команду embed.");
        var embedding = await queryEmbedder.GenerateQueryAsync(query, cancellationToken);
        var neighbours = await chroma.QueryAsync(
            collection.Id,
            embedding.Vector.ToArray(),
            settings.Search.VectorTopN,
            cancellationToken);
        return new VectorCandidates(
            collection.Id,
            neighbours
                .Select(hit => new VectorCandidate(hit.Id, hit.Distance, hit.Document, hit.Metadata))
                .ToList());
    }

    public LexicalCandidates CollectLexical(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var settings = options.Value;
        var index = bm25.GetOrLoad(settings.Chroma.Collection);
        return new LexicalCandidates(
            index.Search(query, settings.Search.Bm25TopN)
                .Select(hit => new LexicalCandidate(hit.Id, hit.Score))
                .ToList());
    }

    public async Task<HybridSearchResult> AssembleAsync(
        string query,
        int take,
        VectorCandidates vector,
        LexicalCandidates lexical,
        IReadOnlyList<RrfEntry> fused,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(vector);
        ArgumentNullException.ThrowIfNull(lexical);
        ArgumentNullException.ThrowIfNull(fused);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);

        var neighbourById = vector.Hits.ToDictionary(hit => hit.Id, StringComparer.Ordinal);
        var lexicalById = lexical.Hits.ToDictionary(hit => hit.Id, StringComparer.Ordinal);
        var records = vector.Hits.ToDictionary(
            hit => hit.Id,
            hit => new ChromaDocumentRecord(hit.Id, hit.Document, hit.Metadata),
            StringComparer.Ordinal);

        // BM25-чанки, которых нет среди соседей Chroma, дочитываются по id одним запросом.
        // Id из старого корпуса, которых уже нет в коллекции, отбрасываются.
        var missing = fused.Select(entry => entry.Id).Where(id => !records.ContainsKey(id)).ToList();
        var stale = new HashSet<string>(StringComparer.Ordinal);
        if (missing.Count > 0)
        {
            foreach (var record in await chroma.GetByIdsAsync(vector.CollectionId, missing, cancellationToken))
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
                    neighbourById.TryGetValue(entry.Id, out var neighbour) ? neighbour.Distance : null,
                    entry.Ranks[1],
                    lexicalById.TryGetValue(entry.Id, out var lexicalHit) ? lexicalHit.Score : null,
                    record.Document,
                    record.Metadata);
            })
            .ToList();
        return new HybridSearchResult(query, hits, vector.Hits.Count, lexical.Hits.Count);
    }
}
