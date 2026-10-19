namespace StarTrek.Indexer.Rag;

/// <summary>
/// Шаг Broaden Query: предыдущий запрос становится шире (синонимы, общие термины, другие написания).
/// Пустой ответ, повтор, ошибка или таймаут не запускают поиск тем же текстом.
/// </summary>
public interface IQueryBroadener
{
    Task<string> BroadenAsync(
        string originalQuestion,
        string previousQuery,
        int attempt,
        CancellationToken cancellationToken = default);
}
