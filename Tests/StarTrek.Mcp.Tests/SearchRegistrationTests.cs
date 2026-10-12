using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StarTrek.Indexer.Commands;
using StarTrek.Indexer.Configuration;
using StarTrek.Indexer.Embeddings;
using StarTrek.Indexer.Search;
using StarTrek.Indexer.Tokenization;

namespace StarTrek.Mcp.Tests;

public sealed class SearchRegistrationTests
{
    [Fact]
    public async Task FactoryRunsOnFirstQueryOnly()
    {
        var calls = 0;
        var lazy = new LazyQueryEmbeddingGenerator(() =>
        {
            calls++;
            return new RecordingQueryEmbedder();
        });
        Assert.Equal(0, calls);
        await lazy.GenerateQueryAsync("логика");
        await lazy.GenerateQueryAsync("логика");
        Assert.Equal(1, calls);
    }

    [Fact]
    public void RegistrationDoesNotConstructOnnxOrCommands()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(TestIndexerOptions.Create(Path.Combine(root, "missing.json")).AsMemoryKeys())
            .Build();
        var services = new ServiceCollection().AddStarTrekSearchRead(configuration, root);
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IQueryEmbeddingGenerator>());
        Assert.Null(provider.GetService<OnnxE5EmbeddingGenerator>());
        Assert.Null(provider.GetService<AnalyzeCommand>());
        Assert.Null(provider.GetService<ChunkCommand>());
        Assert.Null(provider.GetService<EmbedCommand>());
        Assert.Null(provider.GetService<CommandDispatcher>());
        Assert.NotNull(provider.GetRequiredService<LexicalSearchService>());
        Assert.NotNull(provider.GetRequiredService<SemanticSearchService>());
        Assert.NotNull(provider.GetRequiredService<HybridSearchService>());
    }

    [Fact]
    public void MissingModelMessageNamesTheDownload()
    {
        var tokenizerPath = Path.Combine(
            RepositoryPathResolver.FindRepositoryRoot(AppContext.BaseDirectory),
            "Models", "multilingual-e5-small", "tokenizer.json");
        using var tokenizer = new E5Tokenizer(tokenizerPath);
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "model.onnx");
        var error = Assert.Throws<FileNotFoundException>(() => new OnnxE5EmbeddingGenerator(missing, tokenizer));
        Assert.Contains("model.onnx", error.Message);
        Assert.Contains(OnnxE5EmbeddingGenerator.DownloadUrl, error.Message);
    }

    [Fact]
    public async Task RegisteredGeneratorOpensTokenizerOnFirstQuery()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var modelDirectory = Directory.CreateTempSubdirectory().FullName;
        var keys = TestIndexerOptions.Create(Path.Combine(root, "missing.json")).AsMemoryKeys();
        keys["Indexer:Paths:EmbeddingModel"] = modelDirectory;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(keys).Build();
        var services = new ServiceCollection().AddStarTrekSearchRead(configuration, root);
        using var provider = services.BuildServiceProvider();
        var generator = provider.GetRequiredService<IQueryEmbeddingGenerator>();

        var error = await Assert.ThrowsAsync<FileNotFoundException>(() => generator.GenerateQueryAsync("логика"));

        Assert.Contains("tokenizer.json", error.Message);
        Assert.Contains(E5Tokenizer.DownloadUrl, error.Message);
    }
}
