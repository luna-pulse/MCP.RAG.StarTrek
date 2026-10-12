using Microsoft.Extensions.AI;

namespace StarTrek.Indexer.Embeddings;

public sealed class LazyQueryEmbeddingGenerator(Func<IQueryEmbeddingGenerator> factory) :
    IQueryEmbeddingGenerator,
    IDisposable
{
    private readonly object gate = new();
    private IQueryEmbeddingGenerator? generator;
    private bool disposed;

    public Task<Embedding<float>> GenerateQueryAsync(string text, CancellationToken cancellationToken = default)
    {
        return Resolve().GenerateQueryAsync(text, cancellationToken);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (generator is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private IQueryEmbeddingGenerator Resolve()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return generator ??= factory();
        }
    }
}
