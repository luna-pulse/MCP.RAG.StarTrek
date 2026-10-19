namespace StarTrek.Indexer.Search;

/// <summary>Сосед Chroma, который функция плагина <c>vector_candidates</c> отдаёт оркестратору.</summary>
public sealed record VectorCandidate(
    string Id,
    double Distance,
    string? Document,
    IReadOnlyDictionary<string, object?> Metadata);

/// <summary>Список векторных кандидатов и id коллекции, из которой их прочитали.</summary>
public sealed record VectorCandidates(string CollectionId, IReadOnlyList<VectorCandidate> Hits);

/// <summary>Чанк BM25, который функция плагина <c>lexical_candidates</c> отдаёт оркестратору.</summary>
public sealed record LexicalCandidate(string Id, double Score);

/// <summary>Список лексических кандидатов.</summary>
public sealed record LexicalCandidates(IReadOnlyList<LexicalCandidate> Hits);
