namespace StarTrek.Indexer.Chroma;

public sealed record ChromaCollection(string Id, string Name, int? Dimension);

public sealed record ChromaRecord(
    string Id,
    string Document,
    float[] Embedding,
    IReadOnlyDictionary<string, object> Metadata);

public sealed record ChromaStoredRecord(string Id, IReadOnlyDictionary<string, object?> Metadata)
{
    public string? GetString(string key) =>
        Metadata.TryGetValue(key, out var value) ? value as string : null;
}

/// <summary>Запись коллекции с текстом и metadata, найденная по id или запросу.</summary>
public sealed record ChromaDocumentRecord(
    string Id,
    string? Document,
    IReadOnlyDictionary<string, object?> Metadata);

/// <summary>Сосед из векторного запроса. Distance — расстояние коллекции (для cosine: 1 − сходство).</summary>
public sealed record ChromaQueryHit(
    string Id,
    double Distance,
    string? Document,
    IReadOnlyDictionary<string, object?> Metadata);