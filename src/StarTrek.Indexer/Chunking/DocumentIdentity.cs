using System.Security.Cryptography;
using System.Text;
using StarTrek.Indexer.Parsing;

namespace StarTrek.Indexer.Chunking;

public static class DocumentIdentity
{
    public static string NormalizeSourcePath(string sourceFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);

        return sourceFile
            .Replace('\\', '/')
            .Normalize(NormalizationForm.FormC)
            .ToLowerInvariant();
    }

    public static string ComputeDocumentId(string sourceFile) =>
        Sha256Hex(NormalizeSourcePath(sourceFile));

    public static string ComputeContentHash(string text) =>
        Sha256Hex(TextNormalizer.Normalize(text));

    private static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
