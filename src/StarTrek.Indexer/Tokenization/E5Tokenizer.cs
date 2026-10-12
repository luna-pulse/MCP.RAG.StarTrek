using Tokenizers.HuggingFace.Tokenizer;

namespace StarTrek.Indexer.Tokenization;

public sealed record E5TokenEncoding(
    IReadOnlyList<long> InputIds,
    IReadOnlyList<long> AttentionMask);

public sealed class E5Tokenizer : IDisposable
{
    public const int StartTokenId = 0;
    public const int PaddingTokenId = 1;
    public const int EndTokenId = 2;
    public const int UnknownTokenId = 3;
    public const int ModelMaximumLength = 512;
    public const string PassagePrefix = "passage: ";
    public const string QueryPrefix = "query: ";
    public const string DownloadUrl =
        "https://huggingface.co/intfloat/multilingual-e5-small/resolve/main/tokenizer.json";

    private readonly object syncRoot = new();
    private readonly Tokenizer tokenizer;
    private bool disposed;

    public E5Tokenizer(string tokenizerPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenizerPath);
        if (!File.Exists(tokenizerPath))
        {
            throw new FileNotFoundException(
                $"Не найден tokenizer.json: {tokenizerPath}{Environment.NewLine}" +
                $"Скачайте его из {DownloadUrl}",
                tokenizerPath);
        }

        tokenizer = Tokenizer.FromFile(tokenizerPath);
    }

    public E5TokenEncoding Encode(
        string text,
        bool addSpecialTokens = true,
        int? maxLength = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(text);
        if (maxLength is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength));
        }

        long[] ids;
        lock (syncRoot)
        {
            ids = tokenizer
                .Encode(text, addSpecialTokens)
                .First()
                .Ids
                .Select(id => (long)id)
                .ToArray();
        }

        if (maxLength is { } limit && ids.Length > limit)
        {
            ids = Truncate(ids, limit, addSpecialTokens);
        }

        return new E5TokenEncoding(ids, Enumerable.Repeat(1L, ids.Length).ToArray());
    }

    public E5TokenEncoding EncodePassage(string text, int maxLength = ModelMaximumLength) =>
        Encode(PassagePrefix + text, addSpecialTokens: true, maxLength);

    public E5TokenEncoding EncodeQuery(string text, int maxLength = ModelMaximumLength) =>
        Encode(QueryPrefix + text, addSpecialTokens: true, maxLength);

    public int CountPassageTokens(string text) =>
        CountTokens(PassagePrefix + text, addSpecialTokens: true);

    public int CountTokens(string text, bool addSpecialTokens = true) =>
        Encode(text, addSpecialTokens).InputIds.Count;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        tokenizer.Dispose();
        disposed = true;
    }

    private static long[] Truncate(IReadOnlyList<long> ids, int maxLength, bool addSpecialTokens)
    {
        if (!addSpecialTokens)
        {
            return ids.Take(maxLength).ToArray();
        }

        if (maxLength < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxLength),
                "Для кодирования со специальными токенами maxLength должен быть не меньше 2.");
        }

        var result = ids.Take(maxLength).ToArray();
        result[^1] = EndTokenId;
        return result;
    }
}
