using Microsoft.Extensions.Options;
using StarTrek.Indexer.Chroma;
using StarTrek.Indexer.Search;
using StarTrek.Mcp.Rag;

namespace StarTrek.Mcp.Tests;

public sealed class CorrectiveRagServiceTests
{
    [Fact]
    public async Task EnoughImmediatelyGeneratesWithoutBroaden()
    {
        var chat = Chat((function, _) => function switch
        {
            PromptCatalog.Rewrite => "вулканский корабль первого контакта",
            PromptCatalog.Grade => "да",
            PromptCatalog.Generate => "Вулканский корабль первого контакта назывался Т'Плана-Хат.",
            _ => throw new InvalidOperationException(function)
        });
        var retriever = Retriever(_ =>
        [
            Hit("ship:1", "Корабль первого контакта — Т'Плана-Хат."),
            Hit("ship:2", "Контакт состоялся 5 апреля 2063 года.")
        ]);
        var response = await Service(chat, retriever).AskAsync("Как назывался вулканский корабль первого контакта?");

        Assert.Equal(1, response.Meta!.Attempts);
        Assert.Equal("вулканский корабль первого контакта", response.Meta.RewrittenQuery);
        Assert.Equal(response.Meta.RewrittenQuery, response.Meta.Query);
        Assert.Equal(0, chat.Count(PromptCatalog.Broaden));
        Assert.Equal(1, chat.Count(PromptCatalog.Generate));
        Assert.Equal(2, chat.Count(PromptCatalog.Grade));
        Assert.Contains("Т'Плана-Хат", response.Answer, StringComparison.Ordinal);
        Assert.Equal(2, response.Sources.Count);
        Assert.Equal("SourceFiles/Races/Вулканцы.txt", response.Sources[0].SourceFile);
        Assert.Equal("lines 12-40", response.Sources[0].Position);
        Assert.Equal(4, response.Sources[0].ChunkIndex);
        Assert.Single(retriever.Queries);
    }

