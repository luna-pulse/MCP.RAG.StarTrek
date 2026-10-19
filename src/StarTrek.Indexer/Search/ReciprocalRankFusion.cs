namespace StarTrek.Indexer.Search;

/// <summary>Результат слияния: id, сумма RRF и ранг (с 1) в каждом исходном списке или null, если id там не было.</summary>
public sealed record RrfEntry(string Id, double Score, IReadOnlyList<int?> Ranks);

/// <summary>
/// Reciprocal Rank Fusion: score(id) = Σ по спискам 1 / (k + rank), rank считается с 1.
/// Чистая функция без зависимостей: её вызывает гибридный поиск.
/// </summary>
public static class ReciprocalRankFusion
{
    public const int DefaultK = 60;

    /// <summary>
    /// Сливает ранжированные списки id (лучший первым). Повтор id внутри одного списка учитывается один раз
    /// (по первой позиции). Порядок результата: score по убыванию, затем лучший ранг, затем id по ordinal.
    /// </summary>
    public static IReadOnlyList<RrfEntry> Fuse(
        IReadOnlyList<IReadOnlyList<string>> rankedLists,
        int k = DefaultK,
        int? limit = null)
    {
        ArgumentNullException.ThrowIfNull(rankedLists);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);
        if (limit is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "limit должен быть положительным.");
        }

        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        var ranks = new Dictionary<string, int?[]>(StringComparer.Ordinal);
        for (var listIndex = 0; listIndex < rankedLists.Count; listIndex++)
        {
            var list = rankedLists[listIndex] ?? throw new ArgumentException(
                $"Список {listIndex} равен null.",
                nameof(rankedLists));
            for (var position = 0; position < list.Count; position++)
            {
                var id = list[position];
                ArgumentException.ThrowIfNullOrWhiteSpace(id);
                if (!ranks.TryGetValue(id, out var idRanks))
                {
                    idRanks = new int?[rankedLists.Count];
                    ranks[id] = idRanks;
                }

                if (idRanks[listIndex] is not null)
                {
                    continue;
                }

                var rank = position + 1;
                idRanks[listIndex] = rank;
                scores[id] = scores.GetValueOrDefault(id) + (1.0 / (k + rank));
            }
        }

        var fused = scores
            .Select(pair => new RrfEntry(pair.Key, pair.Value, ranks[pair.Key]))
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Ranks.Where(rank => rank is not null).Min(rank => rank!.Value))
            .ThenBy(entry => entry.Id, StringComparer.Ordinal);
        return (limit is { } take ? fused.Take(take) : fused).ToList();
    }

    public static IReadOnlyList<RrfEntry> Fuse(
        IReadOnlyList<string> vectorRanking,
        IReadOnlyList<string> bm25Ranking,
        int k = DefaultK,
        int? limit = null) =>
        Fuse([vectorRanking, bm25Ranking], k, limit);
}
