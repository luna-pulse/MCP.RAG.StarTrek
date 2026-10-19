using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StarTrek.Indexer.Chroma;
using StarTrek.Indexer.Configuration;
using StarTrek.Indexer.Rag;
using StarTrek.Indexer.Search;
using StarTrek.Mcp.Rag;

namespace StarTrek.Mcp.Tests;

public sealed class AskQuestionServiceTests
{
    private const string Question = "Как назывался вулканский корабль первого контакта?";

    [Fact]
    public async Task EnoughImmediatelyAnswersWithoutBroadening()
    {
        var search = new ScriptedHybridSearch(_ =>
        [
            Hit("ship:8", "Корабль Т'Плана-Хат встретил Феникс.", 0.05),
            Hit("ship:9", "Дата контакта — 5 апреля 2063 года.", 0.04)
        ]);
        var broadener = new ScriptedBroadener();
        var chat = new ScriptedChat("Вулканский корабль первого контакта назывался Т'Плана-Хат.");
        var service = Create(search, broadener, chat, "вулканский корабль первого контакта");

        var response = await service.AskAsync(Question);

        Assert.Contains("Т'Плана-Хат", response.Answer, StringComparison.Ordinal);
        Assert.DoesNotContain("&#39;", response.Answer, StringComparison.Ordinal);
        Assert.Equal(1, response.Meta!.Attempts);
        Assert.Equal("вулканский корабль первого контакта", response.Meta.RewrittenQuery);
        Assert.Equal(response.Meta.RewrittenQuery, response.Meta.Query);
        Assert.Empty(broadener.Calls);
        Assert.Equal(["вулканский корабль первого контакта"], search.Queries);
        Assert.Equal(1, chat.Calls);
        var passage = Assert.Single(chat.Requests)[1].Text;
        Assert.Contains("Т'Плана-Хат", passage, StringComparison.Ordinal);
        Assert.DoesNotContain("&#39;", passage, StringComparison.Ordinal);
        Assert.Equal(2, response.Sources.Count);
        Assert.Equal("SourceFiles/Races/Вулканцы.txt", response.Sources[0].SourceFile);
        Assert.Equal("lines 80-90", response.Sources[0].Position);
    }

    [Fact]
    public async Task OneBroadenThenAnswers()
    {
        var search = new ScriptedHybridSearch(query => query == "узкий"
            ?
            [
                Hit("ship:8", "Корабль Т'Плана-Хат.", 0.05)
            ]
            :
            [
                Hit("ship:8", "Корабль Т'Плана-Хат встретил Феникс.", 0.05),
                Hit("ship:9", "Дата контакта — 5 апреля 2063 года.", 0.04)
            ]);
        var broadener = new ScriptedBroadener("вулканский корабль");
        var chat = new ScriptedChat("Корабль назывался Т'Плана-Хат.");
        var service = Create(search, broadener, chat, "узкий");

        var response = await service.AskAsync(Question);

        Assert.Contains("Т'Плана-Хат", response.Answer, StringComparison.Ordinal);
        Assert.Equal(2, response.Meta!.Attempts);
        Assert.Equal("вулканский корабль", response.Meta.Query);
        Assert.Equal("вулканский корабль", response.Meta.RewrittenQuery);
        Assert.Equal(["узкий", "вулканский корабль"], search.Queries);
        Assert.Equal([( "узкий", 1 )], broadener.Calls);
        Assert.Equal(1, chat.Calls);
        Assert.Equal(2, response.Sources.Count);
    }

    [Fact]
    public async Task TwoBroadensThenStopsWithoutCallingTheModel()
    {
        var search = new ScriptedHybridSearch(_ => [Hit("low:1", "нерелевантный текст", 0.001)]);
        var broadener = new ScriptedBroadener("шире один", "шире два");
        var chat = new ScriptedChat("не должен вызваться");
        var service = Create(search, broadener, chat, "узкий");

        var response = await service.AskAsync(Question);

        Assert.Equal(AskQuestionService.NotFoundAnswer, response.Answer);
        Assert.Equal(3, response.Meta!.Attempts);
        Assert.Equal("шире два", response.Meta.Query);
        Assert.Empty(response.Sources);
        Assert.Equal(0, chat.Calls);
        Assert.Equal(["узкий", "шире один", "шире два"], search.Queries);
        Assert.Equal(2, broadener.Calls.Count);
    }

