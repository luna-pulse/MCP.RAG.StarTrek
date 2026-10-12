namespace StarTrek.Indexer.Bm25;

public interface IBm25IndexRebuilder
{
    Task<Bm25RebuildResult> RebuildAsync(
        Bm25RebuildRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record Bm25RebuildRequest(
    string Collection,
    IReadOnlyList<Bm25Chunk> Chunks,
    bool CollectionChanged);

public sealed record Bm25Chunk(string Id, string Text);

public sealed record Bm25RebuildResult(bool Rebuilt, int IndexedChunks, bool LoadedFromDisk = false);

public sealed class NoOpBm25IndexRebuilder : IBm25IndexRebuilder
{
    public Task<Bm25RebuildResult> RebuildAsync(
        Bm25RebuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(new Bm25RebuildResult(Rebuilt: false, IndexedChunks: 0));
    }
}
