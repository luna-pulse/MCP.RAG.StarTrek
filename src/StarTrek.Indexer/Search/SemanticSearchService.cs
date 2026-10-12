using Microsoft.Extensions.Options;
using StarTrek.Indexer.Chroma;
using StarTrek.Indexer.Configuration;
using StarTrek.Indexer.Embeddings;

namespace StarTrek.Indexer.Search;

public sealed class SemanticSearchService(
    ChromaHttpClient chroma,
    IQueryEmbeddingGenerator queryEmbedder,
    IOptions<IndexerOptions> options)
{
    public async Task<ChunkSearchResponse> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var embedding = await queryEmbedder.GenerateQueryAsync(query, cancellationToken);
        var collectionName = options.Value.Chroma.Collection;
        var collection = await chroma.TryGetCollectionAsync(collectionName, cancellationToken) ??
            throw SearchFailures.CollectionMissing(collectionName);
        var hits = await chroma.QueryAsync(
            collection.Id,
            embedding.Vector.ToArray(),
            limit,
            cancellationToken);
        return new ChunkSearchResponse
        {
            Query = query,
            Chunks = hits
                .Select(hit => SearchChunkMapper.FromDocument(
                    hit.Id,
                    hit.Document,
                    hit.Metadata,
                    distance: hit.Distance))
                .ToList()
        };
    }
}
