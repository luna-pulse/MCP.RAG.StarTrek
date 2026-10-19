using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace StarTrek.Mcp.Rag;

public static class RagServiceCollectionExtensions
{
    public static IServiceCollection AddStarTrekRag(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<RagOptions>(configuration.GetSection(RagOptions.SectionName));
        services.AddSingleton<PromptCatalog>();
        services.AddSingleton<IOllamaReadiness, OllamaReadiness>();
        services.AddSingleton<IRagKernelFactory, OllamaKernelFactory>();
        services.AddSingleton<IRagChat, KernelRagChat>();
        services.AddSingleton<ICorpusRetriever, HybridCorpusRetriever>();
        services.AddSingleton<CorrectiveRagService>();
        return services;
    }
}
