using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OllamaSharp;
using StarTrek.Indexer.Rag;

namespace StarTrek.Indexer.Configuration;

public static class LlmServiceCollectionExtensions
{
    public static IServiceCollection AddStarTrekLlm(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<LlmOptions>()
            .Bind(configuration.GetSection(LlmOptions.SectionName))
            .Validate(
                options =>
                    Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) &&
                    (endpoint.Scheme == Uri.UriSchemeHttp || endpoint.Scheme == Uri.UriSchemeHttps),
                "В Llm:Endpoint должен быть абсолютный http-адрес Ollama.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Model),
                "В Llm:Model должен быть тег модели Ollama.")
            .Validate(
                options => double.IsFinite(options.Temperature) && options.Temperature is >= 0 and <= 2,
                "В Llm:Temperature должно быть число от 0 до 2.")
            .Validate(
                options => options.TimeoutSeconds > 0,
                "В Llm:TimeoutSeconds должен быть положительным.")
            .Validate(
                options => options.MaxOutputTokens > 0 && options.MaxRewrittenCharacters > 0,
                "В Llm:MaxOutputTokens и Llm:MaxRewrittenCharacters должны быть положительными.")
            .ValidateOnStart();

        services.AddSingleton<IChatClient>(serviceProvider =>
            new OllamaChatClient(serviceProvider.GetRequiredService<IOptions<LlmOptions>>().Value));
        services.AddSingleton<IQueryRewriter, QueryRewriter>();
        services.AddSingleton<CorrectiveRagRetrieval>();
        return services;
    }

    /// <summary>
    /// <see cref="IChatClient"/> на Ollama. Соединение открывается на первом запросе, не при создании клиента.
    /// </summary>
    private sealed class OllamaChatClient : IChatClient
    {
        private readonly HttpClient http;
        private readonly IChatClient inner;

        public OllamaChatClient(LlmOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            http = new HttpClient
            {
                BaseAddress = new Uri(options.Endpoint),
                Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
            };
            inner = new OllamaApiClient(http, options.Model);
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            inner.GetResponseAsync(messages, options, cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            inner.GetStreamingResponseAsync(messages, options, cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            inner.GetService(serviceType, serviceKey);

        public void Dispose()
        {
            inner.Dispose();
            http.Dispose();
        }
    }
}
