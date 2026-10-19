using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using StarTrek.Indexer.Commands;
using StarTrek.Indexer.Configuration;
using StarTrek.Indexer.Embeddings;

namespace StarTrek.Indexer.Tests;

public sealed class EmbeddingModelValidationTests
{
    [Fact]
    public void MissingModelHasActionableError()
    {
        var missingPath = Path.Combine(
            Path.GetTempPath(),
            Guid.NewGuid().ToString("N"),
            "model.onnx");

        var exception = Assert.Throws<FileNotFoundException>(
            () => OnnxE5EmbeddingGenerator.EnsureModelExists(missingPath));

        Assert.Contains("model.onnx", exception.Message);
        Assert.Contains(missingPath, exception.Message);
        Assert.Contains(OnnxE5EmbeddingGenerator.DownloadUrl, exception.Message);
    }

    [Fact]
    public void RejectsNonPositiveBatchSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OnnxE5EmbeddingGenerator(
                TestEnvironment.EmbeddingModelPath,
                TestEnvironment.Tokenizer,
                maxBatchSize: 0));
    }

    [Fact]
    public void AppWiringTakesModelPathFromAppSettings()
    {
        using var services = TestEnvironment.BuildIndexerServices();

        var paths = services.GetRequiredService<RepositoryPaths>();

        Assert.Equal(TestEnvironment.EmbeddingModelPath, paths.EmbeddingModelFile);
    }

    [Fact]
    public void MissingModelFailsOnlyWhenEmbeddingGeneratorIsResolved()
    {
        var emptyModelDirectory = Directory.CreateTempSubdirectory("startrek-no-model-");
        try
        {
            using var services = TestEnvironment.BuildIndexerServices(new Dictionary<string, string?>
            {
                ["Indexer:Paths:EmbeddingModel"] = emptyModelDirectory.FullName
            });
            var expectedModelPath = Path.Combine(emptyModelDirectory.FullName, "model.onnx");

            Assert.NotNull(services.GetRequiredService<CommandDispatcher>());
            var exception = Assert.Throws<FileNotFoundException>(
                () => services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());

            Assert.Contains(expectedModelPath, exception.Message);
            Assert.Contains(OnnxE5EmbeddingGenerator.DownloadUrl, exception.Message);
        }
        finally
        {
            emptyModelDirectory.Delete(recursive: true);
        }
    }
}