    [Fact]
    public async Task OneBroadenThenEnough()
    {
        var pass = 0;
        var retriever = Retriever(_ =>
        {
            pass++;
            return pass == 1
                ? [Hit("ship:1", "Короткий фрагмент.")]
                : [Hit("ship:1", "Короткий фрагмент."), Hit("ship:2", "Т'Плана-Хат.")];
        });
        var chat = Chat((function, _) => function switch
        {
            PromptCatalog.Rewrite => "узкий запрос",
            PromptCatalog.Grade => "да",
            PromptCatalog.Broaden => "Т'Плана-Хат вулканцы первый контакт",
            PromptCatalog.Generate => "Корабль назывался Т'Плана-Хат.",
            _ => throw new InvalidOperationException(function)
        });

        var response = await Service(chat, retriever).AskAsync("Как назывался корабль?");

        Assert.Equal(2, response.Meta!.Attempts);
        Assert.Equal("узкий запрос", response.Meta.RewrittenQuery);
        Assert.Equal("Т'Плана-Хат вулканцы первый контакт", response.Meta.Query);
        Assert.Equal(1, chat.Count(PromptCatalog.Broaden));
        Assert.Equal(1, chat.Count(PromptCatalog.Generate));
        Assert.Equal(["узкий запрос", "Т'Плана-Хат вулканцы первый контакт"], retriever.Queries);
        Assert.Equal(2, response.Sources.Count);
        Assert.Contains("Т'Плана-Хат", response.Answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoBroadensThenStopWithoutRelevantChunks()
    {
        var retriever = Retriever(_ => [Hit("other:1", "Соседний факт.")]);
        var chat = Chat((function, variables) => function switch
        {
            PromptCatalog.Rewrite => "первый",
            PromptCatalog.Grade => "нет",
            PromptCatalog.Broaden => variables["query"] == "первый" ? "второй" : "третий",
            PromptCatalog.Generate => throw new InvalidOperationException("Generate не должен вызываться"),
            _ => throw new InvalidOperationException(function)
        });

        var response = await Service(chat, retriever).AskAsync("Чего нет в базе?");

        Assert.Equal(CorrectiveRagService.NotFoundAnswer, response.Answer);
        Assert.Empty(response.Sources);
        Assert.Equal(3, response.Meta!.Attempts);
        Assert.Equal("первый", response.Meta.RewrittenQuery);
        Assert.Equal("третий", response.Meta.Query);
        Assert.Equal(2, chat.Count(PromptCatalog.Broaden));
        Assert.Equal(0, chat.Count(PromptCatalog.Generate));
        Assert.Equal(["первый", "второй", "третий"], retriever.Queries);
        Assert.Equal(3, chat.Count(PromptCatalog.Grade));
    }

    [Fact]
    public async Task TwoBroadensThenGenerateFromTheFewRelevantChunks()
    {
        var pass = 0;
        var retriever = Retriever(_ =>
        {
            pass++;
            return pass < 3
                ? [Hit("other:1", "Соседний факт.")]
                : [Hit("ship:1", "Корабль назывался Т'Плана-Хат.")];
        });
        var chat = Chat((function, variables) => function switch
        {
            PromptCatalog.Rewrite => "узко",
            PromptCatalog.Broaden => variables["query"] == "узко" ? "шире" : "ещё шире",
            PromptCatalog.Grade => variables["chunk"].Contains("Т'Плана", StringComparison.Ordinal) ? "да" : "нет",
            PromptCatalog.Generate => "Т'Плана-Хат.",
            _ => throw new InvalidOperationException(function)
        });

        var response = await Service(chat, retriever).AskAsync("Как назывался корабль?");

        Assert.Equal(3, response.Meta!.Attempts);
        Assert.Equal(2, chat.Count(PromptCatalog.Broaden));
        Assert.Equal(1, chat.Count(PromptCatalog.Generate));
        Assert.Equal("Т'Плана-Хат.", response.Answer);
        var source = Assert.Single(response.Sources);
        Assert.Equal("ship:1", source.Id);
        Assert.Equal("SourceFiles/Races/Вулканцы.txt", source.SourceFile);
    }

    [Fact]
    public async Task BlankChunksAreNotGraded()
    {
        var retriever = Retriever(_ =>
        [
            Hit("empty:1", "   "),
            Hit("ship:1", "Т'Плана-Хат."),
            Hit("ship:2", "5 апреля 2063 года.")
        ]);
        var chat = Chat((function, _) => function switch
        {
            PromptCatalog.Rewrite => "запрос",
            PromptCatalog.Grade => "да",
            PromptCatalog.Generate => "Т'Плана-Хат.",
            _ => throw new InvalidOperationException(function)
        });

        var response = await Service(chat, retriever).AskAsync("Корабль?");

        Assert.Equal(2, chat.Count(PromptCatalog.Grade));
        Assert.Equal(1, response.Meta!.Attempts);
        Assert.Equal(2, response.Sources.Count);
    }

    [Fact]
    public async Task InvalidThresholdsFailBeforeTheModel()
    {
        var chat = Chat((_, _) => throw new InvalidOperationException("модель не нужна"));
        var service = new CorrectiveRagService(Retriever(_ => []), chat, Options.Create(new RagOptions
        {
            MinRelevantChunks = 0,
            MaxBroadenAttempts = 2,
            RetrieveCount = 4
        }));

        var error = await Assert.ThrowsAsync<RagDependencyException>(() => service.AskAsync("вопрос"));
        Assert.Contains("MinRelevantChunks", error.Message, StringComparison.Ordinal);
        Assert.Empty(chat.Calls);
    }

    private static CorrectiveRagService Service(ScriptedRagChat chat, ScriptedRetriever retriever) =>
        new(retriever, chat, Options.Create(new RagOptions
        {
            MinRelevantChunks = 2,
            MaxBroadenAttempts = 2,
            RetrieveCount = 4
        }));

    private static ScriptedRagChat Chat(Func<string, IReadOnlyDictionary<string, string>, string> respond) =>
        new() { Respond = respond };

    private static ScriptedRetriever Retriever(Func<string, IReadOnlyList<HybridSearchHit>> respond) =>
        new() { Respond = respond };

    private static HybridSearchHit Hit(string id, string? text) =>
        new(
            id,
            0.03,
            1,
            0.2,
            1,
            3,
            text,
            new Dictionary<string, object?>
            {
                [ChromaMetadataKeys.SourceFile] = "SourceFiles/Races/Вулканцы.txt",
                [ChromaMetadataKeys.Position] = "lines 12-40",
                [ChromaMetadataKeys.ChunkIndex] = 4,
                [ChromaMetadataKeys.Title] = "Вулканцы",
                [ChromaMetadataKeys.SectionPath] = "История"
            });

    private sealed class ScriptedRagChat : IRagChat
    {
        public List<(string Function, Dictionary<string, string> Variables)> Calls { get; } = [];

        public required Func<string, IReadOnlyDictionary<string, string>, string> Respond { get; init; }

        public Task<string> InvokeAsync(
            string functionName,
            IReadOnlyDictionary<string, string> variables,
            CancellationToken cancellationToken)
        {
            Calls.Add((functionName, new Dictionary<string, string>(variables)));
            return Task.FromResult(Respond(functionName, variables));
        }

        public int Count(string functionName) => Calls.Count(call => call.Function == functionName);
    }

    private sealed class ScriptedRetriever : ICorpusRetriever
    {
        public List<string> Queries { get; } = [];

        public required Func<string, IReadOnlyList<HybridSearchHit>> Respond { get; init; }

        public Task<HybridSearchResult> SearchAsync(string query, int resultCount, CancellationToken cancellationToken)
        {
            Queries.Add(query);
            var hits = Respond(query).Take(resultCount).ToList();
            return Task.FromResult(new HybridSearchResult(query, hits, hits.Count, hits.Count));
        }
    }
}
