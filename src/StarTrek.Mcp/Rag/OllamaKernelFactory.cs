using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace StarTrek.Mcp.Rag;

public sealed class OllamaKernelFactory(IOptions<RagOptions> options) : IRagKernelFactory
{
    public Kernel Create()
    {
        var ollama = options.Value.Ollama;
        if (!Uri.TryCreate(ollama.Endpoint, UriKind.Absolute, out var endpoint))
        {
            throw new RagDependencyException(OllamaReadiness.InvalidEndpointMessage(ollama.Endpoint));
        }

#pragma warning disable SKEXP0070
        return Kernel.CreateBuilder()
            .AddOllamaChatClient(modelId: ollama.Model, endpoint: endpoint)
            .Build();
#pragma warning restore SKEXP0070
    }
}
