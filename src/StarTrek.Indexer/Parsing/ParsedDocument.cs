namespace StarTrek.Indexer.Parsing;

public sealed record ParsedTextBlock(
    int StartLine,
    int EndLine,
    string Body);

public sealed record ParsedHeading(
    int Level,
    string Title,
    int LineNumber,
    bool IsDocumentTitle);

public sealed record ParsedSection(
    int Level,
    string Title,
    IReadOnlyList<string> Path,
    int StartLine,
    int EndLine,
    string Body);

public sealed record ParsedDocument(
    string SourceFile,
    string EntityType,
    string Title,
    string? OriginalTitle,
    string NormalizedText,
    ParsedTextBlock? Introduction,
    IReadOnlyList<ParsedHeading> Headings,
    IReadOnlyList<ParsedSection> Sections)
{
    public const string IntroductionTitle = "Введение";

    public IReadOnlyList<ParsedSection> GetContentBlocks()
    {
        if (Introduction is null)
        {
            return Sections;
        }

        var introduction = new ParsedSection(
            0,
            IntroductionTitle,
            [IntroductionTitle],
            Introduction.StartLine,
            Introduction.EndLine,
            Introduction.Body);
        return [introduction, .. Sections];
    }
}
