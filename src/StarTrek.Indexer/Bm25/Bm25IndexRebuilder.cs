namespace StarTrek.Indexer.Bm25;

/// <summary>
/// Хук команды embed. Строит индекс из списка чанков, который передал embed (без чтения Chroma),
/// и сохраняет корпус на диск. Если коллекция не менялась и корпус на диске актуален
/// (тот же формат, токенизатор, коллекция и отпечаток чанков), берёт корпус с диска без повторной токенизации.
/// </summary>
public sealed class Bm25IndexRebuilder(Bm25IndexHolder holder) : IBm25IndexRebuilder
{
    public Task<Bm25RebuildResult> RebuildAsync(
        Bm25RebuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Chunks);
        cancellationToken.ThrowIfCancellationRequested();

        if (!request.CollectionChanged && TryLoadCurrent(request) is { } stored)
        {
            var loaded = holder.Build(stored);
            return Task.FromResult(new Bm25RebuildResult(
                Rebuilt: false,
                IndexedChunks: loaded.DocumentCount,
                LoadedFromDisk: true));
        }

        var corpus = Bm25CorpusStore.Create(request.Collection, request.Chunks);
        holder.Store.Save(corpus);
        var index = holder.Build(corpus);
        return Task.FromResult(new Bm25RebuildResult(Rebuilt: true, IndexedChunks: index.DocumentCount));
    }

    private Bm25Corpus? TryLoadCurrent(Bm25RebuildRequest request)
    {
        if (!holder.Store.Exists)
        {
            return null;
        }

        Bm25Corpus corpus;
        try
        {
            corpus = holder.Store.Load();
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            return null;
        }

        return corpus.IsCurrentFormat &&
            corpus.Collection.Equals(request.Collection, StringComparison.Ordinal) &&
            corpus.Chunks.Count == request.Chunks.Count &&
            corpus.Fingerprint.Equals(Bm25CorpusStore.ComputeFingerprint(request.Chunks), StringComparison.Ordinal)
                ? corpus
                : null;
    }
}
