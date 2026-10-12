using Microsoft.Extensions.AI;
using StarTrek.Indexer.Embeddings;

namespace StarTrek.Mcp.Tests;

internal sealed class RecordingQueryEmbedder : IQueryEmbeddingGenerator
{
    public List<string> Queries { get; } = [];

    public Task<Embedding<float>> GenerateQueryAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        Queries.Add(text);
        return Task.FromResult(new Embedding<float>(new float[OnnxE5EmbeddingGenerator.Dimensions]));
    }
}
