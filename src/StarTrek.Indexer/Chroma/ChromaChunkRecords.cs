using StarTrek.Indexer.Chunking;
using StarTrek.Indexer.Embeddings;
using StarTrek.Indexer.Tokenization;

namespace StarTrek.Indexer.Chroma;

public static class ChromaMetadataKeys
{
    public const string DocumentId = "document_id";
    public const string ContentHash = "content_hash";
    public const string SourceFile = "source_file";
    public const string FileType = "file_type";
    public const string Title = "title";
    public const string OriginalTitle = "original_title";
    public const string EntityType = "entity_type";
    public const string Franchise = "franchise";
    public const string Language = "language";
    public const string ChunkIndex = "chunk_index";
    public const string HeadingLevel = "heading_level";
    public const string StartLine = "start_line";
    public const string EndLine = "end_line";
    public const string TokenCount = "token_count";
    public const string SectionTitle = "section_title";
    public const string SectionPath = "section_path";
    public const string Position = "position";
    public const string EmbeddingModel = "embedding_model";
}

public static class ChromaChunkRecords
{
    public const string IdSeparator = ":";
    public const string SectionPathSeparator = " > ";

    public static string CreateId(string documentId, int chunkIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        return $"{documentId}{IdSeparator}{chunkIndex}";
    }

    public static string? TryGetDocumentId(string recordId)
    {
        var separator = recordId.LastIndexOf(IdSeparator, StringComparison.Ordinal);
        return separator > 0 ? recordId[..separator] : null;
    }

    public static string CreateDocumentText(DocumentChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        var text = chunk.EmbeddingText.StartsWith(E5Tokenizer.PassagePrefix, StringComparison.Ordinal)
            ? chunk.EmbeddingText[E5Tokenizer.PassagePrefix.Length..]
            : chunk.EmbeddingText;
        return EmbeddingTextBuilder.RemoveHashMarks(text);
    }

    public static IReadOnlyDictionary<string, object> CreateMetadata(ChunkedDocument document, DocumentChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(chunk);

        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [ChromaMetadataKeys.DocumentId] = document.DocumentId,
            [ChromaMetadataKeys.ContentHash] = document.ContentHash,
            [ChromaMetadataKeys.SourceFile] = document.SourceFile,
            [ChromaMetadataKeys.FileType] = document.FileType,
            [ChromaMetadataKeys.Title] = document.Title,
            [ChromaMetadataKeys.OriginalTitle] = document.OriginalTitle ?? string.Empty,
            [ChromaMetadataKeys.EntityType] = document.EntityType,
            [ChromaMetadataKeys.Franchise] = document.Franchise,
            [ChromaMetadataKeys.Language] = document.Language,
            [ChromaMetadataKeys.ChunkIndex] = chunk.ChunkIndex,
            [ChromaMetadataKeys.HeadingLevel] = chunk.HeadingLevel,
            [ChromaMetadataKeys.StartLine] = chunk.StartLine,
            [ChromaMetadataKeys.EndLine] = chunk.EndLine,
            [ChromaMetadataKeys.TokenCount] = chunk.TokenCount,
            [ChromaMetadataKeys.SectionTitle] = chunk.SectionTitle,
            [ChromaMetadataKeys.SectionPath] = string.Join(SectionPathSeparator, chunk.SectionPath),
            [ChromaMetadataKeys.Position] = chunk.Position,
            [ChromaMetadataKeys.EmbeddingModel] = OnnxE5EmbeddingGenerator.ModelId
        };
    }

    public static ChromaRecord Create(ChunkedDocument document, DocumentChunk chunk, float[] embedding)
    {
        ArgumentNullException.ThrowIfNull(embedding);
        if (embedding.Length != OnnxE5EmbeddingGenerator.Dimensions)
        {
            throw new ArgumentException(
                $"Эмбеддинг чанка {CreateId(document.DocumentId, chunk.ChunkIndex)} имеет длину {embedding.Length}, " +
                $"ожидалось {OnnxE5EmbeddingGenerator.Dimensions}.",
                nameof(embedding));
        }

        return new ChromaRecord(
            CreateId(document.DocumentId, chunk.ChunkIndex),
            CreateDocumentText(chunk),
            embedding,
            CreateMetadata(document, chunk));
    }
}
