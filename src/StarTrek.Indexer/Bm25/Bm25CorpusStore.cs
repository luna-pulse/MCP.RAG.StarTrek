using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StarTrek.Indexer.Bm25;

/// <summary>
/// Корпус BM25 на диске: JSON (UTF-8, snake_case, без BOM).
/// {"format_version":1,"tokenizer_version":1,"collection":"startrek","fingerprint":"sha256-hex",
///  "chunks":[{"id":"document_id:chunk_index","tokens":["…"]}]}
/// fingerprint — SHA-256 по отсортированным парам (id, текст) чанков, из которых построен корпус.
/// </summary>
public sealed record Bm25Corpus(
    int FormatVersion,
    int TokenizerVersion,
    string Collection,
    string Fingerprint,
    IReadOnlyList<Bm25CorpusChunk> Chunks)
{
    public const int CurrentFormatVersion = 1;

    public bool IsCurrentFormat =>
        FormatVersion == CurrentFormatVersion && TokenizerVersion == Bm25Tokenizer.Version;

    public IEnumerable<Bm25Document> ToDocuments() =>
        Chunks.Select(chunk => new Bm25Document(chunk.Id, chunk.Tokens));
}

public sealed record Bm25CorpusChunk(string Id, IReadOnlyList<string> Tokens);

[JsonSerializable(typeof(Bm25Corpus))]
internal sealed partial class Bm25JsonContext : JsonSerializerContext
{
    public static Bm25JsonContext Relaxed { get; } = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
}
public sealed class Bm25CorpusStore(string path)
{
    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public bool Exists => File.Exists(Path);

    public static Bm25Corpus Create(
        string collection,
        IReadOnlyList<Bm25Chunk> chunks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentNullException.ThrowIfNull(chunks);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in chunks)
        {
            if (!ids.Add(chunk.Id))
            {
                throw new ArgumentException($"Чанк {chunk.Id} повторяется в запросе пересборки BM25.", nameof(chunks));
            }
        }

        return new Bm25Corpus(
            Bm25Corpus.CurrentFormatVersion,
            Bm25Tokenizer.Version,
            collection,
            ComputeFingerprint(chunks),
            chunks.Select(chunk => new Bm25CorpusChunk(chunk.Id, Bm25Tokenizer.Tokenize(chunk.Text))).ToList());
    }

    public static string ComputeFingerprint(IEnumerable<Bm25Chunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var chunk in chunks.OrderBy(chunk => chunk.Id, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(chunk.Id));
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(chunk.Text));
            hash.AppendData([0, 1]);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public void Save(Bm25Corpus corpus)
    {
        ArgumentNullException.ThrowIfNull(corpus);

        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, corpus, Bm25JsonContext.Relaxed.Bm25Corpus);
            }

            File.Move(temporary, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>Читает корпус. Нет файла — FileNotFoundException, битый файл — InvalidDataException.</summary>
    public Bm25Corpus Load()
    {
        if (!File.Exists(Path))
        {
            throw new FileNotFoundException($"Не найден корпус BM25: {Path}", Path);
        }

        try
        {
            using var stream = File.OpenRead(Path);
            var corpus = JsonSerializer.Deserialize(stream, Bm25JsonContext.Relaxed.Bm25Corpus);
            if (corpus is null ||
                string.IsNullOrWhiteSpace(corpus.Collection) ||
                string.IsNullOrWhiteSpace(corpus.Fingerprint) ||
                corpus.Chunks is null ||
                corpus.Chunks.Any(chunk => string.IsNullOrWhiteSpace(chunk?.Id) || chunk.Tokens is null))
            {
                throw new InvalidDataException($"Корпус BM25 повреждён: {Path}");
            }

            return corpus;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Корпус BM25 повреждён: {Path}", exception);
        }
    }
}
