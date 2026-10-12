using System.Text;

namespace StarTrek.Indexer.Parsing;

public static class TextNormalizer
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static string ReadAndNormalize(string path) =>
        Normalize(File.ReadAllText(path, StrictUtf8));

    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text
            .TrimStart('\uFEFF')
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Normalize(NormalizationForm.FormC);
    }

    public static string[] SplitLines(string normalizedText)
    {
        ArgumentNullException.ThrowIfNull(normalizedText);

        return normalizedText.EndsWith('\n')
            ? normalizedText[..^1].Split('\n')
            : normalizedText.Split('\n');
    }
}
