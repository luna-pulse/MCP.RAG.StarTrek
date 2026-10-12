using System.Net;

namespace StarTrek.Indexer.Chroma;

public sealed class ChromaUnavailableException(string message, Exception? innerException = null) :
    InvalidOperationException(message, innerException);

public sealed class ChromaApiException(
    string operation,
    HttpStatusCode statusCode,
    string? error,
    string? details) :
    InvalidOperationException(
        $"ChromaDB отклонила запрос «{operation}»: HTTP {(int)statusCode} {error ?? statusCode.ToString()}" +
        (string.IsNullOrWhiteSpace(details) ? "." : $": {details}"))
{
    public string Operation { get; } = operation;
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? Error { get; } = error;
    public string? Details { get; } = details;
}
