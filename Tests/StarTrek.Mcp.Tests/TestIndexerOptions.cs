using System.Globalization;
using StarTrek.Indexer.Configuration;

namespace StarTrek.Mcp.Tests;

internal static class TestIndexerOptions
{
    public static IndexerOptions Create(string bm25Path) => new()
    {
        Paths = new PathOptions
        {
            Corpus = "SourceFiles",
            Staging = "Data/staging",
            EmbeddingModel = "Models/multilingual-e5-small",
            Bm25Corpus = bm25Path
        },
        Chroma = new ChromaOptions
        {
            Endpoint = "http://127.0.0.1:9",
            Collection = "startrek",
            Tenant = "default_tenant",
            Database = "default_database"
        },
        Bm25 = new Bm25Options
        {
            K1 = 1.5,
            B = 0.75
        },
        Search = new SearchOptions
        {
            RrfK = 60,
            VectorTopN = 20,
            Bm25TopN = 20,
            ResultCount = 10
        }
    };

    public static Dictionary<string, string?> AsMemoryKeys(this IndexerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new Dictionary<string, string?>
        {
            ["Indexer:Paths:Corpus"] = options.Paths.Corpus,
            ["Indexer:Paths:Staging"] = options.Paths.Staging,
            ["Indexer:Paths:EmbeddingModel"] = options.Paths.EmbeddingModel,
            ["Indexer:Paths:Bm25Corpus"] = options.Paths.Bm25Corpus,
            ["Indexer:Chroma:Endpoint"] = options.Chroma.Endpoint,
            ["Indexer:Chroma:Collection"] = options.Chroma.Collection,
            ["Indexer:Chroma:Tenant"] = options.Chroma.Tenant,
            ["Indexer:Chroma:Database"] = options.Chroma.Database,
            ["Indexer:Bm25:K1"] = options.Bm25.K1.ToString(CultureInfo.InvariantCulture),
            ["Indexer:Bm25:B"] = options.Bm25.B.ToString(CultureInfo.InvariantCulture),
            ["Indexer:Search:RrfK"] = options.Search.RrfK.ToString(CultureInfo.InvariantCulture),
            ["Indexer:Search:VectorTopN"] = options.Search.VectorTopN.ToString(CultureInfo.InvariantCulture),
            ["Indexer:Search:Bm25TopN"] = options.Search.Bm25TopN.ToString(CultureInfo.InvariantCulture),
            ["Indexer:Search:ResultCount"] = options.Search.ResultCount.ToString(CultureInfo.InvariantCulture)
        };
    }
}
