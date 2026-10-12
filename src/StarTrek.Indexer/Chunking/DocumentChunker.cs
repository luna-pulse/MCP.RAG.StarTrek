using System.Globalization;
using System.Text.RegularExpressions;
using StarTrek.Indexer.Configuration;
using StarTrek.Indexer.Parsing;
using StarTrek.Indexer.Tokenization;

namespace StarTrek.Indexer.Chunking;

public sealed partial class DocumentChunker
{
    public const string Franchise = "Star Trek";
    public const string Language = "ru";

    private readonly E5Tokenizer tokenizer;
    private readonly ChunkingOptions options;

    public DocumentChunker(E5Tokenizer tokenizer, ChunkingOptions options)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentNullException.ThrowIfNull(options);
        if (options.TargetTokenLimit <= 0 || options.TargetTokenLimit > E5Tokenizer.ModelMaximumLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"TargetTokenLimit должен быть в диапазоне 1..{E5Tokenizer.ModelMaximumLength}.");
        }

        this.tokenizer = tokenizer;
        this.options = options;
    }

    public ChunkingResult Chunk(ParsedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var documentLines = TextNormalizer.SplitLines(document.NormalizedText);
        var chunks = new List<DocumentChunk>();
        var lastOrdinalByFamily = new Dictionary<string, int>(StringComparer.Ordinal);
        var splitSections = 0;
        foreach (var block in document.GetContentBlocks())
        {
            var lines = SectionLayout.ExtractLines(documentLines, block);
            if (lines.Count == 0)
            {
                continue;
            }

            var naming = new SectionNaming(block, lastOrdinalByFamily);
            var pieces = SplitBlock(document, block, lines, naming);
            if (pieces.Count > 1)
            {
                splitSections++;
            }

            foreach (var piece in pieces)
            {
                chunks.Add(CreateChunk(document, block, naming, piece, chunks.Count + 1));
            }

            lastOrdinalByFamily[naming.FamilyKey] = naming.Ordinal(pieces.Count);
        }

        var chunkedDocument = new ChunkedDocument(
            DocumentIdentity.ComputeDocumentId(document.SourceFile),
            document.SourceFile,
            DocumentIdentity.ComputeContentHash(document.NormalizedText),
            document.Title,
            document.OriginalTitle,
            document.EntityType,
            Franchise,
            Language,
            Path.GetExtension(document.SourceFile).TrimStart('.').ToLowerInvariant(),
            chunks);
        return new ChunkingResult(chunkedDocument, splitSections);
    }

    private static ParsedSection CreatePieceSection(
        ParsedSection block,
        SectionNaming naming,
        int pieceNumber,
        string body)
    {
        var title = naming.TitleFor(pieceNumber);
        IReadOnlyList<string> path = block.Path.Count == 0 ? [title] : [.. block.Path.SkipLast(1), title];
        return new ParsedSection(block.Level, title, path, block.StartLine, block.EndLine, body);
    }

    private IReadOnlyList<PackedPiece> SplitBlock(
        ParsedDocument document,
        ParsedSection block,
        IReadOnlyList<SourceLine> lines,
        SectionNaming naming)
    {
        var pieceTokens = new Dictionary<string, int>(StringComparer.Ordinal);
        var textTokens = new Dictionary<string, int>(StringComparer.Ordinal);

        int CountPiece(int pieceNumber, string body)
        {
            var embeddingText = EmbeddingTextBuilder.ForSection(
                document,
                CreatePieceSection(block, naming, pieceNumber, body));
            if (!pieceTokens.TryGetValue(embeddingText, out var count))
            {
                count = tokenizer.CountPassageTokens(embeddingText);
                pieceTokens[embeddingText] = count;
            }

            return count;
        }

        int CountText(string text)
        {
            if (!textTokens.TryGetValue(text, out var count))
            {
                count = tokenizer.CountTokens(text, addSpecialTokens: false);
                textTokens[text] = count;
            }

            return count;
        }

        var wholeBody = SectionLayout.Compose(lines);
        if (CountPiece(1, wholeBody) <= options.TargetTokenLimit)
        {
            return [new PackedPiece(1, wholeBody, lines[0].Number, lines[^1].Number)];
        }

        var packer = new SectionPacker(CountPiece, CountText, options);
        return packer.Pack(SectionLayout.BuildUnits(lines));
    }

    private DocumentChunk CreateChunk(
        ParsedDocument document,
        ParsedSection block,
        SectionNaming naming,
        PackedPiece piece,
        int chunkIndex)
    {
        var section = CreatePieceSection(block, naming, piece.Number, piece.Body);
        var embeddingText = EmbeddingTextBuilder.ForSection(document, section);
        var tokenCount = tokenizer.CountPassageTokens(embeddingText);
        if (tokenCount > options.TargetTokenLimit)
        {
            throw new InvalidOperationException(
                $"Чанк {chunkIndex} файла {document.SourceFile} занимает {tokenCount} токенов " +
                $"при лимите {options.TargetTokenLimit}.");
        }

        var startLine = piece.Number == 1 && block.Level > 0 ? block.StartLine : piece.StartLine;
        return new DocumentChunk(
            chunkIndex,
            block.Level,
            section.Path,
            section.Title,
            $"lines {startLine}-{piece.EndLine}",
            startLine,
            piece.EndLine,
            tokenCount,
            piece.Body,
            embeddingText);
    }

    [GeneratedRegex(@"^(?<base>.*?)\s*\(продолжение\s+(?<number>\d+)\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContinuationMarker();

    private sealed class SectionNaming
    {
        private readonly string title;
        private readonly string baseTitle;
        private readonly int? sourceNumber;
        private readonly int previousOrdinal;

        public SectionNaming(ParsedSection block, IReadOnlyDictionary<string, int> lastOrdinalByFamily)
        {
            title = block.Title;
            var marker = ContinuationMarker().Match(block.Title);
            baseTitle = marker.Success ? marker.Groups["base"].Value : block.Title;
            sourceNumber = marker.Success
                ? int.Parse(marker.Groups["number"].Value, CultureInfo.InvariantCulture)
                : null;
            FamilyKey = string.Join('\u001f', [block.Level.ToString(), .. block.Path.SkipLast(1), baseTitle]);
            previousOrdinal = lastOrdinalByFamily.GetValueOrDefault(FamilyKey);
        }

        public string FamilyKey { get; }

        public int Ordinal(int pieceNumber) =>
            sourceNumber is { } number
                ? Math.Max(previousOrdinal + pieceNumber, number + pieceNumber - 1)
                : previousOrdinal + pieceNumber;

        public string TitleFor(int pieceNumber) =>
            pieceNumber == 1 && sourceNumber is null
                ? title
                : $"{baseTitle} (продолжение {Ordinal(pieceNumber)})";
    }
}
