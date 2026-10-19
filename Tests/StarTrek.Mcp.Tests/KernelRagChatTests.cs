using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using StarTrek.Indexer.Configuration;
using StarTrek.Mcp.Rag;

namespace StarTrek.Mcp.Tests;

public sealed class KernelRagChatTests
{
    [Fact]
    public async Task FourPromptFunctionsSendFileTextToTheChatService()
    {
        var chat = new RecordingChatCompletion
        {
            Respond = prompt => prompt.Contains("да или нет", StringComparison.Ordinal) ? "да" : "Т'Плана-Хат"
        };
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<IChatCompletionService>(chat);
        var kernel = builder.Build();
        var options = Options.Create(new RagOptions());
        var rag = new KernelRagChat(options, new PromptCatalog(options), new NoopReadiness(), new FixedKernelFactory(kernel));

        var grade = await rag.InvokeAsync(
            PromptCatalog.Grade,
            new Dictionary<string, string>
            {
                ["question"] = "Как назывался вулканский корабль первого контакта?",
                ["chunk"] = "Корабль Т'Плана-Хат."
            },
            CancellationToken.None);

        Assert.Equal("да", grade.Trim());
        var prompt = Assert.Single(chat.Prompts);
        Assert.Contains("да или нет", prompt, StringComparison.Ordinal);
        Assert.Contains("Как назывался вулканский корабль первого контакта?", prompt, StringComparison.Ordinal);
        Assert.Contains("Корабль Т'Плана-Хат.", prompt, StringComparison.Ordinal);
        Assert.Equal(
            PromptCatalog.Names.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            kernel.Plugins.SelectMany(plugin => plugin)
                .Select(function => function.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());

        var root = RepositoryPathResolver.FindRepositoryRoot(AppContext.BaseDirectory);
        foreach (var name in PromptCatalog.Names)
        {
            var file = File.ReadAllText(Path.Combine(root, "src", "StarTrek.Mcp", "Prompts", name + ".txt"));
            Assert.Contains("{{$", file, StringComparison.Ordinal);
            Assert.DoesNotContain("KernelFunction", file, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ChatTransportFailureBecomesOllamaError()
    {
        var chat = new RecordingChatCompletion
        {
            Respond = _ => throw new HttpRequestException("Connection refused")
        };
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<IChatCompletionService>(chat);
        var options = Options.Create(new RagOptions
        {
            Ollama = new OllamaOptions { Endpoint = "http://127.0.0.1:9", Model = "qwen2.5:3b" }
        });
        var rag = new KernelRagChat(
            options,
            new PromptCatalog(options),
            new NoopReadiness(),
            new FixedKernelFactory(builder.Build()));

        var error = await Assert.ThrowsAsync<RagDependencyException>(() => rag.InvokeAsync(
            PromptCatalog.Rewrite,
            new Dictionary<string, string> { ["question"] = "Спок" },
            CancellationToken.None));
        Assert.Equal(OllamaReadiness.UnavailableMessage("http://127.0.0.1:9"), error.Message);
    }

    [Fact]
    public void OllamaKernelFactoryBuildsWithoutCallingTheServer()
    {
        var kernel = new OllamaKernelFactory(Options.Create(new RagOptions
        {
            Ollama = new OllamaOptions { Endpoint = "http://127.0.0.1:9", Model = "qwen2.5:3b" }
        })).Create();
        Assert.NotNull(kernel);
    }

    private sealed class NoopReadiness : IOllamaReadiness
    {
        public Task EnsureAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FixedKernelFactory(Kernel kernel) : IRagKernelFactory
    {
        public Kernel Create() => kernel;
    }

    private sealed class RecordingChatCompletion : IChatCompletionService
    {
        public List<string> Prompts { get; } = [];

        public required Func<string, string> Respond { get; init; }

        public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            var prompt = string.Join("\n", chatHistory.Select(message => message.Content));
            Prompts.Add(prompt);
            var text = Respond(prompt);
            return Task.FromResult<IReadOnlyList<ChatMessageContent>>(
                [new ChatMessageContent(AuthorRole.Assistant, text)]);
        }

        public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<StreamingChatMessageContent>();
    }
}
