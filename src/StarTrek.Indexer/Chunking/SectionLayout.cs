using System.Text;
using System.Text.RegularExpressions;
using StarTrek.Indexer.Parsing;
using StarTrek.Indexer.Tokenization;

namespace StarTrek.Indexer.Chunking;

internal sealed record SourceLine(int Number, string Text, string Separator);

internal static partial class SectionLayout
{
    public const string LineSeparator = "\n";
    public const string ParagraphSeparator = "\n\n";
    private const int PlainFieldMaxWords = 4;

    public static IReadOnlyList<SourceLine> ExtractLines(IReadOnlyList<string> documentLines, ParsedSection block)
    {
        if (string.IsNullOrWhiteSpace(block.Body))
        {
            return [];
        }

        var bodyLines = block.Body.Split('\n');
        var lineNumbers = AlignBodyLines(documentLines, block, bodyLines);
        var result = new List<SourceLine>();
        var blankBefore = false;
        for (var index = 0; index < bodyLines.Length; index++)
        {
            var text = EmbeddingTextBuilder.RemoveHashMarks(bodyLines[index]).TrimEnd();
            if (text.Trim().Length == 0 || HorizontalRule().IsMatch(text))
            {
                blankBefore = true;
                continue;
            }

            var separator = result.Count == 0
                ? string.Empty
                : blankBefore ? ParagraphSeparator : LineSeparator;
            result.Add(new SourceLine(lineNumbers[index], text, separator));
            blankBefore = false;
        }

        return result;
    }

    public static string Compose(IReadOnlyList<SourceLine> lines)
    {
        var text = new StringBuilder();
        foreach (var line in lines)
        {
            if (text.Length > 0)
            {
                text.Append(line.Separator);
            }

            text.Append(line.Text);
        }

        return text.ToString();
    }

    public static IReadOnlyList<ChunkUnit> BuildUnits(IReadOnlyList<SourceLine> lines)
    {
        var paragraphs = new List<List<ChunkUnit>>();
        foreach (var line in lines)
        {
            var unit = ChunkUnit.Leaf(
                ChunkUnitKind.Line,
                line.Text,
                line.Number,
                line.Number,
                line.Separator,
                DetectFieldLabel(line.Text));
            var startsParagraph = paragraphs.Count == 0 ||
                                  (line.Separator == ParagraphSeparator && !ContinuesListIntro(paragraphs[^1][^1], unit));
            if (startsParagraph)
            {
                paragraphs.Add([unit]);
            }
            else
            {
                paragraphs[^1].Add(unit);
            }
        }

        return paragraphs
            .Select(paragraph =>
            {
                var blocks = BuildBlocks(paragraph, 0, paragraph.Count);
                return blocks.Count == 1 ? blocks[0] : ChunkUnit.Composite(ChunkUnitKind.Paragraph, blocks);
            })
            .ToList();
    }

    public static IReadOnlyList<ChunkUnit> GetSentences(ChunkUnit line)
    {
        if (line.Children.Count > 0)
        {
            return line.Children;
        }

        var sentences = SentenceSplitter.Split(line.Text)
            .Select((sentence, index) => ChunkUnit.Leaf(
                ChunkUnitKind.Sentence,
                sentence,
                line.StartLine,
                line.EndLine,
                index == 0 ? line.Separator : " "))
            .ToList();
        if (sentences.Count == 0)
        {
            sentences.Add(ChunkUnit.Leaf(ChunkUnitKind.Sentence, line.Text, line.StartLine, line.EndLine, line.Separator));
        }

        line.Adopt(sentences);
        return sentences;
    }

    public static bool IsListItem(string text) => ListItem().IsMatch(text);

    public static string? DetectFieldLabel(string text)
    {
        var bold = BoldField().Match(text);
        if (bold.Success)
        {
            return bold.Groups["label"].Value;
        }

        var plain = PlainField().Match(text);
        if (!plain.Success)
        {
            return null;
        }

        var label = plain.Groups["label"].Value;
        var words = label.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return words <= PlainFieldMaxWords ? label : null;
    }

