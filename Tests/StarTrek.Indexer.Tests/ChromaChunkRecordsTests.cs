using System.Text.RegularExpressions;
using StarTrek.Indexer.Chroma;
using StarTrek.Indexer.Chunking;
using StarTrek.Indexer.Embeddings;

namespace StarTrek.Indexer.Tests;

public sealed class ChromaChunkRecordsTests
{
    private static readonly string[] ExpectedMetadataKeys =
    [
        "document_id", "content_hash", "source_file", "file_type", "title", "original_title", "entity_type",
        "franchise", "language", "chunk_index", "heading_level", "start_line", "end_line", "token_count",
        "section_title", "section_path", "position", "embedding_model"
    ];

    [Fact]
    public void IdIsDocumentIdColonChunkIndex()
    {
        var document = StagedDocuments.Race(chunkCount: 2);

        var id = ChromaChunkRecords.CreateId(document.DocumentId, document.Chunks[1].ChunkIndex);

        Assert.Equal($"{document.DocumentId}:2", id);
        Assert.Matches(new Regex("^[0-9a-f]{64}:[0-9]+$"), id);
        Assert.Equal(document.DocumentId, ChromaChunkRecords.TryGetDocumentId(id));
    }

    [Fact]
    public void MetadataHasOnlyPlanKeysWithFlatStringAndNumberValues()
    {
        var document = StagedDocuments.Race();

        var metadata = ChromaChunkRecords.CreateMetadata(document, document.Chunks[0]);

        Assert.Equal(ExpectedMetadataKeys.Order(StringComparer.Ordinal), metadata.Keys.Order(StringComparer.Ordinal));
        Assert.All(metadata.Values, value => Assert.True(value is string or int, $"{value?.GetType()} не плоский"));
        ChromaHttpClient.EnsureFlatMetadata(metadata, "теста");
        Assert.Equal(document.DocumentId, metadata["document_id"]);
        Assert.Equal(document.ContentHash, metadata["content_hash"]);
        Assert.Equal(StagedDocuments.RaceFile, metadata["source_file"]);
        Assert.Equal("txt", metadata["file_type"]);
        Assert.Equal("Талосианцы", metadata["title"]);
        Assert.Equal(string.Empty, metadata["original_title"]);
        Assert.Equal("race", metadata["entity_type"]);
        Assert.Equal("Star Trek", metadata["franchise"]);
        Assert.Equal("ru", metadata["language"]);
        Assert.Equal(1, metadata["chunk_index"]);
        Assert.Equal(4, metadata["heading_level"]);
        Assert.Equal(10, metadata["start_line"]);
        Assert.Equal(14, metadata["end_line"]);
        Assert.Equal(41, metadata["token_count"]);
        Assert.Equal("Часть 1", metadata["section_title"]);
        Assert.Equal("История > Часть 1", metadata["section_path"]);
        Assert.Equal("lines 10-14", metadata["position"]);
        Assert.Equal("intfloat/multilingual-e5-small", metadata["embedding_model"]);
    }

    [Fact]
    public void KeepsOriginalTitleWhenPresent()
    {
        var document = StagedDocuments.Planet();

        Assert.Equal("Tellar Prime", ChromaChunkRecords.CreateMetadata(document, document.Chunks[0])["original_title"]);
    }

    [Fact]
    public void DocumentTextHasNoPassagePrefixAndNoHashMarks()
    {
        var chunk = new DocumentChunk(
            1, 2, ["Корабли"], "Корабли", "lines 1-3", 1, 3, 20,
            "Тело",
            "passage: Документ: Звездолёты\nРаздел: ## Корабли\n\n### USS Enterprise (NCC-1701-D)");

        var text = ChromaChunkRecords.CreateDocumentText(chunk);

        Assert.DoesNotContain("#", text);
        Assert.False(text.StartsWith("passage:", StringComparison.Ordinal));
        Assert.StartsWith("Документ: Звездолёты", text);
        Assert.Contains("NCC-1701-D", text);
    }

    [Fact]
    public void RecordRequires384DimensionalEmbedding()
    {
        var document = StagedDocuments.Race();

        Assert.Throws<ArgumentException>(
            () => ChromaChunkRecords.Create(document, document.Chunks[0], new float[OnnxE5EmbeddingGenerator.Dimensions - 1]));
        var record = ChromaChunkRecords.Create(
            document,
            document.Chunks[0],
            new float[OnnxE5EmbeddingGenerator.Dimensions]);
        Assert.Equal($"{document.DocumentId}:1", record.Id);
        Assert.Equal(document.Chunks[0].EmbeddingText, record.Document);
    }
}
