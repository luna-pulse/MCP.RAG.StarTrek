namespace StarTrek.Indexer.Rag;

/// <summary>
/// Шаг Rewrite Query: вопрос пользователя становится коротким поисковым запросом.
/// Следующие шаги Corrective RAG (оценка чанков, расширение запроса, ответ) сюда не входят.
/// </summary>
public interface IQueryRewriter
{
    /// <summary>
    /// Возвращает запрос для гибридного поиска.
    /// Пустой ответ, слишком длинный текст, ошибка или таймаут Ollama — исходный вопрос.
    /// </summary>
    Task<string> RewriteAsync(string question, CancellationToken cancellationToken = default);
}
