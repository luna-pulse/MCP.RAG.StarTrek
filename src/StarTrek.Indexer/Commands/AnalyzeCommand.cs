using Microsoft.Extensions.Options;
using StarTrek.Indexer.Configuration;
using StarTrek.Indexer.Parsing;
using StarTrek.Indexer.Tokenization;

namespace StarTrek.Indexer.Commands;

public sealed class AnalyzeCommand(
    RepositoryPaths paths,
    IOptions<IndexerOptions> options,
    DocumentParser parser,
    E5Tokenizer tokenizer)
{
    public const string ShowAllOption = "--all";
    private const int DetailLimit = 20;

    public int Execute(bool showAll = false, TextWriter? output = null)
    {
        output ??= Console.Out;
        if (!Directory.Exists(paths.Corpus))
        {
            throw new DirectoryNotFoundException($"Папка корпуса не найдена: {paths.Corpus}");
        }

        var files = CorpusFiles.Enumerate(paths.Corpus);
        var documents = files.Select(path => parser.ParseFile(path, paths.RepositoryRoot)).ToList();
        var blockStats = documents
            .SelectMany(document => document.GetContentBlocks().Select(block =>
                new SectionStat(
                    document.SourceFile,
                    block,
                    tokenizer.CountPassageTokens(EmbeddingTextBuilder.ForSection(document, block)))))
            .ToList();
        var chunking = options.Value.Chunking;
        var detailLimit = showAll ? int.MaxValue : DetailLimit;

        output.WriteLine("Анализ корпуса Star Trek");
        output.WriteLine($"Файлов: {documents.Count}");
        foreach (var group in documents
                     .GroupBy(document => (
                         Folder: GetTopLevelFolder(document.SourceFile),
                         document.EntityType))
                     .OrderBy(group => group.Key.Folder, StringComparer.OrdinalIgnoreCase))
        {
            output.WriteLine($"  {group.Key.Folder} ({group.Key.EntityType}): {group.Count()}");
        }

        output.WriteLine("Уровни заголовков:");
        foreach (var group in documents
                     .SelectMany(document => document.Headings)
                     .GroupBy(heading => heading.Level)
                     .OrderBy(group => group.Key))
        {
            output.WriteLine($"  {new string('#', group.Key)}: {group.Count()}");
        }

        var emptySections = blockStats.Where(stat => string.IsNullOrWhiteSpace(stat.Section.Body)).ToList();
        var longSections = blockStats
            .Where(stat => stat.TokenCount > chunking.TargetTokenLimit)
            .OrderByDescending(stat => stat.TokenCount)
            .ToList();
        var shortSections = blockStats
            .Where(stat => stat.TokenCount < chunking.ShortSectionThreshold)
            .OrderBy(stat => stat.TokenCount)
            .ThenBy(stat => stat.SourceFile, StringComparer.OrdinalIgnoreCase)
            .ToList();

        output.WriteLine($"Разделов: {documents.Sum(document => document.Sections.Count)}");
        output.WriteLine(
            "Вводных разделов (текст до первого заголовка): " +
            $"{documents.Count(document => document.Introduction is not null)}");
        PrintDetails(output, "Пустые блоки", emptySections, detailLimit);
        PrintDetails(output, $"Разделы > {chunking.TargetTokenLimit} токенов", longSections, detailLimit);
        PrintDetails(output, $"Разделы < {chunking.ShortSectionThreshold} токенов", shortSections, detailLimit);
        output.WriteLine(
            "Токены посчитаны для полного будущего embedding input " +
            "(passage-префикс, метаданные, тело и <s>/</s>), вводные разделы включены.");
        if (!showAll)
        {
            output.WriteLine($"Полные списки: analyze {ShowAllOption}");
        }

        return 0;
    }

    private static void PrintDetails(
        TextWriter output,
        string label,
        IReadOnlyList<SectionStat> sections,
        int detailLimit)
    {
        output.WriteLine($"{label}: {sections.Count}");
        foreach (var stat in sections.Take(detailLimit))
        {
            var title = string.IsNullOrWhiteSpace(stat.Section.Title)
                ? "<без названия>"
                : stat.Section.Title;
            output.WriteLine(
                $"  {stat.SourceFile} | {string.Join(" > ", stat.Section.Path)} " +
                $"| строки {stat.Section.StartLine}-{stat.Section.EndLine} " +
                $"| {stat.TokenCount} токенов | {title}");
        }

        if (sections.Count > detailLimit)
        {
            output.WriteLine($"  ... ещё {sections.Count - detailLimit}");
        }
    }

    private static string GetTopLevelFolder(string sourceFile)
    {
        var parts = sourceFile.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0].Equals("SourceFiles", StringComparison.OrdinalIgnoreCase)
            ? parts[1]
            : parts.FirstOrDefault() ?? "other";
    }

    private sealed record SectionStat(
        string SourceFile,
        ParsedSection Section,
        int TokenCount);
}
