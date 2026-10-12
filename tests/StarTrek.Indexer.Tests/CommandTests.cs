using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StarTrek.Indexer.Bm25;
using StarTrek.Indexer.Chroma;
using StarTrek.Indexer.Commands;
using StarTrek.Indexer.Configuration;
using StarTrek.Indexer.Parsing;
using StarTrek.Indexer.Tokenization;

namespace StarTrek.Indexer.Tests;

public sealed class CommandTests
{
    [Theory]
    [InlineData("unknown")]
    [InlineData("index")]
    [InlineData("search")]
    [InlineData("ask")]
    public void UnknownCommandReturnsNonZeroExitCode(string command)
    {
        var dispatcher = new CommandDispatcher(new ServiceCollection().BuildServiceProvider());

        Assert.NotEqual(0, dispatcher.Execute([command]));
    }

    [Fact]
    public void MissingCommandReturnsNonZeroExitCode()
    {
        var dispatcher = new CommandDispatcher(new ServiceCollection().BuildServiceProvider());

        Assert.NotEqual(0, dispatcher.Execute([]));
    }

    [Fact]
    public void UnknownAnalyzeOptionReturnsNonZeroExitCode()
    {
        var dispatcher = new CommandDispatcher(new ServiceCollection().BuildServiceProvider());

        Assert.NotEqual(0, dispatcher.Execute(["analyze", "--write"]));
    }

    [Fact]
    public void UnknownEmbedOptionReturnsNonZeroExitCode()
    {
        var dispatcher = new CommandDispatcher(new ServiceCollection().BuildServiceProvider());

        Assert.NotEqual(0, dispatcher.Execute(["embed", "--all"]));
    }

    [Fact]
    public void EmbedCommandResolvesWithoutLoadingModel()
    {
        var emptyModelDirectory = Directory.CreateTempSubdirectory("startrek-embed-wiring-");
        try
        {
            using var services = TestEnvironment.BuildIndexerServices(new Dictionary<string, string?>
            {
                ["Indexer:Paths:EmbeddingModel"] = emptyModelDirectory.FullName
            });

            Assert.NotNull(services.GetRequiredService<EmbedCommand>());
            Assert.Equal(new Uri("http://127.0.0.1:8000/"), services.GetRequiredService<ChromaHttpClient>().Endpoint);
            Assert.IsType<Bm25IndexRebuilder>(services.GetRequiredService<IBm25IndexRebuilder>());
        }
        finally
        {
            emptyModelDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Bm25AndSearchParametersComeFromConfiguration()
    {
        using var services = TestEnvironment.BuildIndexerServices(new Dictionary<string, string?>
        {
            ["Indexer:Bm25:K1"] = "1.2",
            ["Indexer:Bm25:B"] = "0.5",
            ["Indexer:Search:RrfK"] = "30"
        });

        var holder = services.GetRequiredService<Bm25IndexHolder>();

        Assert.Equal(new Bm25Parameters(K1: 1.2, B: 0.5), holder.Parameters);
        Assert.Equal(30, services.GetRequiredService<IOptions<IndexerOptions>>().Value.Search.RrfK);
    }

    [Theory]
    [InlineData("Indexer:Bm25:B", "1.5")]
    [InlineData("Indexer:Bm25:K1", "-1")]
    [InlineData("Indexer:Search:RrfK", "0")]
    public void InvalidBm25OrSearchParametersFailOptionsValidation(string key, string value)
    {
        using var services = TestEnvironment.BuildIndexerServices(new Dictionary<string, string?> { [key] = value });

        Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<Bm25IndexHolder>());
    }

    [Fact]
    public void InvalidChromaEndpointFailsOptionsValidation()
    {
        using var services = TestEnvironment.BuildIndexerServices(new Dictionary<string, string?>
        {
            ["Indexer:Chroma:Endpoint"] = "127.0.0.1:8000"
        });

        Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<EmbedCommand>());
    }

    [Fact]
    public void AnalyzeReportsIntroductionBlocksAndWritesNothing()
    {
        var repositoryRoot = RepositoryPathResolver.FindRepositoryRoot(
            Environment.CurrentDirectory,
            AppContext.BaseDirectory);
        var workspace = Directory.CreateTempSubdirectory("startrek-analyze-");
        try
        {
            var planets = Directory.CreateDirectory(Path.Combine(workspace.FullName, "SourceFiles", "Planets"));
            var longIntroduction = string.Join(
                ' ',
                Enumerable.Repeat("Земля — родная планета человечества и столица Федерации.", 60));
            File.WriteAllText(
                Path.Combine(planets.FullName, "земля.txt"),
                $"### Земля\n\n{longIntroduction}\n\n#### История\nКоротко.\n");
            var paths = RepositoryPathResolver.Resolve(
                workspace.FullName,
                new PathOptions
                {
                    Corpus = "SourceFiles",
                    Staging = "data/staging",
                    EmbeddingModel = Path.Combine(repositoryRoot, "models", "multilingual-e5-small"),
                    Bm25Corpus = "data/bm25/corpus.json"
                });
            var options = Options.Create(new IndexerOptions
            {
                Chunking = new ChunkingOptions
                {
                    TargetTokenLimit = 480,
                    ShortSectionThreshold = 80,
                    EmbeddingMaxTokens = 512
                }
            });
            using var tokenizer = new E5Tokenizer(paths.TokenizerFile);
            var command = new AnalyzeCommand(paths, options, new DocumentParser(), tokenizer);
            using var output = new StringWriter();

            var exitCode = command.Execute(showAll: true, output);

            var report = output.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("Файлов: 1", report);
            Assert.Contains("Разделов: 1", report);
            Assert.Contains("Вводных разделов (текст до первого заголовка): 1", report);
            Assert.Contains("Разделы > 480 токенов: 1", report);
            Assert.Contains($"SourceFiles/Planets/земля.txt | {ParsedDocument.IntroductionTitle} | строки 3-3", report);
            Assert.Contains("Разделы < 80 токенов: 1", report);
            Assert.Contains("SourceFiles/Planets/земля.txt | История | строки 5-6", report);
            Assert.Equal(
                ["SourceFiles"],
                workspace.EnumerateFileSystemInfos().Select(entry => entry.Name).ToArray());
            Assert.Single(Directory.EnumerateFiles(workspace.FullName, "*", SearchOption.AllDirectories));
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }
}