    private static int[] AlignBodyLines(
        IReadOnlyList<string> documentLines,
        ParsedSection block,
        IReadOnlyList<string> bodyLines)
    {
        var numbers = new int[bodyLines.Count];
        var firstLine = block.EndLine - bodyLines.Count + 1;
        if (firstLine >= 1 && Enumerable.Range(0, bodyLines.Count)
                .All(index => documentLines[firstLine - 1 + index] == bodyLines[index]))
        {
            for (var index = 0; index < numbers.Length; index++)
            {
                numbers[index] = firstLine + index;
            }

            return numbers;
        }

        var cursor = Math.Max(block.StartLine - 1, 0);
        for (var index = 0; index < bodyLines.Count; index++)
        {
            while (cursor < documentLines.Count && documentLines[cursor] != bodyLines[index])
            {
                cursor++;
            }

            if (cursor >= documentLines.Count || cursor >= block.EndLine)
            {
                throw new InvalidOperationException(
                    $"Не удалось сопоставить строки раздела «{block.Title}» с исходным файлом.");
            }

            numbers[index] = ++cursor;
        }

        return numbers;
    }

    private static bool ContinuesListIntro(ChunkUnit previous, ChunkUnit next) =>
        IsListItem(next.Text) &&
        EndsWithColon(previous.Text) &&
        (!IsListItem(previous.Text) || Indent(next.Text) > Indent(previous.Text));

    private static List<ChunkUnit> BuildBlocks(IReadOnlyList<ChunkUnit> lines, int start, int end)
    {
        var blocks = new List<ChunkUnit>();
        var index = start;
        while (index < end)
        {
            var line = lines[index];
            var indent = Indent(line.Text);
            if (IsListItem(line.Text))
            {
                var next = index + 1;
                while (next < end && Indent(lines[next].Text) > indent)
                {
                    next++;
                }

                if (next > index + 1)
                {
                    blocks.Add(ChunkUnit.Composite(
                        ChunkUnitKind.ItemGroup,
                        [line, .. BuildBlocks(lines, index + 1, next)]));
                    index = next;
                    continue;
                }
            }
            else if (EndsWithColon(line.Text) && index + 1 < end && IsListItem(lines[index + 1].Text))
            {
                var itemIndent = Indent(lines[index + 1].Text);
                var next = index + 1;
                while (next < end && (IsListItem(lines[next].Text)
                           ? Indent(lines[next].Text) >= itemIndent
                           : Indent(lines[next].Text) > itemIndent))
                {
                    next++;
                }

                blocks.Add(ChunkUnit.Composite(
                    ChunkUnitKind.ListGroup,
                    [line, .. BuildBlocks(lines, index + 1, next)]));
                index = next;
                continue;
            }

            blocks.Add(line);
            index++;
        }

        return blocks;
    }

    private static bool EndsWithColon(string text) =>
        text.TrimEnd().TrimEnd('*', '_').TrimEnd().EndsWith(':');

    private static int Indent(string text)
    {
        var indent = 0;
        foreach (var character in text)
        {
            if (character == ' ')
            {
                indent++;
            }
            else if (character == '\t')
            {
                indent += 4;
            }
            else
            {
                break;
            }
        }

        return indent;
    }

    [GeneratedRegex(@"^\s*(?:[-*_]\s*){3,}$")]
    private static partial Regex HorizontalRule();

    [GeneratedRegex(@"^\s*(?:[-*+•]|\d{1,3}[.)])\s+\S")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^(?<label>\s*(?:(?:[-*+•]|\d{1,3}[.)])\s+)?(?:\*\*[^*\n]{1,80}?:\*\*|\*\*[^*\n]{1,80}?\*\*\s*:))\s+\S")]
    private static partial Regex BoldField();

    [GeneratedRegex(@"^(?<label>\s*(?:(?:[-*+•]|\d{1,3}[.)])\s+)?\p{L}[\p{L}\p{N} '’\-]{0,40}:)\s+\S")]
    private static partial Regex PlainField();
}
