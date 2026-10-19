using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using StarTrek.Mcp.Rag;

namespace StarTrek.Mcp;

[McpServerToolType]
public sealed class AskQuestionTools(CorrectiveRagService rag)
{
    public const string EmptyQuestionMessage = "Вопрос пуст: нужна непустая фраза.";

    [McpServerTool(Name = "ask_question", ReadOnly = true, UseStructuredContent = true)]
    [Description("Отвечает на вопрос по русскоязычной базе знаний Star Trek: расы, планеты, корабли и сериалы. Внутри переписывает вопрос, ищет чанки гибридным поиском (BM25 и вектор ChromaDB, слияние RRF), оценивает релевантность и пишет ответ только по найденным фрагментам, с файлом и позицией источника. Вызывай, когда пользователь спрашивает факт о вселенной Star Trek и нужен готовый ответ, а не список чанков. Не вызывай для кода, чужих проектов и вопросов не про Star Trek. Если нужны сами чанки без ответа — search_hybrid, search_bm25 или search_semantic. Индекс не меняет.")]
    public Task<AskQuestionResponse> AskQuestion(
        [Description("Вопрос о факте из базы знаний Star Trek: имя, корабль, дата, раса или серия. Пустая строка — ошибка.")] string question,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() =>
        {
            if (string.IsNullOrWhiteSpace(question))
            {
                throw new McpException(EmptyQuestionMessage);
            }

            return rag.AskAsync(question, cancellationToken);
        });

    private static async Task<AskQuestionResponse> InvokeAsync(Func<Task<AskQuestionResponse>> ask)
    {
        try
        {
            return await ask();
        }
        catch (Exception exception) when (exception is not McpException and not OperationCanceledException)
        {
            throw new McpException(exception.Message, exception);
        }
    }
}
