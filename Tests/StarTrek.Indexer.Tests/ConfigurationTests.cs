using Microsoft.Extensions.Configuration;
using StarTrek.Indexer.Configuration;

namespace StarTrek.Indexer.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void ResolvesConfiguredRelativePathsFromRepositoryRoot()
    {
        var root = RepositoryPathResolver.FindRepositoryRoot(
            Environment.CurrentDirectory,
            AppContext.BaseDirectory);
        var options = new PathOptions
        {
            Corpus = "SourceFiles",
            Staging = "Data/staging",
            EmbeddingModel = "models/multilingual-e5-small",
            Bm25Corpus = "Data/bm25/corpus.json"
        };

        var paths = RepositoryPathResolver.Resolve(root, options);

        Assert.Equal(Path.Combine(root, "SourceFiles"), paths.Corpus);
        Assert.Equal(Path.Combine(root, "Data", "staging"), paths.Staging);
        Assert.Equal(
            Path.Combine(root, "models", "multilingual-e5-small", "tokenizer.json"),
            paths.TokenizerFile);
        Assert.All(
            [paths.Corpus, paths.Staging, paths.EmbeddingModel, paths.Bm25Corpus],
            path => Assert.True(Path.IsPathFullyQualified(path)));
    }

    [Fact]
    public void LoadsRequiredValuesFromAppSettings()
    {
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var configuration = new ConfigurationBuilder().AddJsonFile(settingsPath).Build();
        var options = configuration.GetRequiredSection(IndexerOptions.SectionName).Get<IndexerOptions>();

        Assert.NotNull(options);
        Assert.Equal("SourceFiles", options.Paths.Corpus);
        Assert.Equal(480, options.Chunking.TargetTokenLimit);
        Assert.Equal(80, options.Chunking.ShortSectionThreshold);
        Assert.Equal("http://127.0.0.1:8000", options.Chroma.Endpoint);
        Assert.Equal("startrek", options.Chroma.Collection);
        Assert.Equal("default_tenant", options.Chroma.Tenant);
        Assert.Equal("default_database", options.Chroma.Database);
        Assert.Equal(1.5, options.Bm25.K1);
        Assert.Equal(0.75, options.Bm25.B);
        Assert.Equal(60, options.Search.RrfK);
        Assert.Equal(20, options.Search.VectorTopN);
        Assert.Equal(20, options.Search.Bm25TopN);
        Assert.Equal(10, options.Search.ResultCount);
    }
}
