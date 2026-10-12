using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using StarTrek.Indexer.Tokenization;

namespace StarTrek.Indexer.Embeddings;

public sealed class OnnxE5EmbeddingGenerator :
    IEmbeddingGenerator<string, Embedding<float>>,
    IQueryEmbeddingGenerator,
    IDisposable
{
    public const string ModelId = "intfloat/multilingual-e5-small";
    public const int Dimensions = 384;
    public const string InputIdsName = "input_ids";
    public const string AttentionMaskName = "attention_mask";
    public const string TokenTypeIdsName = "token_type_ids";
    public const string OutputName = "last_hidden_state";
    public const int DefaultMaxBatchSize = 16;
    public const string DownloadUrl =
        "https://huggingface.co/intfloat/multilingual-e5-small/resolve/main/onnx/model.onnx";

    private readonly E5Tokenizer tokenizer;
    private readonly InferenceSession session;
    private readonly int maxBatchSize;
    private readonly EmbeddingGeneratorMetadata metadata =
        new("onnxruntime", providerUri: null, ModelId, Dimensions);
    private bool disposed;

    public OnnxE5EmbeddingGenerator(
        string modelPath,
        E5Tokenizer tokenizer,
        int maxBatchSize = DefaultMaxBatchSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBatchSize);
        EnsureModelExists(modelPath);

        this.tokenizer = tokenizer;
        this.maxBatchSize = maxBatchSize;
        try
        {
            using var sessionOptions = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };
            session = new InferenceSession(modelPath, sessionOptions);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InvalidOperationException(
                $"Не удалось загрузить ONNX-модель {ModelId} из файла: {modelPath}",
                exception);
        }

        try
        {
            ValidateModelContract(modelPath);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Generate(values, E5InputKind.Passage, options, cancellationToken));

    public Task<Embedding<float>> GeneratePassageAsync(
        string text,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            Generate([text], E5InputKind.Passage, options: null, cancellationToken)[0]);

    public Task<Embedding<float>> GenerateQueryAsync(
        string text,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            Generate([text], E5InputKind.Query, options: null, cancellationToken)[0]);

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        return serviceType == typeof(EmbeddingGeneratorMetadata) ? metadata : null;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        session.Dispose();
        disposed = true;
    }

    public static void EnsureModelExists(string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException(
                $"Не найден model.onnx для {ModelId}: {modelPath}{Environment.NewLine}" +
                $"Скачайте файл из {DownloadUrl} и сохраните по указанному пути.",
                modelPath);
        }
    }

    private GeneratedEmbeddings<Embedding<float>> Generate(
        IEnumerable<string> values,
        E5InputKind inputKind,
        EmbeddingGenerationOptions? options,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(values);
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();

        var texts = values.ToArray();
        var encodings = new E5TokenEncoding[texts.Length];
        for (var index = 0; index < texts.Length; index++)
        {
            if (texts[index] is null)
            {
                throw new ArgumentException(
                    $"Текст с индексом {index} не может быть null.",
                    nameof(values));
            }

            encodings[index] = inputKind == E5InputKind.Passage
                ? tokenizer.EncodePassage(texts[index])
                : tokenizer.EncodeQuery(texts[index]);
        }

        var embeddings = new GeneratedEmbeddings<Embedding<float>>(texts.Length);
        for (var offset = 0; offset < encodings.Length; offset += maxBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = encodings.AsSpan(offset, Math.Min(maxBatchSize, encodings.Length - offset));
            foreach (var vector in EmbedBatch(batch))
            {
                embeddings.Add(new Embedding<float>(vector) { ModelId = ModelId });
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return embeddings;
    }

    private float[][] EmbedBatch(ReadOnlySpan<E5TokenEncoding> encodings)
    {
        var batchSize = encodings.Length;
        var sequenceLength = 0;
        foreach (var encoding in encodings)
        {
            sequenceLength = Math.Max(sequenceLength, encoding.InputIds.Count);
        }

        var inputIds = new DenseTensor<long>([batchSize, sequenceLength]);
        var attentionMask = new DenseTensor<long>([batchSize, sequenceLength]);
        var tokenTypeIds = new DenseTensor<long>([batchSize, sequenceLength]);
        var flatAttentionMask = new long[batchSize * sequenceLength];
        for (var batchIndex = 0; batchIndex < batchSize; batchIndex++)
        {
            for (var tokenIndex = 0; tokenIndex < sequenceLength; tokenIndex++)
            {
                var hasToken = tokenIndex < encodings[batchIndex].InputIds.Count;
                inputIds[batchIndex, tokenIndex] = hasToken
                    ? encodings[batchIndex].InputIds[tokenIndex]
                    : E5Tokenizer.PaddingTokenId;
                var maskValue = hasToken ? 1L : 0L;
                attentionMask[batchIndex, tokenIndex] = maskValue;
                flatAttentionMask[(batchIndex * sequenceLength) + tokenIndex] = maskValue;
            }
        }

        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor(InputIdsName, inputIds),
            NamedOnnxValue.CreateFromTensor(AttentionMaskName, attentionMask),
            NamedOnnxValue.CreateFromTensor(TokenTypeIdsName, tokenTypeIds)
        };
        using var outputs = session.Run(inputs, [OutputName]);

        var output = outputs.Single(result => result.Name == OutputName).AsTensor<float>();
        var dimensions = output.Dimensions.ToArray();
        if (dimensions.Length != 3 ||
            dimensions[0] != batchSize ||
            dimensions[1] != sequenceLength ||
            dimensions[2] != Dimensions)
        {
            throw new InvalidDataException(
                $"Выход {OutputName} имеет форму [{string.Join(", ", dimensions)}], " +
                $"ожидалась [{batchSize}, {sequenceLength}, {Dimensions}].");
        }

        return EmbeddingMath.MeanPoolAndNormalize(
            output.ToArray(),
            flatAttentionMask,
            batchSize,
            sequenceLength,
            Dimensions);
    }

    private void ValidateModelContract(string modelPath)
    {
        var inputNames = session.InputMetadata.Keys.Order(StringComparer.Ordinal).ToArray();
        var outputNames = session.OutputMetadata.Keys.Order(StringComparer.Ordinal).ToArray();
        if (!session.InputMetadata.ContainsKey(InputIdsName) ||
            !session.InputMetadata.ContainsKey(AttentionMaskName) ||
            !session.InputMetadata.ContainsKey(TokenTypeIdsName) ||
            !session.OutputMetadata.ContainsKey(OutputName))
        {
            throw new InvalidDataException(
                $"ONNX-модель {modelPath} имеет несовместимый контракт. " +
                $"Входы: [{string.Join(", ", inputNames)}], выходы: [{string.Join(", ", outputNames)}]. " +
                $"Ожидались входы {InputIdsName}, {AttentionMaskName}, {TokenTypeIdsName} " +
                $"и выход {OutputName}.");
        }

        var outputDimensions = session.OutputMetadata[OutputName].Dimensions;
        if (outputDimensions.Length != 3 ||
            (outputDimensions[^1] > 0 && outputDimensions[^1] != Dimensions))
        {
            throw new InvalidDataException(
                $"Выход {OutputName} модели {modelPath} должен иметь форму [batch, tokens, {Dimensions}], " +
                $"получена [{string.Join(", ", outputDimensions)}].");
        }
    }

    private static void ValidateOptions(EmbeddingGenerationOptions? options)
    {
        if (options?.Dimensions is { } requestedDimensions &&
            requestedDimensions != Dimensions)
        {
            throw new NotSupportedException(
                $"{ModelId} выдаёт только {Dimensions} измерений, запрошено {requestedDimensions}.");
        }

        if (!string.IsNullOrWhiteSpace(options?.ModelId) &&
            !options.ModelId.Equals(ModelId, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"Локальный генератор обслуживает только модель {ModelId}, запрошена {options.ModelId}.");
        }
    }

    private enum E5InputKind
    {
        Passage,
        Query
    }
}
