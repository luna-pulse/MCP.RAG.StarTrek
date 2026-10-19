using System.ComponentModel;
using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using StarTrek.Indexer.Bm25;
using StarTrek.Indexer.Configuration;
using StarTrek.Indexer.Embeddings;
using StarTrek.Indexer.Search;
using StarTrek.Mcp;

namespace StarTrek.Mcp.Tests;

[Collection("McpHost")]
public sealed class SearchToolsTests
{
    private const string Bm25Description =
        "Ищет чанки корпуса Star Trek по словам запроса в индексе Okapi BM25 (k1 = 1.5, b = 0.75). Запрос — русская фраза любой длины: одно слово или несколько. Строка режется тем же Bm25Tokenizer, которым собран корпус; это не string.Contains и не поиск только кода корабля. Вызывай, когда важны сами слова вопроса: обычные русские слова, имена, термины, номера вроде NCC-1701-D. Не вызывай, когда нужны смысл и перефраз без совпадения слов: для смысла есть search_semantic, для обоих каналов — find_relevant_docs. Индекс не перестраивает и файлы не индексирует.";

    private const string Bm25QueryDescription =
        "Фраза любой длины: одно или несколько слов на русском или идентификатор. Регистр не важен. Токенизатор режет её на слова; отдельного поиска подстроки нет.";

    private const string Bm25LimitDescription =
        "Сколько чанков вернуть. Нет значения — 10. Меньше 1 приводится к 1, больше 20 — к 20.";

    private const string SemanticDescription =
        "Ищет чанки корпуса Star Trek по смыслу запроса в коллекции ChromaDB startrek. Вектор считает локальная модель intfloat/multilingual-e5-small, 384 измерения. Вызывай, когда вопрос задан своими словами и нужный фрагмент может быть другими словами. Не вызывай, когда важны сами слова запроса без перефраза: для слов есть search_bm25. Если нужны и слова, и смысл — find_relevant_docs. Ответ не сочиняет, только возвращает чанки и расстояние.";

    private const string SemanticQueryDescription =
        "Вопрос или фраза своими словами. В хранилище уходит вектор, а не поиск подстроки.";

    private const string SemanticLimitDescription =
        "Сколько ближайших чанков вернуть. Нет значения — 10. Меньше 1 приводится к 1, больше 20 — к 20.";

    private const string HybridDescription =
        "Ищет чанки корпуса Star Trek сразу по словам и по смыслу: Okapi BM25 по токенам запроса и векторный поиск в коллекции ChromaDB startrek, затем сливает оба списка через Reciprocal Rank Fusion (RRF). Запрос — фраза любой длины. Вызывай, когда в вопросе нужны и точные слова (имена, термины, номера), и смысл или перефраз. Не вызывай, если достаточно одного канала: только слова — search_bm25, только смысл — search_semantic. Ответ не сочиняет, только возвращает чанки и оценку RRF.";

    private const string HybridQueryDescription =
        "Вопрос или фраза любой длины. Идут и в токенизатор BM25, и в векторный запрос.";

    private const string HybridLimitDescription =
        "Сколько чанков вернуть после слияния RRF. Нет значения — 10. Меньше 1 приводится к 1, больше 20 — к 20.";

