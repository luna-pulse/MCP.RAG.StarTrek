using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StarTrek.Indexer.Bm25;
using StarTrek.Indexer.Chroma;
using StarTrek.Indexer.Configuration;
using StarTrek.Indexer.Embeddings;
using StarTrek.Indexer.Rag;
using StarTrek.Indexer.Search;

namespace StarTrek.Indexer.Tests;

public sealed class QueryRewriterTests
{
    private const string Question = "Расскажи, пожалуйста, кто капитан Энтерпрайза NCC-1701-D?";

    [Fact]
    public async Task RewriteReturnsConciseQuery()
    {
        var chat = new ScriptedChatClient { ResponseText = "Энтерпрайз NCC-1701-D капитан" };
        var rewriter = Create(chat);

        var query = await rewriter.RewriteAsync(Question);

        Assert.Equal("Энтерпрайз NCC-1701-D капитан", query);
        var request = Assert.Single(chat.Requests);
        Assert.Equal(Question, request.Single(message => message.Role == ChatRole.User).Text);
        Assert.Equal(0.1f, chat.LastOptions!.Temperature);
        Assert.Equal("qwen3.5:4b", chat.LastOptions.ModelId);
        Assert.Equal(ReasoningEffort.None, chat.LastOptions.Reasoning!.Effort);
    }

    [Fact]
    public async Task EmptyResponseFallsBackToOriginalQuestion()
    {
        var logger = new ListLogger<QueryRewriter>();
        var chat = new ScriptedChatClient { ResponseText = "  \n" };
        var rewriter = Create(chat, logger);

        var query = await rewriter.RewriteAsync(Question);

        Assert.Equal(Question, query);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("пустой"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankQuestionIsReturnedWithoutCallingModel(string question)
    {
        var chat = new ScriptedChatClient { ResponseText = "Спок" };
        var rewriter = Create(chat);

        var query = await rewriter.RewriteAsync(question);

        Assert.Equal(question, query);
        Assert.Empty(chat.Requests);
    }

    [Fact]
    public async Task ExceptionFallsBackToOriginalQuestion()
    {
        var logger = new ListLogger<QueryRewriter>();
        var chat = new ScriptedChatClient { Error = new HttpRequestException("Ollama не отвечает") };
        var rewriter = Create(chat, logger);

        var query = await rewriter.RewriteAsync(Question);

        Assert.Equal(Question, query);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning &&
            entry.Exception is HttpRequestException &&
            entry.Message.Contains("исходный вопрос"));
    }

    [Fact]
    public async Task TimeoutFallsBackToOriginalQuestion()
    {
        var logger = new ListLogger<QueryRewriter>();
        var chat = new ScriptedChatClient { Error = new TaskCanceledException("timeout") };
        var rewriter = Create(chat, logger);

        var query = await rewriter.RewriteAsync(Question);

        Assert.Equal(Question, query);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Exception is TaskCanceledException);
    }

