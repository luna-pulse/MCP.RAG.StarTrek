using System.ComponentModel;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using StarTrek.Indexer.Configuration;
using StarTrek.Indexer.Search;

namespace StarTrek.Mcp;

[McpServerToolType]
public sealed class SearchTools(
    LexicalSearchService lexical,
    SemanticSearchService semantic,
    HybridSearchService hybrid,
    IOptions<IndexerOptions> options)
{
    [McpServerTool(Name = "search_bm25", ReadOnly = true, UseStructuredContent = true)]
    [Description("Ищет чанки корпуса Star Trek по словам запроса в индексе Okapi BM25 (k1 = 1.5, b = 0.75). Запрос — русская фраза любой длины: одно слово или несколько. Строка режется тем же Bm25Tokenizer, которым собран корпус; это не string.Contains и не поиск только кода корабля. Вызывай, когда важны сами слова вопроса: обычные русские слова, имена, термины, номера вроде NCC-1701-D. Не вызывай, когда нужны смысл и перефраз без совпадения слов: для смысла есть search_semantic, для обоих каналов — find_relevant_docs. Индекс не перестраивает и файлы не индексирует.")]
    public Task<ChunkSearchResponse> SearchBm25(
        [Description("Фраза любой длины: одно или несколько слов на русском или идентификатор. Регистр не важен. Токенизатор режет её на слова; отдельного поиска подстроки нет.")] string query,
        [Description("Сколько чанков вернуть. Нет значения — 10. Меньше 1 приводится к 1, больше 20 — к 20.")] int? limit = null,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() =>
        {
            SearchInput.RejectBlank(query);
            return lexical.SearchAsync(query, SearchLimits.Clamp(limit, options.Value.Search), cancellationToken);
        });

    [McpServerTool(Name = "search_semantic", ReadOnly = true, UseStructuredContent = true)]
    [Description("Ищет чанки корпуса Star Trek по смыслу запроса в коллекции ChromaDB startrek. Вектор считает локальная модель intfloat/multilingual-e5-small, 384 измерения. Вызывай, когда вопрос задан своими словами и нужный фрагмент может быть другими словами. Не вызывай, когда важны сами слова запроса без перефраза: для слов есть search_bm25. Если нужны и слова, и смысл — find_relevant_docs. Ответ не сочиняет, только возвращает чанки и расстояние.")]
    public Task<ChunkSearchResponse> SearchSemantic(
        [Description("Вопрос или фраза своими словами. В хранилище уходит вектор, а не поиск подстроки.")] string query,
        [Description("Сколько ближайших чанков вернуть. Нет значения — 10. Меньше 1 приводится к 1, больше 20 — к 20.")] int? limit = null,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() =>
        {
            SearchInput.RejectBlank(query);
            return semantic.SearchAsync(query, SearchLimits.Clamp(limit, options.Value.Search), cancellationToken);
        });

    [McpServerTool(Name = "find_relevant_docs", ReadOnly = true, UseStructuredContent = true)]
    [Description("Ищет чанки корпуса Star Trek сразу по словам и по смыслу: Okapi BM25 по токенам запроса и векторный поиск в коллекции ChromaDB startrek, затем сливает оба списка через Reciprocal Rank Fusion (RRF). Запрос — фраза любой длины. Вызывай, когда в вопросе нужны и точные слова (имена, термины, номера), и смысл или перефраз. Не вызывай, если достаточно одного канала: только слова — search_bm25, только смысл — search_semantic. Ответ не сочиняет, только возвращает чанки и оценку RRF.")]
    public Task<ChunkSearchResponse> FindRelevantDocs(
        [Description("Вопрос или фраза любой длины. Идут и в токенизатор BM25, и в векторный запрос.")] string query,
        [Description("Сколько чанков вернуть после слияния RRF. Нет значения — 10. Меньше 1 приводится к 1, больше 20 — к 20.")] int? top_k = null,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(async () =>
        {
            SearchInput.RejectBlank(query);
            var result = await hybrid.SearchAsync(
                query,
                SearchLimits.Clamp(top_k, options.Value.Search),
                cancellationToken);
            return new ChunkSearchResponse
            {
                Query = result.Query,
                Chunks = result.Hits.Select(SearchChunkMapper.FromHybridHit).ToArray()
            };
        });

    private static async Task<ChunkSearchResponse> InvokeAsync(Func<Task<ChunkSearchResponse>> search)
    {
        try
        {
            return await search();
        }
        catch (Exception exception) when (exception is not McpException and not OperationCanceledException)
        {
            throw new McpException(exception.Message, exception);
        }
    }
}
