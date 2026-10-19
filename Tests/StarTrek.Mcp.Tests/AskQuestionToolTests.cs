using System.ComponentModel;
using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using StarTrek.Indexer.Chroma;
using StarTrek.Indexer.Search;
using StarTrek.Mcp.Rag;

namespace StarTrek.Mcp.Tests;

[Collection("McpHost")]
public sealed class AskQuestionToolTests
{
    private const string Description =
        "Отвечает на вопрос по русскоязычной базе знаний Star Trek: расы, планеты, корабли и сериалы. Внутри переписывает вопрос, ищет чанки гибридным поиском (BM25 и вектор ChromaDB, слияние RRF), оценивает релевантность и пишет ответ только по найденным фрагментам, с файлом и позицией источника. Вызывай, когда пользователь спрашивает факт о вселенной Star Trek и нужен готовый ответ, а не список чанков. Не вызывай для кода, чужих проектов и вопросов не про Star Trek. Если нужны сами чанки без ответа — search_hybrid, search_bm25 или search_semantic. Индекс не меняет.";

    [Fact]
    public async Task AskQuestionReturnsAnswerSourcesAndAttempts()
    {
        var chat = new ToolChat((function, _) => function switch
        {
            PromptCatalog.Rewrite => "вулканский корабль первого контакта",
            PromptCatalog.Grade => "да",
            PromptCatalog.Generate => "Вулканский корабль первого контакта назывался Т'Плана-Хат.",
            _ => throw new InvalidOperationException(function)
        });
        var retriever = new ToolRetriever(_ =>
        [
            Hit("ship:8", "Корабль Т'Плана-Хат встретил Феникс."),
            Hit("ship:9", "Дата контакта — 5 апреля 2063 года.")
        ]);
        await using var app = await McpHostTests.Start(services =>
        {
            services.AddSingleton<IRagChat>(chat);
            services.AddSingleton<ICorpusRetriever>(retriever);
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
    }

    [Fact]
    public async Task BlankQuestionIsToolError()
    {
        var chat = new ToolChat((_, _) => throw new InvalidOperationException("модель не нужна"));
        await using var app = await McpHostTests.Start(services => services.AddSingleton<IRagChat>(chat));
        await using var client = await ConnectAsync(app);

        var result = await client.CallToolAsync("ask_question", new Dictionary<string, object?> { ["question"] = "  " });
        Assert.True(result.IsError);
        Assert.Equal(AskQuestionTools.EmptyQuestionMessage, Text(result));
        Assert.DoesNotContain("An error occurred invoking", Text(result));
        Assert.Empty(chat.Calls);
    }

    [Fact]
    public async Task ChromaFailureIsToolError()
    {
        var chat = new ToolChat((function, _) => function == PromptCatalog.Rewrite
            ? "запрос"
            : throw new InvalidOperationException(function));
        var retriever = new ToolRetriever(_ => throw new ChromaUnavailableException(
            "ChromaDB не отвечает по адресу http://127.0.0.1:8000 (отказ соединения). Запустите её командой «docker compose up -d» из корня репозитория."));
        await using var app = await McpHostTests.Start(services =>
        {
            services.AddSingleton<IRagChat>(chat);
            services.AddSingleton<ICorpusRetriever>(retriever);
        });
        await using var client = await ConnectAsync(app);

        var result = await client.CallToolAsync(
            "ask_question",
            new Dictionary<string, object?> { ["question"] = "Кто такой Сурак?" });
        Assert.True(result.IsError);
        Assert.Contains("ChromaDB не отвечает по адресу", Text(result), StringComparison.Ordinal);
        Assert.DoesNotContain("An error occurred invoking", Text(result));
        Assert.Equal(0, chat.Count(PromptCatalog.Generate));
    }

    [Fact]
    public async Task OllamaDownIsToolErrorAndTheHostStaysUp()
    {
        await using var app = await McpHostTests.Start(extraKeys: new Dictionary<string, string?>
        {
            ["Rag:Ollama:Endpoint"] = "http://127.0.0.1:9",
            ["Rag:Ollama:Model"] = "qwen2.5:3b"
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
    }

    private static HybridSearchHit Hit(string id, string text) =>
        new(
            id,
            0.02,
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

    private sealed class ToolChat : IRagChat
    {
        public List<string> Calls { get; } = [];

        private readonly Func<string, IReadOnlyDictionary<string, string>, string> _respond;

        public ToolChat(Func<string, IReadOnlyDictionary<string, string>, string> respond) => _respond = respond;

        public Task<string> InvokeAsync(
            string functionName,
            IReadOnlyDictionary<string, string> variables,
            CancellationToken cancellationToken)
        {
            Calls.Add(functionName);
            return Task.FromResult(_respond(functionName, variables));
        }

        public int Count(string functionName) => Calls.Count(call => call == functionName);
    }

    private sealed class ToolRetriever : ICorpusRetriever
    {
        private readonly Func<string, IReadOnlyList<HybridSearchHit>> _respond;

        public ToolRetriever(Func<string, IReadOnlyList<HybridSearchHit>> respond) => _respond = respond;

        public Task<HybridSearchResult> SearchAsync(string query, int resultCount, CancellationToken cancellationToken)
        {
            var hits = _respond(query).Take(resultCount).ToList();
            return Task.FromResult(new HybridSearchResult(query, hits, hits.Count, hits.Count));
        }
    }
}
