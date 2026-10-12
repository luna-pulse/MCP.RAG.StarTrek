namespace StarTrek.Indexer.Bm25;

public sealed record Bm25Parameters(double K1 = Bm25Parameters.DefaultK1, double B = Bm25Parameters.DefaultB)
{
    public const double DefaultK1 = 1.5;
    public const double DefaultB = 0.75;

    public static Bm25Parameters Default { get; } = new();

    public void Validate()
    {
        if (!double.IsFinite(K1) || K1 < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(K1), K1, "k1 должен быть неотрицательным числом.");
        }

        if (!double.IsFinite(B) || B is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(B), B, "b должен лежать в диапазоне [0, 1].");
        }
    }
}

/// <summary>Документ корпуса BM25: id чанка и его токены (с повторами).</summary>
public sealed record Bm25Document(string Id, IReadOnlyList<string> Tokens);

public sealed record Bm25Hit(string Id, double Score);

/// <summary>
/// Okapi BM25 в памяти процесса.
/// score(D, Q) = Σ idf(t) · tf · (k1 + 1) / (tf + k1 · (1 − b + b · |D| / avgdl)),
/// idf(t) = ln(1 + (N − n_t + 0.5) / (n_t + 0.5)), где N — число чанков, n_t — число чанков с термином t.
/// Каждый уникальный термин запроса учитывается один раз.
/// </summary>
public sealed class Bm25Index
{
    private readonly string[] ids;
    private readonly int[] lengths;
    private readonly Dictionary<string, Posting[]> postings;
    private readonly double averageLength;

    private Bm25Index(
        string[] ids,
        int[] lengths,
        Dictionary<string, Posting[]> postings,
        double averageLength,
        Bm25Parameters parameters)
    {
        this.ids = ids;
        this.lengths = lengths;
        this.postings = postings;
        this.averageLength = averageLength;
        Parameters = parameters;
    }

    public Bm25Parameters Parameters { get; }
    public int DocumentCount => ids.Length;
    public int TermCount => postings.Count;
    public double AverageDocumentLength => averageLength;

    public static Bm25Index Create(IEnumerable<Bm25Document> documents, Bm25Parameters? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        parameters ??= Bm25Parameters.Default;
        parameters.Validate();

        var ids = new List<string>();
        var lengths = new List<int>();
        var building = new Dictionary<string, List<Posting>>(StringComparer.Ordinal);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        long totalLength = 0;
        foreach (var document in documents)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentException.ThrowIfNullOrWhiteSpace(document.Id);
            ArgumentNullException.ThrowIfNull(document.Tokens);
            if (!seenIds.Add(document.Id))
            {
                throw new ArgumentException($"Чанк {document.Id} повторяется в корпусе BM25.", nameof(documents));
            }

            var index = ids.Count;
            ids.Add(document.Id);
            lengths.Add(document.Tokens.Count);
            totalLength += document.Tokens.Count;

            var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var token in document.Tokens)
            {
                frequencies[token] = frequencies.GetValueOrDefault(token) + 1;
            }

            foreach (var (term, frequency) in frequencies)
            {
                if (!building.TryGetValue(term, out var list))
                {
                    list = [];
                    building[term] = list;
                }

                list.Add(new Posting(index, frequency));
            }
        }

        var postings = new Dictionary<string, Posting[]>(building.Count, StringComparer.Ordinal);
        foreach (var (term, list) in building)
        {
            postings[term] = list.ToArray();
        }

        var averageLength = ids.Count == 0 ? 0 : (double)totalLength / ids.Count;
        return new Bm25Index(ids.ToArray(), lengths.ToArray(), postings, averageLength, parameters);
    }

    public IReadOnlyList<Bm25Hit> Search(string query, int topN)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Search(Bm25Tokenizer.Tokenize(query), topN);
    }

    /// <summary>Топ-N чанков по убыванию score; чанки без совпадений не возвращаются. Ничья — по id.</summary>
    public IReadOnlyList<Bm25Hit> Search(IReadOnlyList<string> queryTokens, int topN)
    {
        ArgumentNullException.ThrowIfNull(queryTokens);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topN);
        if (ids.Length == 0 || queryTokens.Count == 0)
        {
            return [];
        }

        var scores = new double[ids.Length];
        var touched = new List<int>();
        foreach (var term in queryTokens.Distinct(StringComparer.Ordinal))
        {
            if (!postings.TryGetValue(term, out var list))
            {
                continue;
            }

            var documentFrequency = list.Length;
            var idf = Math.Log(1 + ((ids.Length - documentFrequency + 0.5) / (documentFrequency + 0.5)));
            foreach (var posting in list)
            {
                var normalization = averageLength > 0
                    ? 1 - Parameters.B + (Parameters.B * lengths[posting.Document] / averageLength)
                    : 1;
                var frequency = posting.Frequency;
                var contribution = idf * frequency * (Parameters.K1 + 1) /
                    (frequency + (Parameters.K1 * normalization));
                if (scores[posting.Document] == 0)
                {
                    touched.Add(posting.Document);
                }

                scores[posting.Document] += contribution;
            }
        }

        return touched
            .Where(document => scores[document] > 0)
            .Select(document => new Bm25Hit(ids[document], scores[document]))
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Id, StringComparer.Ordinal)
            .Take(topN)
            .ToList();
    }

    private readonly record struct Posting(int Document, int Frequency);
}
