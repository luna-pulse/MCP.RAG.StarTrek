using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using StarTrek.Indexer.Configuration;
using StarTrek.Indexer.Tokenization;

namespace StarTrek.Indexer.Embeddings;

public static class EmbeddingServiceCollectionExtensions
{
    public static IServiceCollection AddLocalE5EmbeddingGenerator(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(serviceProvider =>
        {
            var paths = serviceProvider.GetRequiredService<RepositoryPaths>();
            OnnxE5EmbeddingGenerator.EnsureModelExists(paths.EmbeddingModelFile);
            var tokenizer = serviceProvider.GetRequiredService<E5Tokenizer>();
            return new OnnxE5EmbeddingGenerator(paths.EmbeddingModelFile, tokenizer);
        });
        services.AddEmbeddingGenerator<string, Embedding<float>>(
            serviceProvider =>
                serviceProvider.GetRequiredService<OnnxE5EmbeddingGenerator>());
        services.AddKernel();

        return services;
    }
}
