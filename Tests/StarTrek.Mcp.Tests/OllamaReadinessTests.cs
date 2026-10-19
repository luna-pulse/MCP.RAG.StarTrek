using System.Net;
using Microsoft.Extensions.Options;
using StarTrek.Mcp.Rag;

namespace StarTrek.Mcp.Tests;

public sealed class OllamaReadinessTests
{
    [Fact]
    public async Task MissingModelIsAClearError()
    {
        var readiness = Readiness(_ => Json(HttpStatusCode.OK, """{"models":[{"name":"llama3.2"}]}"""));
        var error = await Assert.ThrowsAsync<RagDependencyException>(() => readiness.EnsureAsync(CancellationToken.None));
        Assert.Equal(OllamaReadiness.ModelMissingMessage("qwen2.5:3b"), error.Message);
    }

    [Fact]
    public async Task ListedModelPasses()
    {
        var readiness = Readiness(_ => Json(HttpStatusCode.OK, """{"models":[{"name":"qwen2.5:3b"}]}"""));
        await readiness.EnsureAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RefusedConnectionNamesTheEndpoint()
    {
        var readiness = Readiness(_ => throw new HttpRequestException("Connection refused"));
        var error = await Assert.ThrowsAsync<RagDependencyException>(() => readiness.EnsureAsync(CancellationToken.None));
        Assert.Equal(OllamaReadiness.UnavailableMessage("http://127.0.0.1:9"), error.Message);
    }

    [Fact]
    public void ModelNameMustBeQuotedExactly()
    {
        Assert.True(OllamaReadiness.ModelIsListed("""{"name":"qwen2.5:3b"}""", "qwen2.5:3b"));
        Assert.False(OllamaReadiness.ModelIsListed("""{"name":"qwen2.5:3b-instruct"}""", "qwen2.5:3b"));
        Assert.False(OllamaReadiness.ModelIsListed("""{"models":[]}""", "qwen2.5:3b"));
    }

    private static OllamaReadiness Readiness(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(Options.Create(new RagOptions
        {
            Ollama = new OllamaOptions
            {
                Endpoint = "http://127.0.0.1:9",
                Model = "qwen2.5:3b"
            }
        }), new StubHandler(respond));

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
