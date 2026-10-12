namespace StarTrek.Indexer.Configuration;

public sealed class IndexerOptions
{
    public const string SectionName = "Indexer";

    public PathOptions Paths { get; init; } = new();
    public ChromaOptions Chroma { get; init; } = new();
    public ChunkingOptions Chunking { get; init; } = new();
    public Bm25Options Bm25 { get; init; } = new();
    public SearchOptions Search { get; init; } = new();
}

public sealed class Bm25Options
{
    public double K1 { get; init; } = 1.5;
    public double B { get; init; } = 0.75;
}

public sealed class SearchOptions
{
    /// <summary>Константа k в RRF: 1 / (k + rank).</summary>
    public int RrfK { get; init; } = 60;

    /// <summary>Сколько соседей брать из Chroma.</summary>
    public int VectorTopN { get; init; } = 20;

    /// <summary>Сколько чанков брать из BM25.</summary>
    public int Bm25TopN { get; init; } = 20;

    /// <summary>Сколько результатов отдавать после слияния.</summary>
    public int ResultCount { get; init; } = 10;
}

public sealed class PathOptions
{
    public string Corpus { get; init; } = string.Empty;
    public string Staging { get; init; } = string.Empty;
    public string EmbeddingModel { get; init; } = string.Empty;
    public string Bm25Corpus { get; init; } = string.Empty;
}

public sealed class ChromaOptions
{
    public string Endpoint { get; init; } = string.Empty;
    public string Collection { get; init; } = string.Empty;
    public string Tenant { get; init; } = "default_tenant";
    public string Database { get; init; } = "default_database";
}

public sealed class ChunkingOptions
{
    public int TargetTokenLimit { get; init; }
    public int ShortSectionThreshold { get; init; }
    public int EmbeddingMaxTokens { get; init; }
    public int OverlapMaxSentences { get; init; } = 2;
    public int OverlapMaxTokens { get; init; } = 120;
    public int TokenWindowOverlapTokens { get; init; } = 32;
}

public sealed record RepositoryPaths(
    string RepositoryRoot,
    string Corpus,
    string Staging,
    string EmbeddingModel,
    string Bm25Corpus)
{
    public string TokenizerFile => Path.Combine(EmbeddingModel, "tokenizer.json");
    public string EmbeddingModelFile => Path.Combine(EmbeddingModel, "model.onnx");
}
