using StarTrek.Indexer.Embeddings;

namespace StarTrek.Indexer.Tests;

public sealed class EmbeddingMathTests
{
    [Fact]
    public void MeanPoolingIgnoresPaddingAndNormalizesResult()
    {
        float[] hiddenStates =
        [
            1, 0,
            3, 0,
            100, 100
        ];
        long[] attentionMask = [1, 1, 0];

        var result = EmbeddingMath.MeanPoolAndNormalize(
            hiddenStates,
            attentionMask,
            batchSize: 1,
            sequenceLength: 3,
            hiddenSize: 2);

        Assert.Single(result);
        Assert.Equal(1f, result[0][0], precision: 6);
        Assert.Equal(0f, result[0][1], precision: 6);
    }

    [Fact]
    public void L2NormalizationProducesUnitVector()
    {
        var result = EmbeddingMath.L2Normalize([3, 4]);

        Assert.Equal(0.6f, result[0], precision: 6);
        Assert.Equal(0.8f, result[1], precision: 6);
        Assert.Equal(1d, Math.Sqrt(result.Sum(value => value * value)), precision: 6);
    }

    [Fact]
    public void RejectsMaskWithoutTokens()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => EmbeddingMath.MeanPoolAndNormalize(
                [1f, 2f],
                [0L],
                batchSize: 1,
                sequenceLength: 1,
                hiddenSize: 2));

        Assert.Contains("не содержит ни одного токена", exception.Message);
    }
}
