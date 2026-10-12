using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using StarTrek.Indexer.Bm25;
using StarTrek.Indexer.Chunking;
using StarTrek.Indexer.Embeddings;

namespace StarTrek.Indexer.Tests;

internal sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public List<string> Inputs { get; } = [];

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var texts = values.ToList();
        Inputs.AddRange(texts);
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
            texts.Select(text => new Embedding<float>(CreateVector(text))
            {
                ModelId = OnnxE5EmbeddingGenerator.ModelId
            })));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    public static float[] CreateVector(string text)
    {
        var seed = BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        var random = new Random(seed);
        var vector = new float[OnnxE5EmbeddingGenerator.Dimensions];
        for (var index = 0; index < vector.Length; index++)
        {
            vector[index] = (float)(random.NextDouble() - 0.5);
        }

        var norm = MathF.Sqrt(vector.Sum(value => value * value));
        return vector.Select(value => value / norm).ToArray();
    }
}

internal sealed class RecordingBm25IndexRebuilder : IBm25IndexRebuilder
{
    public List<Bm25RebuildRequest> Requests { get; } = [];

    public Task<Bm25RebuildResult> RebuildAsync(
        Bm25RebuildRequest request,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return Task.FromResult(new Bm25RebuildResult(Rebuilt: false, IndexedChunks: 0));
    }
}

internal static class StagedDocuments
{
    public const string RaceFile = "SourceFiles/Races/Талосианцы.txt";
    public const string PlanetFile = "SourceFiles/Planets/теллар.txt";

    public static ChunkedDocument Race(int chunkCount = 2, string version = "v1") =>
        Create(RaceFile, "Талосианцы", originalTitle: null, "race", "раса", chunkCount, version);

    public static ChunkedDocument Planet(int chunkCount = 1, string version = "v1") =>
        Create(PlanetFile, "Теллар", "Tellar Prime", "planet", "планета", chunkCount, version);

    public static ChunkedDocument Create(
        string sourceFile,
        string title,
        string? originalTitle,
        string entityType,
        string entityLabel,
        int chunkCount,
        string version)
    {
        var chunks = Enumerable.Range(1, chunkCount)
            .Select(index =>
            {
                var body = $"{title}: раздел {index}, редакция {version}. Код NCC-1701-D остаётся одним токеном.";
                return new DocumentChunk(
                    index,
                    HeadingLevel: 4,
                    ["История", $"Часть {index}"],
                    $"Часть {index}",
                    $"lines {index * 10}-{index * 10 + 4}",
                    index * 10,
                    index * 10 + 4,
                    TokenCount: 40 + index,
                    body,
                    $"Документ: {title}\nТип сущности: {entityLabel}\nРаздел: История > Часть {index}\n\n{body}");
            })
            .ToList();
        return new ChunkedDocument(
            DocumentIdentity.ComputeDocumentId(sourceFile),
            sourceFile,
            DocumentIdentity.ComputeContentHash($"{sourceFile}|{version}|{chunkCount}"),
            title,
            originalTitle,
            entityType,
            "Star Trek",
            "ru",
            "txt",
            chunks);
    }
}

internal static class ClosedPort
{
    public static Uri CreateEndpoint()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new Uri($"http://127.0.0.1:{port}");
    }
}

internal sealed class FakeQueryEmbedder : IQueryEmbeddingGenerator
{
    private readonly Dictionary<string, string> mapping = new(StringComparer.Ordinal);

    public List<string> Queries { get; } = [];

    public void Map(string query, string passageText) => mapping[query] = passageText;

    public Task<Embedding<float>> GenerateQueryAsync(string text, CancellationToken cancellationToken = default)
    {
        Queries.Add(text);
        return Task.FromResult(new Embedding<float>(
            FakeEmbeddingGenerator.CreateVector(mapping.GetValueOrDefault(text, text))));
    }
}
