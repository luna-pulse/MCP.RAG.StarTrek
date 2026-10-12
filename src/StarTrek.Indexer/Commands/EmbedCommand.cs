using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using StarTrek.Indexer.Bm25;
using StarTrek.Indexer.Chroma;
using StarTrek.Indexer.Chunking;
using StarTrek.Indexer.Configuration;
using StarTrek.Indexer.Embeddings;
using StarTrek.Indexer.Tokenization;

namespace StarTrek.Indexer.Commands;

public sealed class EmbedCommand(
    RepositoryPaths paths,
    IOptions<IndexerOptions> options,
    ChromaHttpClient chroma,
    Func<IEmbeddingGenerator<string, Embedding<float>>> embeddingGeneratorFactory,
    IBm25IndexRebuilder bm25IndexRebuilder)
{
    public const string ForceOption = "--force";
    public const string DistanceSpace = "cosine";
    private const int ProgressStep = 200;

    public async Task<int> ExecuteAsync(
        bool force = false,
        TextWriter? output = null,
        CancellationToken cancellationToken = default)
    {
        output ??= Console.Out;
        var stopwatch = Stopwatch.StartNew();
        var collectionName = options.Value.Chroma.Collection;

        EnsureModelFilesExist();
        await EnsureChromaAvailableAsync(collectionName, cancellationToken);
        var documents = ReadStagedDocuments();

        var collection = await chroma.GetOrCreateCollectionAsync(
            collectionName,
            DistanceSpace,
            new Dictionary<string, object>
            {
                [ChromaMetadataKeys.EmbeddingModel] = OnnxE5EmbeddingGenerator.ModelId
            },
            cancellationToken);
        if (collection.Dimension is { } dimension && dimension != OnnxE5EmbeddingGenerator.Dimensions)
        {
            throw new InvalidOperationException(
                $"Коллекция {collectionName} хранит векторы длины {dimension}, " +
                $"а {OnnxE5EmbeddingGenerator.ModelId} выдаёт {OnnxE5EmbeddingGenerator.Dimensions}. " +
                "Записи коллекции не изменены.");
        }

        var existing = await chroma.GetAllRecordsAsync(collection.Id, cancellationToken);
        var plan = ChromaSyncPlanner.Create(documents, existing, force);
        var pending = plan.Items.Where(item => item.Action != ChromaSyncAction.Skip).ToList();
        var pendingChunks = pending.Sum(item => item.Document.Chunks.Count);

        output.WriteLine("Запись эмбеддингов в ChromaDB");
        output.WriteLine($"ChromaDB: {chroma.Endpoint}, коллекция: {collectionName}");
        output.WriteLine(
            $"Файлов в подготовке: {documents.Count}, чанков: {documents.Sum(document => document.Chunks.Count)}");
        if (pendingChunks > 0)
        {
            output.WriteLine($"Нужно посчитать векторов: {pendingChunks}");
        }

        IEmbeddingGenerator<string, Embedding<float>>? generator = null;
        var writtenChunks = 0;
        var replacedForHashChange = 0;
        var rewrittenRecords = 0;
        var deletedStaleIds = 0;
        foreach (var item in pending)
        {
            generator ??= embeddingGeneratorFactory();
            var records = await CreateRecordsAsync(generator, item.Document, cancellationToken);

            if (records.Count > 0)
            {
                await chroma.UpsertAsync(collection.Id, records, cancellationToken);
            }

            var currentIds = records
                .Select(record => record.Id)
                .ToHashSet(StringComparer.Ordinal);
            var staleIds = item.ExistingIds
                .Where(id => !currentIds.Contains(id))
                .ToArray();
            if (staleIds.Length > 0)
            {
                await chroma.DeleteAsync(collection.Id, staleIds, cancellationToken);
                deletedStaleIds += staleIds.Length;
            }

            if (item.ExistingIds.Count > 0)
            {
                if (item.Action == ChromaSyncAction.ReplaceChangedHash)
                {
                    replacedForHashChange += item.ExistingIds.Count;
                }
                else
                {
                    rewrittenRecords += item.ExistingIds.Count;
                }
            }

            var previous = writtenChunks;
            writtenChunks += records.Count;
            if (writtenChunks / ProgressStep > previous / ProgressStep || writtenChunks == pendingChunks)
            {
                output.WriteLine($"  записано чанков: {writtenChunks}/{pendingChunks}");
            }
        }

        var total = await chroma.CountAsync(collection.Id, cancellationToken);
        var bm25Request = new Bm25RebuildRequest(
            collectionName,
            await CreateBm25ChunksAsync(collection.Id, documents, plan, cancellationToken),
            CollectionChanged: pending.Count > 0);
        var bm25 = await bm25IndexRebuilder.RebuildAsync(bm25Request, cancellationToken);

        var skipped = plan.Items.Where(item => item.Action == ChromaSyncAction.Skip).ToList();
        output.WriteLine(
            $"Записано файлов: {pending.Count} " +
            $"(новых: {Count(pending, ChromaSyncAction.Add)}, " +
            $"со сменой content_hash: {Count(pending, ChromaSyncAction.ReplaceChangedHash)}, " +
            $"перезаписано целиком: {Count(pending, ChromaSyncAction.Rewrite)})");
        output.WriteLine($"Записано чанков: {writtenChunks}");
        output.WriteLine($"Пропущено файлов (тот же source_file и content_hash): {skipped.Count}");
        output.WriteLine($"Пропущено чанков: {skipped.Sum(item => item.Document.Chunks.Count)}");
        output.WriteLine($"Заменено записей из-за смены content_hash: {replacedForHashChange}");
        output.WriteLine($"Перезаписано записей ({ForceOption} или неполная карточка): {rewrittenRecords}");
        output.WriteLine($"Удалено устаревших id: {deletedStaleIds}");
        output.WriteLine(
            $"Карточек в коллекции без файла подготовки (не тронуты): {plan.UnstagedDocuments}, " +
            $"записей: {plan.UnstagedRecords}");
        output.WriteLine($"Записей в коллекции: {total}");
        output.WriteLine(bm25.Rebuilt
            ? $"Индекс BM25 пересобран и сохранён: чанков {bm25.IndexedChunks}"
            : bm25.LoadedFromDisk
                ? $"Индекс BM25 загружен с диска (корпус актуален): чанков {bm25.IndexedChunks}"
                : $"Индекс BM25 не пересобран (передано чанков: {bm25Request.Chunks.Count})");
        output.WriteLine($"Время: {stopwatch.Elapsed:mm\\:ss}");
        return 0;
    }

    /// <summary>
    /// Корпус BM25 должен совпадать с коллекцией. Записи без файла подготовки embed не трогает,
    /// поэтому их текст дочитывается из ChromaDB, иначе они находились бы только вектором.
    /// </summary>
    private async Task<IReadOnlyList<Bm25Chunk>> CreateBm25ChunksAsync(
        string collectionId,
        IReadOnlyList<ChunkedDocument> documents,
        ChromaSyncPlan plan,
        CancellationToken cancellationToken)
    {
        var chunks = documents
            .SelectMany(document => document.Chunks.Select(chunk => new Bm25Chunk(
                ChromaChunkRecords.CreateId(document.DocumentId, chunk.ChunkIndex),
                ChromaChunkRecords.CreateDocumentText(chunk))))
            .ToList();
        var stagedIds = chunks.Select(chunk => chunk.Id).ToHashSet(StringComparer.Ordinal);
        var unstagedIds = plan.UnstagedRecordIds.Where(id => !stagedIds.Contains(id)).ToList();
        if (unstagedIds.Count > 0)
        {
            var unstaged = await chroma.GetByIdsAsync(collectionId, unstagedIds, cancellationToken);
            chunks.AddRange(unstaged
                .Where(record => !string.IsNullOrWhiteSpace(record.Document))
                .Select(record => new Bm25Chunk(record.Id, record.Document!)));
        }

        return chunks;
    }

    private void EnsureModelFilesExist()
    {
        OnnxE5EmbeddingGenerator.EnsureModelExists(paths.EmbeddingModelFile);
        if (!File.Exists(paths.TokenizerFile))
        {
            throw new FileNotFoundException(
                $"Не найден tokenizer.json: {paths.TokenizerFile}{Environment.NewLine}" +
                $"Скачайте его из {E5Tokenizer.DownloadUrl}",
                paths.TokenizerFile);
        }
    }

    private async Task EnsureChromaAvailableAsync(string collectionName, CancellationToken cancellationToken)
    {
        try
        {
            await chroma.EnsureAvailableAsync(cancellationToken);
        }
        catch (ChromaUnavailableException exception)
        {
            throw new ChromaUnavailableException(
                $"{exception.Message} Команда embed остановлена до записи, коллекция {collectionName} не изменена.",
                exception);
        }
    }

    private IReadOnlyList<ChunkedDocument> ReadStagedDocuments()
    {
        var store = new ChunkStagingStore(paths.Staging);
        var files = store.EnumerateFiles();
        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                $"В папке подготовки нет чанков: {store.DirectoryPath}. Сначала выполните команду chunk.");
        }

        return files
            .Select(path =>
            {
                var document = store.Read(path);
                ValidateStagedDocument(document, path);
                return document;
            })
            .ToList();
    }

    private static void ValidateStagedDocument(ChunkedDocument document, string path)
    {
        var expectedId = Path.GetFileNameWithoutExtension(path);
        if (document.DocumentId != expectedId)
        {
            throw new InvalidDataException(
                $"Файл подготовки {path} содержит document_id {document.DocumentId}. " +
                "Пересоберите подготовку командой chunk --force.");
        }

        if (string.IsNullOrWhiteSpace(document.SourceFile) ||
            string.IsNullOrWhiteSpace(document.ContentHash) ||
            document.Chunks is null)
        {
            throw new InvalidDataException(
                $"В файле подготовки {path} не заполнены source_file, content_hash или chunks.");
        }

        var chunkIndexes = new HashSet<int>();
        foreach (var chunk in document.Chunks)
        {
            if (!chunkIndexes.Add(chunk.ChunkIndex))
            {
                throw new InvalidDataException($"В файле подготовки {path} повторяется chunk_index {chunk.ChunkIndex}.");
            }

            if (string.IsNullOrWhiteSpace(chunk.EmbeddingText))
            {
                throw new InvalidDataException(
                    $"В файле подготовки {path} у чанка {chunk.ChunkIndex} пустой embedding_text.");
            }

            ChromaHttpClient.EnsureFlatMetadata(
                ChromaChunkRecords.CreateMetadata(document, chunk),
                $"чанка {chunk.ChunkIndex} в {path}");
        }
    }

    private static async Task<IReadOnlyList<ChromaRecord>> CreateRecordsAsync(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        ChunkedDocument document,
        CancellationToken cancellationToken)
    {
        if (document.Chunks.Count == 0)
        {
            return [];
        }

        var texts = document.Chunks.Select(ChromaChunkRecords.CreateDocumentText).ToArray();
        var embeddings = await generator.GenerateAsync(texts, cancellationToken: cancellationToken);
        if (embeddings.Count != texts.Length)
        {
            throw new InvalidDataException(
                $"Генератор вернул {embeddings.Count} векторов для {texts.Length} чанков файла {document.SourceFile}.");
        }

        var records = new List<ChromaRecord>(texts.Length);
        for (var index = 0; index < texts.Length; index++)
        {
            if (embeddings[index].ModelId is { } modelId &&
                !modelId.Equals(OnnxE5EmbeddingGenerator.ModelId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Вектор посчитан моделью {modelId}, ожидалась {OnnxE5EmbeddingGenerator.ModelId}.");
            }

            records.Add(ChromaChunkRecords.Create(document, document.Chunks[index], embeddings[index].Vector.ToArray()));
        }

        return records;
    }

    private static int Count(IEnumerable<ChromaSyncItem> items, ChromaSyncAction action) =>
        items.Count(item => item.Action == action);
}
