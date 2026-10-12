using Microsoft.Extensions.AI;

namespace StarTrek.Indexer.Embeddings;

/// <summary>Эмбеддинг поискового запроса (для e5 — с префиксом «query: »).</summary>
public interface IQueryEmbeddingGenerator
{
    Task<Embedding<float>> GenerateQueryAsync(string text, CancellationToken cancellationToken = default);
}