    [Fact]
    public async Task ListsExactlyThreeToolsWithFullDescriptions()
    {
        await using var app = await McpHostTests.Start(services => PrepareRankDoubles(services, out _, out _));
        await using var client = await ConnectAsync(app, "/mcp");
        var tools = await client.ListToolsAsync();

        Assert.Equal(
            ["find_relevant_docs", "search_bm25", "search_semantic"],
            tools.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.All(tools, tool => Assert.False(string.IsNullOrWhiteSpace(tool.Description)));
        var joined = string.Join('\n', tools.Select(tool => tool.Description));
        Assert.Contains("search_bm25", joined);
        Assert.Contains("search_semantic", joined);
        Assert.Contains("find_relevant_docs", joined);

        AssertTool(nameof(SearchTools.SearchBm25), "search_bm25", Bm25Description, Bm25QueryDescription, Bm25LimitDescription);
        AssertTool(nameof(SearchTools.SearchSemantic), "search_semantic", SemanticDescription, SemanticQueryDescription, SemanticLimitDescription);
        AssertTool(
            nameof(SearchTools.FindRelevantDocs),
            "find_relevant_docs",
            HybridDescription,
            HybridQueryDescription,
            HybridLimitDescription,
            "top_k");
    }

    [Fact]
    public void ClampFollowsSearchOptions()
    {
        var standard = new SearchOptions { ResultCount = 10, VectorTopN = 20, Bm25TopN = 20 };
        Assert.Equal(10, SearchLimits.Clamp(null, standard));
        Assert.Equal(1, SearchLimits.Clamp(0, standard));
        Assert.Equal(20, SearchLimits.Clamp(21, standard));

        var narrow = new SearchOptions { ResultCount = 10, VectorTopN = 5, Bm25TopN = 8 };
        Assert.Equal(5, SearchLimits.Clamp(100, narrow));

        var shortDefault = new SearchOptions { ResultCount = 4, VectorTopN = 20, Bm25TopN = 20 };
        Assert.Equal(4, SearchLimits.Clamp(null, shortDefault));
        Assert.DoesNotContain("20", File.ReadAllText(Path.Combine(
            RepositoryPathResolver.FindRepositoryRoot(AppContext.BaseDirectory),
            "src",
            "StarTrek.Mcp",
            "SearchLimits.cs")));
    }

    [Fact]
    public async Task SearchBm25RanksWordsAndSkipsEmbeddings()
    {
        RecordingQueryEmbedder embedder = null!;
        await using var app = await McpHostTests.Start(services => PrepareRankDoubles(services, out _, out embedder));
        await using var client = await ConnectAsync(app, "/mcp");

        var phrase = SuccessJson(await client.CallToolAsync("search_bm25", Args("Вулканцы телепаты")));
        Assert.Equal(["races:1", "races:2"], Ids(phrase));
        var code = SuccessJson(await client.CallToolAsync("search_bm25", Args("NCC-1701-D")));
        Assert.Equal("ships:1", Assert.Single(Ids(code)));
        Assert.Empty(embedder.Queries);
    }

    [Fact]
    public async Task SearchSemanticSendsRawQueryWithoutCorpusFile()
    {
        var corpus = MissingCorpusPath();
        RecordingQueryEmbedder embedder = null!;
        ScriptedChromaHandler handler = null!;
        await using var app = await McpHostTests.Start(services =>
        {
            handler = new ScriptedChromaHandler();
            handler.AddCollection("startrek");
            handler.Seed("startrek", "far:0", "дальний чанк", null, 0.4);
            handler.Seed("startrek", "near:0", "ближний чанк", null, 0.1);
            embedder = new RecordingQueryEmbedder();
            services.AddSingleton(handler.CreateClient());
            services.AddSingleton<IQueryEmbeddingGenerator>(embedder);
        }, corpusPath: corpus);
        await using var client = await ConnectAsync(app, "/mcp");

        var result = SuccessJson(await client.CallToolAsync("search_semantic", Args("вулканская логика", 2)));
        Assert.Equal("вулканская логика", Assert.Single(embedder.Queries));
        Assert.False(embedder.Queries[0].StartsWith("query: ", StringComparison.Ordinal));
        Assert.Equal(["far:0", "near:0"], Ids(result));
        Assert.Equal(2, LastResultCount(handler));
        Assert.False(File.Exists(corpus));
    }

    [Fact]
    public async Task FindRelevantDocsMatchesServiceAndKeepsVectorOnlyQuery()
    {
        RecordingQueryEmbedder embedder = null!;
        await using var app = await McpHostTests.Start(services => PrepareRankDoubles(services, out _, out embedder));
        await using var client = await ConnectAsync(app, "/mcp");
        const string query = "Вулканцы телепаты";
        var direct = await app.Services.GetRequiredService<HybridSearchService>().SearchAsync(query, 10);
        var viaTool = SuccessJson(await client.CallToolAsync(
            "find_relevant_docs",
            new Dictionary<string, object?> { ["query"] = query, ["top_k"] = 10 }));
        var expected = direct.Hits.Select(SearchChunkMapper.FromHybridHit).Select(chunk => chunk.RrfScore).ToArray();
        var actual = viaTool["chunks"]!.AsArray().Select(chunk => chunk!["rrfScore"]!.GetValue<double>()).ToArray();
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index]!.Value, actual[index], precision: 10);
        }

        RecordingQueryEmbedder vectorEmbedder = null!;
        await using var vectorHost = await McpHostTests.Start(services =>
        {
            var handler = new ScriptedChromaHandler();
            handler.AddCollection("startrek");
            handler.Seed("startrek", "only:0", "один чанк", null, 0.2);
            vectorEmbedder = new RecordingQueryEmbedder();
            services.AddSingleton(handler.CreateClient());
            services.AddSingleton<IQueryEmbeddingGenerator>(vectorEmbedder);
            services.AddSingleton(BuiltHolder());
        });
        await using var vectorClient = await ConnectAsync(vectorHost, "/mcp");
        var vectorOnly = SuccessJson(await vectorClient.CallToolAsync("find_relevant_docs", Args("???")));
        Assert.Equal("???", Assert.Single(vectorEmbedder.Queries));
        var only = Assert.Single(vectorOnly["chunks"]!.AsArray());
        Assert.Equal(1d / 61d, only!["rrfScore"]!.GetValue<double>(), precision: 10);
    }

    [Fact]
    public async Task BlankQueryIsToolErrorAndDoesNotTouchStorage()
    {
        var corpus = MissingCorpusPath();
        ScriptedChromaHandler handler = null!;
        await using var app = await McpHostTests.Start(services =>
        {
            handler = new ScriptedChromaHandler();
            services.AddSingleton(handler.CreateClient());
        }, corpusPath: corpus);
        await using var client = await ConnectAsync(app, "/mcp");

        foreach (var query in new[] { "", "   " })
        {
            var result = await client.CallToolAsync("search_bm25", Args(query));
            Assert.True(result.IsError);
            Assert.Equal(SearchInput.EmptyQueryMessage, Text(result));
            Assert.DoesNotContain("An error occurred invoking", Text(result));
        }

        Assert.Empty(handler.Requests);
        Assert.False(File.Exists(corpus));
    }

    [Fact]
    public async Task Bm25WithoutTokensIsToolError()
    {
        await using var app = await McpHostTests.Start(services => PrepareRankDoubles(services, out _, out _));
        await using var client = await ConnectAsync(app, "/mcp");
        var result = await client.CallToolAsync("search_bm25", Args("???"));
        Assert.True(result.IsError);
        Assert.Equal(LexicalSearchService.NoTokensMessage, Text(result));
    }

    [Fact]
    public async Task SemanticLimitIsClampedIntoChroma()
    {
        ScriptedChromaHandler handler = null!;
        await using var app = await McpHostTests.Start(services =>
        {
            handler = SeedSemantic(services);
        });
        await using var client = await ConnectAsync(app, "/mcp");

        Assert.NotEqual(true, (await client.CallToolAsync("search_semantic", Args("логика", 0))).IsError);
        Assert.Equal(1, LastResultCount(handler));
        Assert.NotEqual(true, (await client.CallToolAsync("search_semantic", Args("логика", 100))).IsError);
        Assert.Equal(20, LastResultCount(handler));
        Assert.NotEqual(true, (await client.CallToolAsync("search_semantic", Args("логика"))).IsError);
        Assert.Equal(10, LastResultCount(handler));
    }

    [Fact]
    public async Task MissingCorpusNamesEmbedForBm25AndHybrid()
    {
        var corpus = MissingCorpusPath();
        await using var app = await McpHostTests.Start(services =>
        {
            var handler = new ScriptedChromaHandler();
            handler.AddCollection("startrek");
            services.AddSingleton(handler.CreateClient());
            services.AddSingleton<IQueryEmbeddingGenerator>(new RecordingQueryEmbedder());
        }, corpusPath: corpus);
        await using var client = await ConnectAsync(app, "/mcp");

        foreach (var tool in new[] { "search_bm25", "find_relevant_docs" })
        {
            var result = await client.CallToolAsync(tool, Args("вулканцы"));
            Assert.True(result.IsError);
            Assert.Contains("Корпус BM25 не найден", Text(result));
            Assert.Contains("embed", Text(result));
        }

        Assert.False(File.Exists(corpus));
    }

    [Fact]
    public async Task MissingCollectionAndUnavailableChromaStayRussian()
    {
        await using (var missing = await McpHostTests.Start(services =>
        {
            var handler = new ScriptedChromaHandler();
            services.AddSingleton(handler.CreateClient());
            services.AddSingleton(BuiltHolder());
            services.AddSingleton<IQueryEmbeddingGenerator>(new RecordingQueryEmbedder());
        }))
        {
            await using var client = await ConnectAsync(missing, "/mcp");
            var result = await client.CallToolAsync("search_semantic", Args("логика"));
            Assert.True(result.IsError);
            Assert.Contains("Коллекция startrek не найдена", Text(result));
            Assert.DoesNotContain("/upsert", Text(result));
        }

        ScriptedChromaHandler down = null!;
        await using var unavailable = await McpHostTests.Start(services =>
        {
            down = new ScriptedChromaHandler { Available = false };
            down.AddCollection("startrek");
            services.AddSingleton(down.CreateClient());
            services.AddSingleton(BuiltHolder());
            services.AddSingleton<IQueryEmbeddingGenerator>(new RecordingQueryEmbedder());
        });
        await using var downClient = await ConnectAsync(unavailable, "/mcp");
        var failure = await downClient.CallToolAsync("search_bm25", Args("вулканцы"));
        Assert.True(failure.IsError);
        Assert.Contains("ChromaDB не отвечает по адресу", Text(failure));
        Assert.DoesNotContain(down.Requests, request => request.Path.EndsWith("/upsert", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ToolErrorStaysInProcess()
    {
        await using var app = await McpHostTests.Start(services => PrepareRankDoubles(services, out _, out _));
        await using var client = await ConnectAsync(app, "/mcp");
        var error = await client.CallToolAsync("search_bm25", Args("???"));
        Assert.True(error.IsError);

        var again = SuccessJson(await client.CallToolAsync("search_bm25", Args("вулканцы")));
        Assert.NotEmpty(Ids(again));

        using var http = new HttpClient();
        var health = await http.GetAsync(new Uri(new Uri(app.Urls.Single()), "/health"));
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("MCP-сервер отвечает: healthy.", await health.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TwoSemanticCallsNeedNoSession()
    {
        await using var app = await McpHostTests.Start(services => SeedSemantic(services));
        await using var client = await ConnectAsync(app, "/mcp");
        Assert.NotEqual(true, (await client.CallToolAsync("search_semantic", Args("логика"))).IsError);
        Assert.NotEqual(true, (await client.CallToolAsync("search_semantic", Args("логика"))).IsError);
    }

    [Fact]
    public async Task ToolsStayOffRootAndFollowConfiguredPath()
    {
        await using var app = await McpHostTests.Start();
        Assert.False(await ListsThreeToolsAsync(app, "/"));
        Assert.True(await ListsThreeToolsAsync(app, "/mcp"));

        await using var custom = await McpHostTests.Start(mcpPath: "/trek");
        Assert.False(await ListsThreeToolsAsync(custom, "/mcp"));
        Assert.True(await ListsThreeToolsAsync(custom, "/trek"));
    }

    private static void PrepareRankDoubles(
        IServiceCollection services,
        out ScriptedChromaHandler handler,
        out RecordingQueryEmbedder embedder)
    {
        handler = new ScriptedChromaHandler();
        handler.AddCollection("startrek");
        handler.Seed("startrek", "ships:1", "Энтерпрайз", null, 0.2);
        handler.Seed("startrek", "ships:0", "старый корабль", null, 0.2);
        handler.Seed("startrek", "races:1", "вулканцы телепаты", null, 0.4);
        handler.Seed("startrek", "races:2", "вулканцы", null, 0.3);
        handler.Seed("startrek", "names:1", "Таль Шанар", null, 0.2);
        handler.Seed("startrek", "b:0", "Спок", null, 0.2);
        handler.Seed("startrek", "a:0", "Спок", null, 0.2);
        embedder = new RecordingQueryEmbedder();
        services.AddSingleton(handler.CreateClient());
        services.AddSingleton<IQueryEmbeddingGenerator>(embedder);
        services.AddSingleton(BuiltHolder());
    }

    private static ScriptedChromaHandler SeedSemantic(IServiceCollection services)
    {
        var handler = new ScriptedChromaHandler();
        handler.AddCollection("startrek");
        handler.Seed("startrek", "far:0", "дальний чанк", null, 0.4);
        services.AddSingleton(handler.CreateClient());
        services.AddSingleton<IQueryEmbeddingGenerator>(new RecordingQueryEmbedder());
        return handler;
    }

    private static Bm25IndexHolder BuiltHolder()
    {
        var holder = new Bm25IndexHolder(
            new Bm25CorpusStore(MissingCorpusPath()),
            new Bm25Parameters(1.5, 0.75));
        holder.Build(new Bm25Corpus(
            Bm25Corpus.CurrentFormatVersion,
            Bm25Tokenizer.Version,
            "startrek",
            "fingerprint",
            [
                new Bm25CorpusChunk("ships:1", ["ncc-1701-d"]),
                new Bm25CorpusChunk("ships:0", ["ncc-1701"]),
                new Bm25CorpusChunk("races:1", ["вулканцы", "телепаты"]),
                new Bm25CorpusChunk("races:2", ["вулканцы"]),
                new Bm25CorpusChunk("names:1", ["таль", "шанар"]),
                new Bm25CorpusChunk("b:0", ["спок"]),
                new Bm25CorpusChunk("a:0", ["спок"])
            ]));
        return holder;
    }

    private static string MissingCorpusPath() =>
        Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "corpus.json");

    private static async Task<McpClient> ConnectAsync(WebApplication app, string path)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(new Uri(app.Urls.Single()), path),
            TransportMode = HttpTransportMode.StreamableHttp
        });
        return await McpClient.CreateAsync(transport);
    }

    private static async Task<bool> ListsThreeToolsAsync(WebApplication app, string path)
    {
        try
        {
            await using var client = await ConnectAsync(app, path);
            var tools = await client.ListToolsAsync();
            return tools.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(
                ["find_relevant_docs", "search_bm25", "search_semantic"]);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Dictionary<string, object?> Args(string query, int? limit = null)
    {
        var args = new Dictionary<string, object?> { ["query"] = query };
        if (limit is not null)
        {
            args["limit"] = limit.Value;
        }

        return args;
    }

    private static string Text(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    private static JsonObject SuccessJson(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        Assert.NotNull(result.StructuredContent);
        var json = JsonNode.Parse(Text(result))!.AsObject();
        Assert.NotNull(json["query"]);
        Assert.NotNull(json["chunks"]);
        return json;
    }

    private static string[] Ids(JsonObject response) =>
        response["chunks"]!.AsArray().Select(chunk => chunk!["id"]!.GetValue<string>()).ToArray();

    private static int LastResultCount(ScriptedChromaHandler handler)
    {
        var query = handler.Requests.Last(request => request.Path.EndsWith("/query", StringComparison.Ordinal));
        return JsonNode.Parse(query.Body)!["n_results"]!.GetValue<int>();
    }

    private static void AssertTool(
        string methodName,
        string toolName,
        string description,
        string queryDescription,
        string limitDescription,
        string countParameterName = "limit")
    {
        var method = typeof(SearchTools).GetMethod(methodName)!;
        var tool = method.GetCustomAttribute<McpServerToolAttribute>()!;
        Assert.Equal(toolName, tool.Name);
        Assert.True(tool.ReadOnly);
        Assert.True(tool.UseStructuredContent);
        Assert.Equal(description, method.GetCustomAttribute<DescriptionAttribute>()!.Description);
        Assert.Equal(queryDescription, DescriptionOf(method, "query"));
        Assert.Equal(limitDescription, DescriptionOf(method, countParameterName));
        Assert.Contains(method.GetParameters(), parameter => parameter.ParameterType == typeof(CancellationToken));
    }

    private static string DescriptionOf(MethodInfo method, string parameterName) =>
        method.GetParameters().Single(parameter => parameter.Name == parameterName)
            .GetCustomAttribute<DescriptionAttribute>()!.Description;
}
