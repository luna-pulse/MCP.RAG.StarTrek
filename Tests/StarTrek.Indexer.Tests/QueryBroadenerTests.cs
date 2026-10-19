using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StarTrek.Indexer.Configuration;
using StarTrek.Indexer.Rag;
using StarTrek.Indexer.Search;

namespace StarTrek.Indexer.Tests;

public sealed class QueryBroadenerTests
{
    private const string Question = "Кто капитан Энтерпрайза NCC-1701-D?";
    private const string Previous = "Энтерпрайз NCC-1701-D капитан";

    [Fact]
    public async Task BroadensWithSynonymsAndDisablesThinking()
    {
        var chat = new ScriptedChatClient { ResponseText = "звёздный флот флагман" };
        var broadener = Create(chat);

        var query = await broadener.BroadenAsync(Question, Previous, attempt: 1);

        Assert.Equal("звёздный флот флагман", query);
        var request = Assert.Single(chat.Requests);
        var user = request.Single(message => message.Role == ChatRole.User).Text;
        Assert.Contains(Previous, user, StringComparison.Ordinal);
        Assert.Contains("Attempt: 1", user, StringComparison.Ordinal);
        Assert.Equal(ReasoningEffort.None, chat.LastOptions!.Reasoning!.Effort);
        Assert.Equal("qwen3.5:4b", chat.LastOptions.ModelId);
    }

    [Fact]
    public async Task StripsThinkBlockAndBroadenedPrefix()
    {
        var chat = new ScriptedChatClient
        {
            ResponseText = """
                <think>
                Нужны синонимы и английское имя корабля.
                </think>
                Расширенный запрос: "Enterprise NCC-1701-D"
                """
        };
        var broadener = Create(chat);

        Assert.Equal("Enterprise NCC-1701-D", await broadener.BroadenAsync(Question, Previous, 2));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n")]
    [InlineData("<think>только рассуждение</think>")]
    public async Task EmptyResponseUsesShorterQuery(string response)
    {
        var logger = new ListLogger<QueryBroadener>();
        var chat = new ScriptedChatClient { ResponseText = response };
        var broadener = Create(chat, logger);

        var query = await broadener.BroadenAsync(Question, Previous, 1);

        Assert.Equal("Энтерпрайз NCC-1701-D", query);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("укороченный"));
    }

    [Fact]
    public async Task RepeatedQueryUsesShorterQuery()
    {
        var chat = new ScriptedChatClient { ResponseText = "Query: Энтерпрайз NCC-1701-D капитан" };
        var broadener = Create(chat);

        Assert.Equal("Энтерпрайз NCC-1701-D", await broadener.BroadenAsync(Question, Previous, 1));
    }

    [Fact]
    public async Task ExceptionKeepsSingleTokenQuery()
    {
        var logger = new ListLogger<QueryBroadener>();
        var chat = new ScriptedChatClient { Error = new HttpRequestException("нет связи") };
        var broadener = Create(chat, logger);

        var query = await broadener.BroadenAsync(Question, "Спок", 1);

        Assert.Equal("Спок", query);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning &&
            entry.Exception is HttpRequestException &&
            entry.Message.Contains("предыдущий запрос"));
    }

    [Fact]
    public async Task TimeoutUsesShorterQuery()
    {
        var chat = new ScriptedChatClient { Error = new TaskCanceledException("timeout") };
        var broadener = Create(chat);

        Assert.Equal("Энтерпрайз NCC-1701-D", await broadener.BroadenAsync(Question, Previous, 1));
    }

    [Fact]
    public async Task OverlongResponseUsesShorterQuery()
    {
        var chat = new ScriptedChatClient { ResponseText = new string('а', 21) };
        var broadener = Create(chat, options: new LlmOptions { MaxRewrittenCharacters = 20 });

        Assert.Equal("Энтерпрайз NCC-1701-D", await broadener.BroadenAsync(Question, Previous, 1));
    }

    [Fact]
    public async Task CallerCancellationIsNotSwallowed()
    {
        var chat = new ScriptedChatClient { ResponseText = "флот" };
        var broadener = Create(chat);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            broadener.BroadenAsync(Question, Previous, 1, cancellation.Token));
        Assert.Empty(chat.Requests);
    }

    private static QueryBroadener Create(
        ScriptedChatClient chat,
        ListLogger<QueryBroadener>? logger = null,
        LlmOptions? options = null) =>
        new(chat, Options.Create(options ?? new LlmOptions()), logger ?? new ListLogger<QueryBroadener>());
}