    [Fact]
    public async Task CallerCancellationIsNotSwallowed()
    {
        var chat = new ScriptedChatClient { ResponseText = "Спок" };
        var rewriter = Create(chat);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rewriter.RewriteAsync(Question, cancellation.Token));
        Assert.Empty(chat.Requests);
    }

    [Theory]
    [InlineData("Query: Энтерпрайз NCC-1701-D", "Энтерпрайз NCC-1701-D")]
    [InlineData("\"Спок\"", "Спок")]
    [InlineData("Query: \"NCC-1701-D\"", "NCC-1701-D")]
    [InlineData("Запрос: Вулканцы", "Вулканцы")]
    public async Task StripsQuotesAndQueryPrefix(string response, string expected)
    {
        var chat = new ScriptedChatClient { ResponseText = response };
        var rewriter = Create(chat);

        Assert.Equal(expected, await rewriter.RewriteAsync(Question));
    }

    [Fact]
    public async Task StripsThinkBlockBeforeQuery()
    {
        var chat = new ScriptedChatClient
        {
            ResponseText = """
                <think>
                Пользователь спрашивает про капитана. Нужны корабль и номер.
                </think>
                Query: "Энтерпрайз NCC-1701-D"
                """
        };
        var rewriter = Create(chat);

        Assert.Equal("Энтерпрайз NCC-1701-D", await rewriter.RewriteAsync(Question));
    }

    [Fact]
    public async Task ThinkOnlyResponseFallsBackToOriginalQuestion()
    {
        var logger = new ListLogger<QueryRewriter>();
        var chat = new ScriptedChatClient
        {
            ResponseText = "<think>рассуждение без запроса</think>"
        };
        var rewriter = Create(chat, logger);

        Assert.Equal(Question, await rewriter.RewriteAsync(Question));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task UnclosedThinkBlockFallsBackToOriginalQuestion()
    {
        var chat = new ScriptedChatClient { ResponseText = "<think>модель не закончила рассуждение" };
        var rewriter = Create(chat);

        Assert.Equal(Question, await rewriter.RewriteAsync(Question));
    }

    [Fact]
    public async Task OverlongRewriteFallsBackToOriginalQuestion()
    {
        var logger = new ListLogger<QueryRewriter>();
        var chat = new ScriptedChatClient { ResponseText = new string('а', 21) };
        var rewriter = Create(chat, logger, new LlmOptions { MaxRewrittenCharacters = 20 });

        Assert.Equal(Question, await rewriter.RewriteAsync(Question));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("длиннее"));
    }

    [Fact]
    public void AppSettingsUsesQwen35()
    {
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var configuration = new ConfigurationBuilder().AddJsonFile(settingsPath).Build();
        var llm = configuration.GetRequiredSection(LlmOptions.SectionName).Get<LlmOptions>();

        Assert.NotNull(llm);
        Assert.Equal("http://localhost:11434", llm.Endpoint);
        Assert.Equal("qwen3.5:4b", llm.Model);
        Assert.Equal(0.1, llm.Temperature);
        Assert.Equal(30, llm.TimeoutSeconds);
        Assert.True(llm.DisableThinking);
        var rag = configuration.GetRequiredSection(RagOptions.SectionName).Get<RagOptions>();
        Assert.NotNull(rag);
        Assert.Equal(2, rag.MinRelevantChunks);
        Assert.Equal(2, rag.MaxBroadenAttempts);
        Assert.Equal(0.015, rag.MinRrfScore);
    }

    [Fact]
    public void InvalidEndpointFailsOptionsValidation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Llm:Endpoint"] = "localhost:11434" })
            .Build();
        using var provider = new ServiceCollection().AddStarTrekLlm(configuration).BuildServiceProvider();

        var error = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<LlmOptions>>().Value);
        Assert.Contains("Llm:Endpoint", error.Message);
    }

    [Fact]
    public void RegistrationResolvesChatClientWithoutCallingOllama()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(IndexerMemoryKeys(root))
            .Build();
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddStarTrekSearchRead(configuration, root)
            .BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IChatClient>());
        Assert.NotNull(provider.GetRequiredService<IQueryRewriter>());
        Assert.NotNull(provider.GetRequiredService<IQueryBroadener>());
        Assert.NotNull(provider.GetRequiredService<IChunkRelevanceEvaluator>());
        Assert.NotNull(provider.GetRequiredService<CorrectiveRagRetrieval>());
        Assert.Equal(2, provider.GetRequiredService<IOptions<RagOptions>>().Value.MinRelevantChunks);
        Assert.Equal(2, provider.GetRequiredService<IOptions<RagOptions>>().Value.MaxBroadenAttempts);
        Assert.Equal("qwen3.5:4b", provider.GetRequiredService<IOptions<LlmOptions>>().Value.Model);
    }

    private static QueryRewriter Create(
        ScriptedChatClient chat,
        ListLogger<QueryRewriter>? logger = null,
        LlmOptions? options = null) =>
        new(chat, Options.Create(options ?? new LlmOptions()), logger ?? new ListLogger<QueryRewriter>());

    internal static Dictionary<string, string?> IndexerMemoryKeys(string root) => new()
    {
        ["Indexer:Paths:Corpus"] = "SourceFiles",
        ["Indexer:Paths:Staging"] = "Data/staging",
        ["Indexer:Paths:EmbeddingModel"] = "Models/multilingual-e5-small",
        ["Indexer:Paths:Bm25Corpus"] = Path.Combine(root, "missing.json"),
        ["Indexer:Chroma:Endpoint"] = "http://127.0.0.1:9",
        ["Indexer:Chroma:Collection"] = "startrek",
        ["Indexer:Chroma:Tenant"] = "default_tenant",
        ["Indexer:Chroma:Database"] = "default_database",
        ["Indexer:Bm25:K1"] = "1.5",
        ["Indexer:Bm25:B"] = "0.75",
        ["Indexer:Search:RrfK"] = "60",
        ["Indexer:Search:VectorTopN"] = "20",
        ["Indexer:Search:Bm25TopN"] = "20",
        ["Indexer:Search:ResultCount"] = "10"
    };
}

public sealed class CorrectiveRagRetrievalTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "startrek-rag-" + Guid.NewGuid().ToString("N"));
    private readonly FakeChromaServer server = new();
    private readonly FakeQueryEmbedder embedder = new();
    private readonly List<(string Previous, int Attempt)> broadenerCalls = [];

    public CorrectiveRagRetrievalTests()
    {
        var collection = server.AddCollection("startrek", dimension: 384);
        collection.Records["voyager:1"] = new FakeChromaRecord(
            "voyager:1",
            "Звездолёт Вояджер NCC-74656",
            FakeEmbeddingGenerator.CreateVector("Вояджер NCC-74656"),
            new JsonObject());
    }

    [Fact]
    public async Task RetrieveSearchesWithRewrittenQuery()
    {
        var chat = new ScriptedChatClient { ResponseText = "Вояджер NCC-74656" };
        var retrieval = await CreateAsync(chat);

        var result = await retrieval.RetrieveAsync("Расскажи, пожалуйста, про корабль Вояджер");

        Assert.Equal("Расскажи, пожалуйста, про корабль Вояджер", result.Question);
        Assert.Equal("Вояджер NCC-74656", result.SearchQuery);
        Assert.Equal(0, result.BroadenAttempts);
        Assert.Equal("Вояджер NCC-74656", result.Result.Query);
        Assert.Equal(["Вояджер NCC-74656"], embedder.Queries);
        Assert.Contains(result.Chunks, hit => hit.Id == "voyager:1");
        Assert.Empty(broadenerCalls);
    }

    [Fact]
    public async Task RetrieveKeepsOriginalQuestionWhenRewriteFails()
    {
        var chat = new ScriptedChatClient { Error = new HttpRequestException("нет связи") };
        var retrieval = await CreateAsync(chat);

        var result = await retrieval.RetrieveAsync("Вояджер NCC-74656");

        Assert.Equal("Вояджер NCC-74656", result.SearchQuery);
        Assert.Equal(0, result.BroadenAttempts);
        Assert.Equal(["Вояджер NCC-74656"], embedder.Queries);
    }

    [Fact]
    public async Task BlankQuestionDoesNotCallModelOrSearch()
    {
        var chat = new ScriptedChatClient { ResponseText = "Спок" };
        var retrieval = await CreateAsync(chat);

        await Assert.ThrowsAsync<ArgumentException>(() => retrieval.RetrieveAsync("  "));

        Assert.Empty(chat.Requests);
        Assert.Empty(embedder.Queries);
    }

    private async Task<CorrectiveRagRetrieval> CreateAsync(ScriptedChatClient chat)
    {
        var holder = new Bm25IndexHolder(
            new Bm25CorpusStore(Path.Combine(root, "corpus.json")),
            Bm25Parameters.Default);
        await new Bm25IndexRebuilder(holder).RebuildAsync(new Bm25RebuildRequest(
            "startrek",
            [new Bm25Chunk("voyager:1", "Звездолёт Вояджер NCC-74656")],
            CollectionChanged: true));
        var chroma = new ChromaOptions { Endpoint = "http://127.0.0.1:8000", Collection = "startrek" };
        var hybrid = new HybridSearchService(
            new ChromaHttpClient(new HttpClient(server, disposeHandler: false), chroma),
            embedder,
            holder,
            Options.Create(new IndexerOptions { Chroma = chroma, Search = new SearchOptions() }));
        var rewriter = new QueryRewriter(chat, Options.Create(new LlmOptions()), new ListLogger<QueryRewriter>());
        var broadener = new RecordingBroadener(broadenerCalls);
        var relevance = new RrfScoreRelevanceEvaluator(Options.Create(new RagOptions
        {
            MinRelevantChunks = 1,
            MinRrfScore = 0
        }));
        return new CorrectiveRagRetrieval(
            rewriter,
            broadener,
            relevance,
            hybrid,
            Options.Create(new RagOptions { MinRelevantChunks = 1, MinRrfScore = 0 }),
            new ListLogger<CorrectiveRagRetrieval>());
    }

    public void Dispose()
    {
        server.Dispose();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

internal sealed class ScriptedChatClient : IChatClient
{
    public string ResponseText { get; init; } = string.Empty;

    public Exception? Error { get; init; }

    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    public ChatOptions? LastOptions { get; private set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(messages.ToArray());
        LastOptions = options;
        if (Error is not null)
        {
            throw Error;
        }

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ResponseText)));
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

internal sealed class RecordingBroadener(List<(string Previous, int Attempt)> calls) : IQueryBroadener
{
    public Task<string> BroadenAsync(
        string originalQuestion,
        string previousQuery,
        int attempt,
        CancellationToken cancellationToken = default)
    {
        calls.Add((previousQuery, attempt));
        return Task.FromResult(previousQuery + " шире");
    }
}

internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception), exception));
}
