using StarTrek.Indexer.Chroma;

namespace StarTrek.Indexer.Search;

public static class SearchChunkMapper
{
    public static SearchChunk FromDocument(
        string id,
        string? text,
        IReadOnlyDictionary<string, object?>? metadata,
        double? bm25Score = null,
        double? distance = null,
        double? rrfScore = null,
        int? vectorRank = null,
        int? bm25Rank = null) =>
        new()
        {
            Id = id,
            Text = text,
            Bm25Score = bm25Score,
            Distance = distance,
            RrfScore = rrfScore,
            VectorRank = vectorRank,
            Bm25Rank = bm25Rank,
            SourceFile = ReadString(metadata, ChromaMetadataKeys.SourceFile),
            Title = ReadString(metadata, ChromaMetadataKeys.Title),
            OriginalTitle = ReadString(metadata, ChromaMetadataKeys.OriginalTitle),
            EntityType = ReadString(metadata, ChromaMetadataKeys.EntityType),
            SectionPath = ReadString(metadata, ChromaMetadataKeys.SectionPath),
            Position = ReadString(metadata, ChromaMetadataKeys.Position),
            ChunkIndex = ReadChunkIndex(metadata)
        };

    public static SearchChunk FromHybridHit(HybridSearchHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        return FromDocument(
            hit.Id,
            hit.Document,
            hit.Metadata,
            bm25Score: hit.Bm25Score,
            distance: hit.VectorDistance,
            rrfScore: hit.Score,
            vectorRank: hit.VectorRank,
            bm25Rank: hit.Bm25Rank);
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value))
        {
            return null;
        }

        return value as string;
    }

    private static int? ReadChunkIndex(IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue(ChromaMetadataKeys.ChunkIndex, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            int index => index,
            long index when index is >= int.MinValue and <= int.MaxValue => (int)index,
            double index when IsWholeInt(index) => (int)index,
            _ => null
        };
    }

    private static bool IsWholeInt(double value) =>
        double.IsFinite(value) &&
        value == Math.Truncate(value) &&
        value is >= int.MinValue and <= int.MaxValue;
}
