namespace StarTrek.Indexer.Bm25;

/// <summary>
/// Держит BM25-индекс в памяти процесса. Если индекс ещё не собран в этом процессе,
/// поднимает его из сохранённого корпуса на диске (без чтения Chroma).
/// </summary>
public sealed class Bm25IndexHolder(Bm25CorpusStore store, Bm25Parameters parameters)
{
    private readonly object gate = new();
    private Bm25Index? current;
    private string? currentCollection;

    public Bm25CorpusStore Store { get; } = store;
    public Bm25Parameters Parameters { get; } = parameters;

    public Bm25Index? Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    public Bm25Index Build(Bm25Corpus corpus)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        var index = Bm25Index.Create(corpus.ToDocuments(), Parameters);
        lock (gate)
        {
            current = index;
            currentCollection = corpus.Collection;
        }

        return index;
    }

    /// <summary>
    /// Индекс коллекции из памяти или из корпуса на диске. Нет корпуса, он устарел
    /// или собран для другой коллекции — понятная ошибка про embed.
    /// </summary>
    public Bm25Index GetOrLoad(string collection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        lock (gate)
        {
            if (current is not null && collection.Equals(currentCollection, StringComparison.Ordinal))
            {
                return current;
            }
        }

        Bm25Corpus corpus;
        try
        {
            corpus = Store.Load();
        }
        catch (FileNotFoundException exception)
        {
            throw new InvalidOperationException(
                $"Корпус BM25 не найден: {Store.Path}. Сначала выполните команду embed.",
                exception);
        }

        if (!corpus.IsCurrentFormat)
        {
            throw new InvalidOperationException(
                $"Корпус BM25 {Store.Path} собран по старым правилам (формат {corpus.FormatVersion}, " +
                $"токенизатор {corpus.TokenizerVersion}). Выполните команду embed.");
        }

        if (!corpus.Collection.Equals(collection, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Корпус BM25 {Store.Path} собран для коллекции {corpus.Collection}, " +
                $"а в настройках коллекция {collection}. Выполните команду embed.");
        }

        return Build(corpus);
    }
}
