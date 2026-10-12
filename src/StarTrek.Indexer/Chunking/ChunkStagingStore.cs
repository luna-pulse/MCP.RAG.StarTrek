using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StarTrek.Indexer.Chunking;

public sealed partial class ChunkStagingStore
{
    public const string FileExtension = ".json";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        TypeInfoResolver = ChunkJsonContext.Default
    };

    public ChunkStagingStore(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        DirectoryPath = Path.GetFullPath(directoryPath);
    }

    public string DirectoryPath { get; }

    public string GetPath(string documentId) => Path.Combine(DirectoryPath, documentId + FileExtension);

    public IReadOnlyList<string> EnumerateFiles() =>
        Directory.Exists(DirectoryPath)
            ? Directory
                .EnumerateFiles(DirectoryPath, "*" + FileExtension)
                .Where(path => DocumentIdFileName().IsMatch(Path.GetFileName(path)))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList()
            : [];

    public ChunkedDocument Read(string path) =>
        JsonSerializer.Deserialize<ChunkedDocument>(File.ReadAllBytes(path), JsonOptions)
        ?? throw new InvalidDataException($"Пустой файл подготовки: {path}");

    public IReadOnlyList<ChunkedDocument> ReadAll() => EnumerateFiles().Select(Read).ToList();

    public string? TryReadContentHash(string documentId)
    {
        var path = GetPath(documentId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return Read(path).ContentHash;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return null;
        }
    }

    public void Write(ChunkedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        Directory.CreateDirectory(DirectoryPath);
        var path = GetPath(document.DocumentId);
        var temporaryPath = path + ".tmp";
        File.WriteAllBytes(temporaryPath, JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }

    public IReadOnlyList<string> RemoveExcept(IReadOnlySet<string> documentIds)
    {
        var removed = new List<string>();
        foreach (var path in EnumerateFiles())
        {
            var documentId = Path.GetFileNameWithoutExtension(path);
            if (!documentIds.Contains(documentId))
            {
                File.Delete(path);
                removed.Add(documentId);
            }
        }

        return removed;
    }

    [GeneratedRegex(@"^[0-9a-f]{64}\.json$")]
    private static partial Regex DocumentIdFileName();
}