public sealed class CorrectiveRagLoopTests
{
    [Fact]
    public async Task StopsWhenFirstSearchHasEnoughRelevantChunks()
    {
        var search = new ScriptedHybridSearch(_ => Hits("a", "b"));
        var broadener = new QueueBroadener("шире");
        var retrieval = Create(search, broadener, relevantAtLeast: 2);

        var result = await retrieval.RetrieveAsync("Кто такой Спок?");

        Assert.Equal("спок", result.SearchQuery);
        Assert.Equal(0, result.BroadenAttempts);
        Assert.Equal(["спок"], search.Queries);
        Assert.Empty(broadener.Calls);
        Assert.Equal(["a", "b"], result.Chunks.Select(hit => hit.Id).ToArray());
    }

    [Fact]
    public async Task BroadensAtMostTwice()
    {
        var search = new ScriptedHybridSearch(query => Hits(query));
        var broadener = new QueueBroadener("шире-1", "шире-2", "шире-3");
        var retrieval = Create(search, broadener, relevantAtLeast: 5, maxAttempts: 2);

        var result = await retrieval.RetrieveAsync("вопрос");

        Assert.Equal(2, result.BroadenAttempts);
        Assert.Equal(["спок", "шире-1", "шире-2"], search.Queries);
        Assert.Equal([1, 2], broadener.Calls.Select(call => call.Attempt).ToArray());
        Assert.Equal("шире-2", result.SearchQuery);
        Assert.Equal(3, result.Chunks.Count);
    }

    [Fact]
    public async Task StopsEarlyWhenBroadeningFindsEnough()
    {
        var search = new ScriptedHybridSearch(query => query == "спок" ? Hits("a") : Hits("a", "b", "c"));
        var broadener = new QueueBroadener("шире-1", "шире-2");
        var retrieval = Create(search, broadener, relevantAtLeast: 2);

        var result = await retrieval.RetrieveAsync("вопрос");

        Assert.Equal(1, result.BroadenAttempts);
        Assert.Equal(["спок", "шире-1"], search.Queries);
        Assert.Equal("шире-1", result.SearchQuery);
        Assert.Single(broadener.Calls);
    }

    [Fact]
    public async Task IdenticalFallbackDoesNotSearchAgain()
    {
        var chat = new ScriptedChatClient { Error = new HttpRequestException("timeout") };
        var search = new ScriptedHybridSearch(_ => Hits("a"));
        var retrieval = Create(
            search,
            new QueryBroadener(chat, Options.Create(new LlmOptions()), new ListLogger<QueryBroadener>()),
            relevantAtLeast: 2,
            rewritten: "Спок");

        var result = await retrieval.RetrieveAsync("Кто такой Спок?");

        Assert.Equal(["Спок"], search.Queries);
        Assert.Equal(0, result.BroadenAttempts);
        Assert.Equal("Спок", result.SearchQuery);
    }

    [Fact]
    public async Task FailedBroadenSearchesTheShorterQuery()
    {
        var chat = new ScriptedChatClient { Error = new TaskCanceledException("timeout") };
        var search = new ScriptedHybridSearch(_ => new HybridSearchResult("", [], 0, 0));
        var retrieval = Create(
            search,
            new QueryBroadener(chat, Options.Create(new LlmOptions()), new ListLogger<QueryBroadener>()),
            relevantAtLeast: 2,
            maxAttempts: 1,
            rewritten: "Энтерпрайз NCC-1701-D капитан");

        var result = await retrieval.RetrieveAsync("Кто капитан?");

        Assert.Equal(["Энтерпрайз NCC-1701-D капитан", "Энтерпрайз NCC-1701-D"], search.Queries);
        Assert.Equal(1, result.BroadenAttempts);
        Assert.Equal("Энтерпрайз NCC-1701-D", result.SearchQuery);
    }

