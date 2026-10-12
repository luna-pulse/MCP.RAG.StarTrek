namespace StarTrek.Indexer.Embeddings;

public static class EmbeddingMath
{
    public static float[][] MeanPoolAndNormalize(
        ReadOnlySpan<float> hiddenStates,
        ReadOnlySpan<long> attentionMask,
        int batchSize,
        int sequenceLength,
        int hiddenSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequenceLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hiddenSize);

        var expectedHiddenStateCount = checked(batchSize * sequenceLength * hiddenSize);
        var expectedMaskCount = checked(batchSize * sequenceLength);
        if (hiddenStates.Length != expectedHiddenStateCount)
        {
            throw new ArgumentException(
                $"Ожидалось {expectedHiddenStateCount} значений hidden state, получено {hiddenStates.Length}.",
                nameof(hiddenStates));
        }

        if (attentionMask.Length != expectedMaskCount)
        {
            throw new ArgumentException(
                $"Ожидалось {expectedMaskCount} значений attention mask, получено {attentionMask.Length}.",
                nameof(attentionMask));
        }

        var embeddings = new float[batchSize][];
        for (var batchIndex = 0; batchIndex < batchSize; batchIndex++)
        {
            var pooled = new float[hiddenSize];
            var tokenCount = 0L;
            for (var tokenIndex = 0; tokenIndex < sequenceLength; tokenIndex++)
            {
                var maskIndex = (batchIndex * sequenceLength) + tokenIndex;
                if (attentionMask[maskIndex] == 0)
                {
                    continue;
                }

                tokenCount++;
                var hiddenOffset = maskIndex * hiddenSize;
                for (var dimension = 0; dimension < hiddenSize; dimension++)
                {
                    pooled[dimension] += hiddenStates[hiddenOffset + dimension];
                }
            }

            if (tokenCount == 0)
            {
                throw new InvalidOperationException(
                    $"Attention mask для элемента батча {batchIndex} не содержит ни одного токена.");
            }

            var divisor = (float)tokenCount;
            for (var dimension = 0; dimension < pooled.Length; dimension++)
            {
                pooled[dimension] /= divisor;
            }

            NormalizeInPlace(pooled);
            embeddings[batchIndex] = pooled;
        }

        return embeddings;
    }

    public static float[] L2Normalize(ReadOnlySpan<float> values)
    {
        var result = values.ToArray();
        NormalizeInPlace(result);
        return result;
    }

    private static void NormalizeInPlace(Span<float> values)
    {
        double sumOfSquares = 0;
        foreach (var value in values)
        {
            sumOfSquares += value * value;
        }

        var norm = Math.Sqrt(sumOfSquares);
        if (norm <= 0 || !double.IsFinite(norm))
        {
            throw new InvalidOperationException(
                "Нельзя L2-нормализовать вектор с нулевой или некорректной нормой.");
        }

        var inverseNorm = (float)(1d / norm);
        for (var index = 0; index < values.Length; index++)
        {
            values[index] *= inverseNorm;
        }
    }
}
