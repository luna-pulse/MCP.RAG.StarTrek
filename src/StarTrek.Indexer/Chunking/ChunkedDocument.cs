namespace StarTrek.Indexer.Chunking;

public sealed record ChunkedDocument(
    string DocumentId,
    string SourceFile,
    string ContentHash,
    string Title,
    string? OriginalTitle,
    string EntityType,
    string Franchise,
    string Language,
    string FileType,
    IReadOnlyList<DocumentChunk> Chunks);

public sealed record DocumentChunk(
    int ChunkIndex,
    int HeadingLevel,
    IReadOnlyList<string> SectionPath,
    string SectionTitle,
    string Position,
    int StartLine,
    int EndLine,
    int TokenCount,
    string Body,
    string EmbeddingText);

public sealed record ChunkingResult(ChunkedDocument Document, int SplitSections);
