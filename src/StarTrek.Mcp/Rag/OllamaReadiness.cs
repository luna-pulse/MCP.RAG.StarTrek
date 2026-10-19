using Microsoft.Extensions.Options;

namespace StarTrek.Mcp.Rag;

/// <summary>
/// До первого вызова модели проверяет, что Ollama отвечает и модель есть в списке.
/// </summary>
public sealed class OllamaReadiness : IOllamaReadiness, IDisposable
{
    private readonly HttpClient _http;
    private readonly IOptions<RagOptions> _options;
    private readonly bool _disposeClient;

    public OllamaReadiness(IOptions<RagOptions> options)
        : this(options, new HttpClient { Timeout = TimeSpan.FromSeconds(5) }, disposeClient: true)
    {
    }

    public OllamaReadiness(IOptions<RagOptions> options, HttpMessageHandler handler)
        : this(options, new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(5) }, disposeClient: true)
    {
        ArgumentNullException.ThrowIfNull(handler);
    }

    private OllamaReadiness(IOptions<RagOptions> options, HttpClient http, bool disposeClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _http = http;
        _disposeClient = disposeClient;
    }

    public static string UnavailableMessage(string endpoint) =>
        $"Ollama не отвечает по адресу {endpoint}. Запустите её: docker compose up -d ollama.";

    public static string ModelMissingMessage(string model) =>
        $"Модель {model} не загружена в Ollama. Выполните ollama pull {model}.";

    public static string InvalidEndpointMessage(string endpoint) =>
        $"Адрес Ollama задан неверно: {endpoint}.";

    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        var settings = _options.Value.Ollama;
        var endpoint = settings.Endpoint.TrimEnd('/');
        if (!Uri.TryCreate(endpoint + "/", UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new RagDependencyException(InvalidEndpointMessage(settings.Endpoint));
        }

        var tags = new Uri(baseUri, "api/tags");
        string body;
        try
        {
            using var response = await _http.GetAsync(tags, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new RagDependencyException(
                    $"Ollama не отвечает по адресу {endpoint}: HTTP {(int)response.StatusCode}.");
            }
        }
        catch (RagDependencyException)
        {
            throw;
        }
        catch (Exception exception) when (IsTransportFailure(exception, cancellationToken))
        {
            throw new RagDependencyException(UnavailableMessage(endpoint), exception);
        }

        if (!ModelIsListed(body, settings.Model))
        {
            throw new RagDependencyException(ModelMissingMessage(settings.Model));
        }
    }

    public static bool ModelIsListed(string? body, string? model)
    {
        if (string.IsNullOrWhiteSpace(body) || string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        return body.Contains($"\"{model}\"", StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (_disposeClient)
        {
            _http.Dispose();
        }
    }

    private static bool IsTransportFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException ||
        (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested);
}
