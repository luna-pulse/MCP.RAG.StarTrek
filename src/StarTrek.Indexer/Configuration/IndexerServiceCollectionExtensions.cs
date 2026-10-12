using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StarTrek.Indexer.Bm25;
using StarTrek.Indexer.Chroma;
using StarTrek.Indexer.Chunking;
using StarTrek.Indexer.Commands;
using StarTrek.Indexer.Embeddings;
using StarTrek.Indexer.Parsing;
using StarTrek.Indexer.Search;
using StarTrek.Indexer.Tokenization;

namespace StarTrek.Indexer.Configuration;

public static class IndexerServiceCollectionExtensions
{
    public static IServiceCollection AddStarTrekIndexer(
        this IServiceCollection services,
        IConfiguration configuration,
        string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        services
            .AddOptions<IndexerOptions>()
            .Bind(configuration.GetRequiredSection(IndexerOptions.SectionName))
            .Validate(
                options =>
                    !string.IsNullOrWhiteSpace(options.Paths.Corpus) &&
                    !string.IsNullOrWhiteSpace(options.Paths.Staging) &&
                    !string.IsNullOrWhiteSpace(options.Paths.EmbeddingModel) &&
                    !string.IsNullOrWhiteSpace(options.Paths.Bm25Corpus),
                "В Indexer:Paths должны быть заданы все пути.")
            .Validate(
                options =>
                    options.Chunking.TargetTokenLimit > 0 &&
                    options.Chunking.ShortSectionThreshold > 0 &&
                    options.Chunking.EmbeddingMaxTokens >= options.Chunking.TargetTokenLimit &&
                    options.Chunking.OverlapMaxSentences >= 0 &&
                    options.Chunking.OverlapMaxTokens >= 0 &&
                    options.Chunking.OverlapMaxTokens < options.Chunking.TargetTokenLimit &&
                    options.Chunking.TokenWindowOverlapTokens >= 0 &&
                    options.Chunking.TokenWindowOverlapTokens < options.Chunking.TargetTokenLimit / 2,
                "Пороги Indexer:Chunking должны быть положительными и согласованными.")
            .Validate(
                options =>
                    Uri.TryCreate(options.Chroma.Endpoint, UriKind.Absolute, out var endpoint) &&
                    (endpoint.Scheme == Uri.UriSchemeHttp || endpoint.Scheme == Uri.UriSchemeHttps) &&
                    !string.IsNullOrWhiteSpace(options.Chroma.Collection) &&
                    !string.IsNullOrWhiteSpace(options.Chroma.Tenant) &&
                    !string.IsNullOrWhiteSpace(options.Chroma.Database),
                "В Indexer:Chroma должны быть заданы http-адрес Endpoint, Collection, Tenant и Database.")
            .Validate(
                options =>
                    double.IsFinite(options.Bm25.K1) && options.Bm25.K1 >= 0 &&
                    double.IsFinite(options.Bm25.B) && options.Bm25.B is >= 0 and <= 1,
                "В Indexer:Bm25 k1 должен быть >= 0, b — в диапазоне [0, 1].")
            .Validate(
                options =>
                    options.Search.RrfK > 0 &&
                    options.Search.VectorTopN > 0 &&
                    options.Search.Bm25TopN > 0 &&
                    options.Search.ResultCount > 0,
                "В Indexer:Search RrfK, VectorTopN, Bm25TopN и ResultCount должны быть положительными.")
            .ValidateOnStart();
        services.AddSingleton(repositoryRoot);
        services.AddSingleton(serviceProvider =>
        {
            var configuredPaths = serviceProvider.GetRequiredService<IOptions<IndexerOptions>>().Value.Paths;
            return RepositoryPathResolver.Resolve(repositoryRoot, configuredPaths);
        });
        services.AddSingleton<DocumentParser>();
        services.AddSingleton(serviceProvider =>
        {
            var paths = serviceProvider.GetRequiredService<RepositoryPaths>();
            return new E5Tokenizer(paths.TokenizerFile);
        });
        services.AddLocalE5EmbeddingGenerator();
        services.AddSingleton(serviceProvider => new DocumentChunker(
            serviceProvider.GetRequiredService<E5Tokenizer>(),
            serviceProvider.GetRequiredService<IOptions<IndexerOptions>>().Value.Chunking));
        services.AddSingleton(serviceProvider => new ChromaHttpClient(
            new HttpClient(),
            serviceProvider.GetRequiredService<IOptions<IndexerOptions>>().Value.Chroma));
        services.AddSingleton(serviceProvider =>
        {
            var configured = serviceProvider.GetRequiredService<IOptions<IndexerOptions>>().Value.Bm25;
            return new Bm25IndexHolder(
                new Bm25CorpusStore(serviceProvider.GetRequiredService<RepositoryPaths>().Bm25Corpus),
                new Bm25Parameters(configured.K1, configured.B));
        });
        services.AddSingleton<IBm25IndexRebuilder, Bm25IndexRebuilder>();
        services.AddSingleton<IQueryEmbeddingGenerator>(serviceProvider =>
            serviceProvider.GetRequiredService<OnnxE5EmbeddingGenerator>());
        services.AddSingleton<HybridSearchService>();
        services.AddSingleton<AnalyzeCommand>();
        services.AddSingleton<ChunkCommand>();
        services.AddSingleton(serviceProvider => new EmbedCommand(
            serviceProvider.GetRequiredService<RepositoryPaths>(),
            serviceProvider.GetRequiredService<IOptions<IndexerOptions>>(),
            serviceProvider.GetRequiredService<ChromaHttpClient>(),
            () => serviceProvider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
            serviceProvider.GetRequiredService<IBm25IndexRebuilder>()));
        services.AddSingleton<CommandDispatcher>();

        return services;
    }
}