    [Fact]
    public async Task MergesChunksAndKeepsHigherScore()
    {
        var search = new ScriptedHybridSearch(query => query == "спок"
            ? Hits(("a", 0.02), ("b", 0.01))
            : Hits(("a", 0.05), ("c", 0.04)));
        var broadener = new QueueBroadener("шире");
        var retrieval = Create(search, broadener, relevantAtLeast: 10, maxAttempts: 1);

        var result = await retrieval.RetrieveAsync("вопрос");

        Assert.Equal(["a", "c", "b"], result.Chunks.Select(hit => hit.Id).ToArray());
        Assert.Equal(0.05, result.Chunks[0].Score);
        Assert.Equal(3, result.Chunks.Count);
    }

    [Fact]
    public async Task ScoreThresholdDropsWeakerHits()
    {
        var evaluator = new RrfScoreRelevanceEvaluator(Options.Create(new RagOptions { MinRrfScore = 0.015 }));
        var hits = new[]
        {
            Hit("high", 0.02),
            Hit("edge", 0.015),
            Hit("low", 0.014)
        };

        var relevant = await evaluator.SelectRelevantAsync("вопрос", "запрос", hits);

        Assert.Equal(["high", "edge"], relevant.Select(hit => hit.Id).ToArray());
    }

    private static CorrectiveRagRetrieval Create(
        ScriptedHybridSearch search,
        IQueryBroadener broadener,
        int relevantAtLeast,
        int maxAttempts = 2,
        string rewritten = "спок")
    {
        var options = Options.Create(new RagOptions
        {
            MinRelevantChunks = relevantAtLeast,
            MaxBroadenAttempts = maxAttempts,
            MinRrfScore = 0
        });
        return new CorrectiveRagRetrieval(
            new FixedRewriter(rewritten),
            broadener,
            new CountingRelevance(),
            search,
            options,
            new ListLogger<CorrectiveRagRetrieval>());
    }

    private static HybridSearchResult Hits(params string[] ids) =>
        Hits(ids.Select(id => (id, 0.02)).ToArray());

    private static HybridSearchResult Hits(params (string Id, double Score)[] hits) =>
        new(
            "q",
            hits.Select(hit => Hit(hit.Id, hit.Score)).ToArray(),
            hits.Length,
            hits.Length);

    private static HybridSearchHit Hit(string id, double score) =>
        new(id, score, 1, 0.1, null, null, id, new Dictionary<string, object?>());

    private sealed class FixedRewriter(string query) : IQueryRewriter
    {
        public Task<string> RewriteAsync(string question, CancellationToken cancellationToken = default) =>
            Task.FromResult(query);
    }

    private sealed class QueueBroadener(params string[] responses) : IQueryBroadener
    {
        private readonly Queue<string> pending = new(responses);

        public List<(string Previous, int Attempt)> Calls { get; } = [];

        public Task<string> BroadenAsync(
            string originalQuestion,
            string previousQuery,
            int attempt,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((previousQuery, attempt));
            return Task.FromResult(pending.Dequeue());
        }
    }

    private sealed class CountingRelevance : IChunkRelevanceEvaluator
    {
        public Task<IReadOnlyList<HybridSearchHit>> SelectRelevantAsync(
            string question,
            string searchQuery,
            IReadOnlyList<HybridSearchHit> hits,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(hits);
    }

    private sealed class ScriptedHybridSearch(Func<string, HybridSearchResult> respond) : IHybridSearch
    {
        public List<string> Queries { get; } = [];

        public Task<HybridSearchResult> SearchAsync(
            string query,
            int? resultCount = null,
            CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            return Task.FromResult(respond(query));
        }
    }
}
