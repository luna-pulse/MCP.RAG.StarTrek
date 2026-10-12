using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StarTrek.Indexer.Chunking;
using StarTrek.Indexer.Configuration;
using StarTrek.Indexer.Tokenization;

namespace StarTrek.Indexer.Tests;

internal static class TestEnvironment
{
    private static readonly Lazy<E5Tokenizer> SharedTokenizer = new(() => new E5Tokenizer(TokenizerPath));

    public static string RepositoryRoot { get; } = RepositoryPathResolver.FindRepositoryRoot(
        Environment.CurrentDirectory,
        AppContext.BaseDirectory);

    public static string TokenizerPath =>
        Path.Combine(RepositoryRoot, "Models", "multilingual-e5-small", "tokenizer.json");

    public static string EmbeddingModelPath =>
        Path.Combine(RepositoryRoot, "Models", "multilingual-e5-small", "model.onnx");

    public static E5Tokenizer Tokenizer => SharedTokenizer.Value;

    public static ChunkingOptions ChunkingOptions { get; } = new()
    {
        TargetTokenLimit = 480,
        ShortSectionThreshold = 80,
        EmbeddingMaxTokens = 512,
        OverlapMaxSentences = 2,
        OverlapMaxTokens = 120,
        TokenWindowOverlapTokens = 32
    };

    public static DocumentChunker CreateChunker() => new(Tokenizer, ChunkingOptions);

    public static ServiceProvider BuildIndexerServices(IDictionary<string, string?>? overrides = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .AddInMemoryCollection(overrides ?? new Dictionary<string, string?>())
            .Build();
        return new ServiceCollection()
            .AddStarTrekIndexer(configuration, RepositoryRoot)
            .BuildServiceProvider();
    }
}
