using StarTrek.Indexer.Chunking;

namespace StarTrek.Indexer.Chroma;

public enum ChromaSyncAction
{
    Add,
    Skip,
    ReplaceChangedHash,
    Rewrite
}

public sealed record ChromaSyncItem(
    ChunkedDocument Document,
    ChromaSyncAction Action,
    IReadOnlyList<string> ExistingIds);

public sealed record ChromaSyncPlan(
    IReadOnlyList<ChromaSyncItem> Items,
    int UnstagedDocuments,
    IReadOnlyList<string> UnstagedRecordIds)
{
    public int UnstagedRecords => UnstagedRecordIds.Count;
}

public static class ChromaSyncPlanner
{
    public static ChromaSyncPlan Create(
        IReadOnlyList<ChunkedDocument> documents,
        IReadOnlyList<ChromaStoredRecord> existingRecords,
        bool force)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(existingRecords);

        var existingByDocument = new Dictionary<string, List<ChromaStoredRecord>>(StringComparer.Ordinal);
        foreach (var record in existingRecords)
        {
            var documentId = record.GetString(ChromaMetadataKeys.DocumentId) ??
                ChromaChunkRecords.TryGetDocumentId(record.Id);
            if (documentId is null)
            {
                continue;
            }

            if (!existingByDocument.TryGetValue(documentId, out var group))
            {
                group = [];
                existingByDocument[documentId] = group;
            }

            group.Add(record);
        }

        var items = documents
            .Select(document => Plan(
                document,
                existingByDocument.GetValueOrDefault(document.DocumentId) ?? [],
                force))
            .ToList();
        var stagedIds = documents.Select(document => document.DocumentId).ToHashSet(StringComparer.Ordinal);
        var unstaged = existingByDocument.Where(pair => !stagedIds.Contains(pair.Key)).ToList();
        return new ChromaSyncPlan(
            items,
            unstaged.Count,
            unstaged.SelectMany(pair => pair.Value.Select(record => record.Id)).ToList());
    }

    private static ChromaSyncItem Plan(
        ChunkedDocument document,
        IReadOnlyList<ChromaStoredRecord> existing,
        bool force)
    {
        var existingIds = existing.Select(record => record.Id).ToList();
        if (existing.Count == 0)
        {
            return new ChromaSyncItem(document, ChromaSyncAction.Add, existingIds);
        }

        if (existing.Any(record =>
                record.GetString(ChromaMetadataKeys.ContentHash) != document.ContentHash))
        {
            return new ChromaSyncItem(document, ChromaSyncAction.ReplaceChangedHash, existingIds);
        }

        var expectedIds = document.Chunks
            .Select(chunk => ChromaChunkRecords.CreateId(document.DocumentId, chunk.ChunkIndex));
        var isSameCard =
            existing.All(record => record.GetString(ChromaMetadataKeys.SourceFile) == document.SourceFile) &&
            existingIds.ToHashSet(StringComparer.Ordinal).SetEquals(expectedIds);
        return new ChromaSyncItem(
            document,
            force || !isSameCard ? ChromaSyncAction.Rewrite : ChromaSyncAction.Skip,
            existingIds);
    }
}
