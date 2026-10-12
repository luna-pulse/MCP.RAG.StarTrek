namespace StarTrek.Indexer.Parsing;

public static class CorpusFiles
{
    private static readonly string[] Extensions = [".txt", ".md"];

    public static IReadOnlyList<string> Enumerate(string corpusDirectory) =>
        Directory
            .EnumerateFiles(corpusDirectory, "*", SearchOption.AllDirectories)
            .Where(path => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
