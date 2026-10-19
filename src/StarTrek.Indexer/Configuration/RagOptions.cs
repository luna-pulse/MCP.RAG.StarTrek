namespace StarTrek.Indexer.Configuration;

/// <summary>Пороги цикла Corrective RAG: сколько релевантных чанков достаточно и сколько раз расширять запрос.</summary>
public sealed class RagOptions
{
    public const string SectionName = "Rag";

    /// <summary>Сколько релевантных чанков останавливает цикл. Меньше — запрос расширяется.</summary>
    public int MinRelevantChunks { get; init; } = 2;

    /// <summary>Сколько раз можно вызвать расширение запроса после первого поиска.</summary>
    public int MaxBroadenAttempts { get; init; } = 2;

    /// <summary>
    /// Временный порог оценки RRF. Чанк с оценкой не ниже этого числа считается релевантным,
    /// пока вместо него не подключена оценка моделью.
    /// </summary>
    public double MinRrfScore { get; init; } = 0.015;
}