    [Fact]
    public async Task PartialRelevantChunksAfterTwoBroadensStillGenerate()
    {
        var search = new ScriptedHybridSearch(query => query == "шире два"
            ? [Hit("ship:8", "Корабль Т'Плана-Хат.", 0.05)]
            : [Hit("low:1", "мимо", 0.001)]);
        var broadener = new ScriptedBroadener("шире один", "шире два");
        var chat = new ScriptedChat("Корабль назывался Т'Плана-Хат.");
        var service = Create(search, broadener, chat, "узкий");

        var response = await service.AskAsync(Question);

        Assert.Contains("Т'Плана-Хат", response.Answer, StringComparison.Ordinal);
        Assert.Equal(3, response.Meta!.Attempts);
        Assert.Single(response.Sources);
        Assert.Equal("SourceFiles/Races/Вулканцы.txt", response.Sources[0].SourceFile);
        Assert.Equal(1, chat.Calls);
    }

    [Fact]
    public async Task BlankDocumentsDoNotCountAsRelevant()
    {
        var search = new ScriptedHybridSearch(_ =>
        [
            Hit("ship:8", "   ", 0.05),
            Hit("ship:9", "", 0.04)
        ]);
        var chat = new ScriptedChat("не должен вызваться");
        var service = Create(search, new ScriptedBroadener("шире один", "шире два"), chat, "запрос");

        var response = await service.AskAsync(Question);

        Assert.Equal(AskQuestionService.NotFoundAnswer, response.Answer);
        Assert.Empty(response.Sources);
        Assert.Equal(0, chat.Calls);
    }

    [Fact]
    public async Task ChatConnectionFailureBecomesOllamaMessage()
    {
        var search = new ScriptedHybridSearch(_ =>
        [
            Hit("ship:8", "Корабль Т'Плана-Хат.", 0.05),
            Hit("ship:9", "Дата контакта.", 0.04)
        ]);
        var chat = new ScriptedChat(new HttpRequestException("Connection refused"));
        var service = Create(search, new ScriptedBroadener(), chat, "запрос");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.AskAsync(Question));
        Assert.Equal(AskQuestionService.UnavailableMessage("http://127.0.0.1:9"), error.Message);
    }

    private static AskQuestionService Create(
        IHybridSearch search,
        IQueryBroadener broadener,
        IChatClient chat,
        string rewritten)
    {
        var rag = Options.Create(new RagOptions());
        var llm = Options.Create(new LlmOptions
        {
            Endpoint = "http://127.0.0.1:9",
            Model = "qwen3.5:4b"
        });
        var evaluator = new RrfScoreRelevanceEvaluator(rag);
        var retrieval = new CorrectiveRagRetrieval(
            new FixedRewriter(rewritten),
            broadener,
            evaluator,
            search,
            rag,
            NullLogger<CorrectiveRagRetrieval>.Instance);
        return new AskQuestionService(retrieval, evaluator, chat, llm);
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

    private sealed class FixedRewriter(string query) : IQueryRewriter
    {
        public Task<string> RewriteAsync(string question, CancellationToken cancellationToken = default) =>
            Task.FromResult(query);
    }

    private sealed class ScriptedBroadener(params string[] queries) : IQueryBroadener
    {
        public List<(string Previous, int Attempt)> Calls { get; } = [];

        public Task<string> BroadenAsync(
            string originalQuestion,
            string previousQuery,
            int attempt,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((previousQuery, attempt));
            return Task.FromResult(queries[Calls.Count - 1]);
        }
    }

    private sealed class ScriptedHybridSearch(Func<string, IReadOnlyList<HybridSearchHit>> hits) : IHybridSearch
    {
        public List<string> Queries { get; } = [];

        public Task<HybridSearchResult> SearchAsync(
            string query,
            int? resultCount = null,
            CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            var found = hits(query);
            return Task.FromResult(new HybridSearchResult(query, found, found.Count, found.Count));
        }
    }

    private sealed class ScriptedChat : IChatClient
    {
        private readonly string _text = string.Empty;
        private readonly Exception? _error;

        public ScriptedChat(string text) => _text = text;

        public ScriptedChat(Exception error) => _error = error;

        public int Calls { get; private set; }

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var request = messages.ToArray();
            Requests.Add(request);
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
