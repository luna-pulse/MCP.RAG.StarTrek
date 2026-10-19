using System.ComponentModel;
using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using StarTrek.Indexer.Chroma;
using StarTrek.Indexer.Rag;
using StarTrek.Indexer.Search;
using StarTrek.Mcp.Rag;

namespace StarTrek.Mcp.Tests;

[Collection("McpHost")]
public sealed class AskQuestionToolTests
{
    private const string Description =
        "Отвечает на вопрос по русскоязычной базе знаний Star Trek: расы, планеты, корабли и сериалы. Внутри Corrective RAG: переписывает вопрос, ищет чанки гибридным поиском (BM25 и вектор ChromaDB, слияние RRF), при нехватке релевантных чанков расширяет запрос до двух раз и пишет ответ только по фрагментам с оценкой RRF не ниже порога, с файлом и позицией источника. Вызывай, когда пользователь спрашивает факт о вселенной Star Trek и нужен готовый ответ, а не список чанков. Не вызывай для кода, чужих проектов и вопросов не про Star Trek. Если нужны сами чанки без ответа — find_relevant_docs, search_bm25 или search_semantic. Индекс не меняет.";

    [Fact]
    public async Task AskQuestionReturnsAnswerSourcesAndAttempts()
    {
        var chat = new ToolChat("Вулканский корабль первого контакта назывался Т'Плана-Хат.");
        var search = new ToolSearch(_ =>
        [
            Hit("ship:8", "Корабль Т'Плана-Хат встретил Феникс.", 0.05),
            Hit("ship:9", "Дата контакта — 5 апреля 2063 года.", 0.04)
        ]);
        await using var app = await McpHostTests.Start(services =>
        {
            services.AddSingleton<IQueryRewriter>(new FixedRewriter("вулканский корабль первого контакта"));
            services.AddSingleton<IQueryBroadener>(new UnusedBroadener());
            services.AddSingleton<IHybridSearch>(search);
            services.AddSingleton<IChatClient>(chat);
        });
        await using var client = await ConnectAsync(app);

        var listed = await client.ListToolsAsync();
        var tool = Assert.Single(listed, item => item.Name == "ask_question");
        Assert.Equal(Description, tool.Description);
        Assert.Contains("Star Trek", tool.Description, StringComparison.Ordinal);

        var result = await client.CallToolAsync(
            "ask_question",
            new Dictionary<string, object?> { ["question"] = "Как назывался вулканский корабль первого контакта?" });
        var json = SuccessJson(result);
        Assert.Contains("Т'Плана-Хат", json["answer"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(1, json["meta"]!["attempts"]!.GetValue<int>());
        Assert.Equal("вулканский корабль первого контакта", json["meta"]!["rewrittenQuery"]!.GetValue<string>());
        Assert.Equal("вулканский корабль первого контакта", json["meta"]!["query"]!.GetValue<string>());
        var sources = json["sources"]!.AsArray();
        Assert.Equal(2, sources.Count);
        Assert.Equal("SourceFiles/Races/Вулканцы.txt", sources[0]!["sourceFile"]!.GetValue<string>());
        Assert.Equal("lines 80-90", sources[0]!["position"]!.GetValue<string>());

        var method = typeof(AskQuestionTools).GetMethod(nameof(AskQuestionTools.AskQuestion))!;
        var attribute = method.GetCustomAttribute<McpServerToolAttribute>()!;
        Assert.Equal("ask_question", attribute.Name);
        Assert.True(attribute.ReadOnly);
        Assert.True(attribute.UseStructuredContent);
        Assert.Equal(Description, method.GetCustomAttribute<DescriptionAttribute>()!.Description);
        Assert.Equal(1, chat.Calls);
    }

    [Fact]
    public async Task BlankQuestionIsToolError()
    {
        var chat = new ToolChat(new InvalidOperationException("модель не нужна"));
        await using var app = await McpHostTests.Start(services => services.AddSingleton<IChatClient>(chat));
        await using var client = await ConnectAsync(app);

        var result = await client.CallToolAsync("ask_question", new Dictionary<string, object?> { ["question"] = "  " });
        Assert.True(result.IsError);
        Assert.Equal(AskQuestionTools.EmptyQuestionMessage, Text(result));
        Assert.DoesNotContain("An error occurred invoking", Text(result));
        Assert.Equal(0, chat.Calls);
    }

    [Fact]
    public async Task ChromaFailureIsToolError()
    {
        var chat = new ToolChat("модель не нужна");
        var search = new ToolSearch(_ => throw new ChromaUnavailableException(
            "ChromaDB не отвечает по адресу http://127.0.0.1:8000 (отказ соединения). Запустите её командой «docker compose up -d» из корня репозитория."));
        await using var app = await McpHostTests.Start(services =>
        {
            services.AddSingleton<IQueryRewriter>(new FixedRewriter("запрос"));
            services.AddSingleton<IHybridSearch>(search);
            services.AddSingleton<IChatClient>(chat);
        });
        await using var client = await ConnectAsync(app);

        var result = await client.CallToolAsync(
            "ask_question",
            new Dictionary<string, object?> { ["question"] = "Кто такой Сурак?" });
        Assert.True(result.IsError);
        Assert.Contains("ChromaDB не отвечает по адресу", Text(result), StringComparison.Ordinal);
        Assert.DoesNotContain("An error occurred invoking", Text(result));
        Assert.Equal(0, chat.Calls);
    }

    [Fact]
    public async Task OllamaDownIsToolErrorAndTheHostStaysUp()
    {
        var search = new ToolSearch(_ =>
        [
            Hit("ship:8", "Родители Спока — Сарек и Аманда Грейсон.", 0.05),
            Hit("ship:9", "Спок родился в 2230 году.", 0.04)
        ]);
        await using var app = await McpHostTests.Start(
            services => services.AddSingleton<IHybridSearch>(search),
            extraKeys: new Dictionary<string, string?>
            {
                ["Llm:Endpoint"] = "http://127.0.0.1:9",
                ["Llm:Model"] = "qwen3.5:4b",
                ["Llm:TimeoutSeconds"] = "2"
            });
        await using var client = await ConnectAsync(app);

        var result = await client.CallToolAsync(
            "ask_question",
            new Dictionary<string, object?> { ["question"] = "Кто родители Спока?" });
        Assert.True(result.IsError);
        Assert.Contains("Ollama не отвечает по адресу http://127.0.0.1:9", Text(result), StringComparison.Ordinal);
        Assert.DoesNotContain("An error occurred invoking", Text(result));

        using var http = new HttpClient();
        var health = await http.GetAsync(new Uri(new Uri(app.Urls.Single()), "/health"));
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("MCP-сервер отвечает: healthy.", await health.Content.ReadAsStringAsync());
    }

    private static HybridSearchHit Hit(string id, string text, double score) =>
        new(
            id,
            score,
            1,
            0.1,
            1,
            2,
            text,
            new Dictionary<string, object?>
            {
                [ChromaMetadataKeys.SourceFile] = "SourceFiles/Races/Вулканцы.txt",
                [ChromaMetadataKeys.Position] = "lines 80-90",
                [ChromaMetadataKeys.ChunkIndex] = 8
            });

    private static async Task<McpClient> ConnectAsync(WebApplication app)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(new Uri(app.Urls.Single()), "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp
        });
        return await McpClient.CreateAsync(transport);
    }

    private static string Text(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    private static JsonObject SuccessJson(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        Assert.NotNull(result.StructuredContent);
        return JsonNode.Parse(Text(result))!.AsObject();
    }

    private sealed class FixedRewriter(string query) : IQueryRewriter
    {
        public Task<string> RewriteAsync(string question, CancellationToken cancellationToken = default) =>
            Task.FromResult(query);
    }

    private sealed class UnusedBroadener : IQueryBroadener
    {
        public Task<string> BroadenAsync(
            string originalQuestion,
            string previousQuery,
            int attempt,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("расширение не нужно");
    }

    private sealed class ToolSearch(Func<string, IReadOnlyList<HybridSearchHit>> hits) : IHybridSearch
    {
        public Task<HybridSearchResult> SearchAsync(
            string query,
            int? resultCount = null,
            CancellationToken cancellationToken = default)
        {
            var found = hits(query);
            return Task.FromResult(new HybridSearchResult(query, found, found.Count, found.Count));
        }
    }

    private sealed class ToolChat : IChatClient
    {
        private readonly string? _text;
        private readonly Exception? _error;

        public ToolChat(string text) => _text = text;

        public ToolChat(Exception error) => _error = error;

        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (_error is not null)
            {
                throw _error;
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _text)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
