using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using StarTrek.Indexer.Embeddings;
using StarTrek.Indexer.Tokenization;

namespace StarTrek.Indexer.Tests;

public sealed class OnnxE5EmbeddingGeneratorTests(OnnxE5Fixture fixture) :
    IClassFixture<OnnxE5Fixture>
{
    private const string Text =
        "Документ: Талосианцы\nТип сущности: раса\nРаздел: История\n\n" +
        "Талосианцы обладали сильными телепатическими способностями.";

    [OnnxModelFact]
    public async Task Produces384Dimensions()
    {
        var embedding = await RequireGenerator().GeneratePassageAsync(Text);

        Assert.Equal(OnnxE5EmbeddingGenerator.Dimensions, embedding.Vector.Length);
    }

    [OnnxModelFact]
    public async Task ProducesL2NormalizedVector()
    {
        var embedding = await RequireGenerator().GeneratePassageAsync(Text);
        var norm = Math.Sqrt(embedding.Vector.ToArray().Sum(value => value * value));

        Assert.Equal(1d, norm, precision: 5);
    }

    [OnnxModelFact]
    public async Task SameTextProducesSameVector()
    {
        var generator = RequireGenerator();
        var embeddings = await generator.GenerateAsync([Text, Text]);

        Assert.Equal(2, embeddings.Count);
        Assert.Equal(embeddings[0].Vector.ToArray(), embeddings[1].Vector.ToArray());
    }

    [OnnxModelFact]
    public async Task PassageAndQueryPrefixesProduceDifferentVectors()
    {
        var generator = RequireGenerator();

        var passage = await generator.GeneratePassageAsync("Талосианцы");
        var query = await generator.GenerateQueryAsync("Талосианцы");

        Assert.Contains(
            passage.Vector.ToArray().Zip(query.Vector.ToArray()),
            pair => Math.Abs(pair.First - pair.Second) > 0.000001f);
    }

    [OnnxModelFact]
    public void SemanticKernelResolvesRegisteredGenerator()
    {
        var expected = RequireGenerator();
        var registered =
            fixture.Kernel!.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        Assert.Same(
            expected,
            registered.GetService<OnnxE5EmbeddingGenerator>());
    }

    [OnnxModelFact]
    public async Task SemanticKernelGeneratorEmbedsTextAsPassage()
    {
        var expected = await RequireGenerator().GeneratePassageAsync(Text);
        var registered =
            fixture.Kernel!.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        var embeddings = await registered.GenerateAsync([Text]);

        var embedding = Assert.Single(embeddings);
        Assert.Equal(OnnxE5EmbeddingGenerator.ModelId, embedding.ModelId);
        Assert.Equal(expected.Vector.ToArray(), embedding.Vector.ToArray());
    }

    [OnnxModelFact]
    public async Task InputsBeyondOneBatchMatchSingleTextEmbeddings()
    {
        var generator = RequireGenerator();
        var texts = Enumerable
            .Range(1, OnnxE5EmbeddingGenerator.DefaultMaxBatchSize + 2)
            .Select(index => string.Join(' ', Enumerable.Repeat(Text, 1 + (index % 4))) + $" Запись {index}.")
            .ToArray();

        var batched = await generator.GenerateAsync(texts);

        Assert.Equal(texts.Length, batched.Count);
        for (var index = 0; index < texts.Length; index++)
        {
            var single = await generator.GeneratePassageAsync(texts[index]);
            Assert.All(
                single.Vector.ToArray().Zip(batched[index].Vector.ToArray()),
                pair => Assert.Equal(pair.First, pair.Second, 0.00001f));
        }
    }

    [OnnxModelFact]
    public async Task TextBeyondModelWindowIsTruncated()
    {
        var generator = RequireGenerator();
        var longText = string.Join(' ', Enumerable.Repeat("Талосианцы живут на планете Талос IV.", 200));

        var truncated = await generator.GeneratePassageAsync(longText);
        var withDifferentTail = await generator.GeneratePassageAsync(longText + " Совсем другой хвост.");

        Assert.True(TestEnvironment.Tokenizer.CountPassageTokens(longText) > E5Tokenizer.ModelMaximumLength);
        Assert.Equal(OnnxE5EmbeddingGenerator.Dimensions, truncated.Vector.Length);
        Assert.Equal(1d, Math.Sqrt(truncated.Vector.ToArray().Sum(value => value * value)), precision: 5);
        Assert.Equal(truncated.Vector.ToArray(), withDifferentTail.Vector.ToArray());
    }

    private OnnxE5EmbeddingGenerator RequireGenerator()
        => fixture.Generator ??
            throw new InvalidOperationException(
                $"Интеграционный тест запущен без файла {TestEnvironment.EmbeddingModelPath}.");
}

public sealed class OnnxE5Fixture : IDisposable
{
    private readonly ServiceProvider? serviceProvider;

    public OnnxE5EmbeddingGenerator? Generator { get; }
    public Kernel? Kernel { get; }

    public OnnxE5Fixture()
    {
        if (!File.Exists(TestEnvironment.EmbeddingModelPath))
        {
            return;
        }

        serviceProvider = TestEnvironment.BuildIndexerServices();
        Generator = serviceProvider.GetRequiredService<OnnxE5EmbeddingGenerator>();
        Kernel = serviceProvider.GetRequiredService<Kernel>();
    }

    public void Dispose() => serviceProvider?.Dispose();
}

public sealed class OnnxModelFactAttribute : FactAttribute
{
    public OnnxModelFactAttribute()
    {
        if (!File.Exists(TestEnvironment.EmbeddingModelPath))
        {
            Skip =
                $"Интеграционные ONNX-тесты пропущены: нет файла {TestEnvironment.EmbeddingModelPath}";
        }
    }
}
