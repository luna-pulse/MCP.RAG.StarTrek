using Microsoft.Extensions.Options;
using StarTrek.Indexer.Chunking;
using StarTrek.Indexer.Configuration;
using StarTrek.Indexer.Parsing;

namespace StarTrek.Indexer.Commands;

public sealed class ChunkCommand(
    RepositoryPaths paths,
    IOptions<IndexerOptions> options,
    DocumentParser parser,
    DocumentChunker chunker)
{
    public const string ForceOption = "--force";

    public int Execute(bool force = false, TextWriter? output = null)
    {
        output ??= Console.Out;
        if (!Directory.Exists(paths.Corpus))
        {
            throw new DirectoryNotFoundException($"Папка корпуса не найдена: {paths.Corpus}");
        }

        var store = new ChunkStagingStore(paths.Staging);
        var files = CorpusFiles.Enumerate(paths.Corpus);
        var documentIds = new HashSet<string>(StringComparer.Ordinal);
        var written = 0;
        var skipped = 0;
        var chunkCount = 0;
        var splitSections = 0;
        var maxTokens = 0;
        foreach (var file in files)
        {
            var document = parser.ParseFile(file, paths.RepositoryRoot);
            var documentId = DocumentIdentity.ComputeDocumentId(document.SourceFile);
            if (!documentIds.Add(documentId))
            {
                throw new InvalidOperationException(
                    $"Файл {document.SourceFile} даёт тот же document_id, что и другой файл корпуса.");
            }

            var contentHash = DocumentIdentity.ComputeContentHash(document.NormalizedText);
            if (!force && store.TryReadContentHash(documentId) == contentHash)
            {
                skipped++;
                continue;
            }

            var result = chunker.Chunk(document);
            store.Write(result.Document);
            written++;
            chunkCount += result.Document.Chunks.Count;
            splitSections += result.SplitSections;
            maxTokens = result.Document.Chunks.Select(chunk => chunk.TokenCount).Append(maxTokens).Max();
        }

        var removed = store.RemoveExcept(documentIds);
        output.WriteLine("Нарезка корпуса Star Trek");
        output.WriteLine($"Файлов в корпусе: {files.Count}");
        output.WriteLine($"Обработано и записано: {written}");
        output.WriteLine($"Пропущено (тот же content_hash): {skipped}");
        output.WriteLine($"Удалено файлов подготовки без исходника: {removed.Count}");
        output.WriteLine($"Чанков записано: {chunkCount}");
        output.WriteLine($"Разделов разрезано: {splitSections}");
        output.WriteLine(
            $"Максимум токенов в записанном чанке: {maxTokens} " +
            $"(лимит {options.Value.Chunking.TargetTokenLimit})");
        output.WriteLine(
            $"Папка подготовки: {Path.GetRelativePath(paths.RepositoryRoot, store.DirectoryPath).Replace('\\', '/')}");
        return 0;
    }
}
