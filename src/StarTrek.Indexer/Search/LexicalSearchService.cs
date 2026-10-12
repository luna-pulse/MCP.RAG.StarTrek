using Microsoft.Extensions.Options;
using StarTrek.Indexer.Bm25;
using StarTrek.Indexer.Chroma;
using StarTrek.Indexer.Configuration;

namespace StarTrek.Indexer.Search;

public sealed class LexicalSearchService(
    Bm25IndexHolder bm25,
    ChromaHttpClient chroma,
    IOptions<IndexerOptions> options)
{
    public const string NoTokensMessage = "В запросе нет искомых слов.";

    public async Task<ChunkSearchResponse> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var index = bm25.GetOrLoad(options.Value.Chroma.Collection);
        if (Bm25Tokenizer.Tokenize(query).Count == 0)
        {
            throw new InvalidOperationException(NoTokensMessage);
        }

        var hits = index.Search(query, limit);
        var collectionName = options.Value.Chroma.Collection;
        var collection = await chroma.TryGetCollectionAsync(collectionName, cancellationToken) ??
            throw SearchFailures.CollectionMissing(collectionName);
        var records = await chroma.GetByIdsAsync(
            collection.Id,
            hits.Select(hit => hit.Id).ToArray(),
            cancellationToken);
        var byId = records.ToDictionary(record => record.Id, StringComparer.Ordinal);
        var chunks = new List<SearchChunk>(hits.Count);
        foreach (var hit in hits)
        {
            if (!byId.TryGetValue(hit.Id, out var record))
            {
                continue;
            }

            chunks.Add(SearchChunkMapper.FromDocument(
                hit.Id,
                record.Document,
                record.Metadata,
                bm25Score: hit.Score));
        }

        return new ChunkSearchResponse
        {
            Query = query,
            Chunks = chunks
        };
    }
}
