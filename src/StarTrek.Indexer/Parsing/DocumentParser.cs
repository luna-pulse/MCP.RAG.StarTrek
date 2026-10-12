namespace StarTrek.Indexer.Parsing;

public sealed class DocumentParser
{
    public ParsedDocument ParseFile(string fullPath, string repositoryRoot)
    {
        var sourceFile = Path.GetRelativePath(repositoryRoot, fullPath).Replace('\\', '/');
        return Parse(TextNormalizer.ReadAndNormalize(fullPath), sourceFile);
    }

    public ParsedDocument Parse(string text, string sourceFile)
    {
        var normalizedText = TextNormalizer.Normalize(text);
        var lines = TextNormalizer.SplitLines(normalizedText);
        var markers = FindHeadingMarkers(lines);

        var documentMarker = markers.FirstOrDefault(marker =>
            lines.Take(marker.MarkerLineIndex).All(string.IsNullOrWhiteSpace));
        var title = documentMarker?.Title ?? TitleFromFileName(sourceFile);
        var originalTitle = FindOriginalTitle(lines);

        var contentMarkers = documentMarker is null
            ? markers
            : markers.Where(marker => marker != documentMarker).ToList();
        var sections = BuildSections(lines, contentMarkers);
        var introduction = BuildIntroduction(lines, documentMarker, contentMarkers, originalTitle);
        var headings = markers
            .Select(marker => new ParsedHeading(
                marker.Level,
                marker.Title,
                marker.MarkerLineIndex + 1,
                marker == documentMarker))
            .ToList();

        return new ParsedDocument(
            sourceFile,
            ResolveEntityType(sourceFile),
            title,
            originalTitle,
            normalizedText,
            introduction,
            headings,
            sections);
    }

    public static string ResolveEntityType(string sourceFile)
    {
        var parts = sourceFile.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

        if (parts.Contains("Races", StringComparer.OrdinalIgnoreCase))
        {
            return "race";
        }

        if (parts.Contains("Planets", StringComparer.OrdinalIgnoreCase))
        {
            return "planet";
        }

        if (parts.Contains("StarShips", StringComparer.OrdinalIgnoreCase))
        {
            return "starship";
        }

        return parts.Contains("Series", StringComparer.OrdinalIgnoreCase) ? "series" : "other";
    }

    private static string TitleFromFileName(string sourceFile)
    {
        var name = Path.GetFileNameWithoutExtension(sourceFile).Trim();
        return name.Length == 0
            ? name
            : char.ToUpperInvariant(name[0]) + name[1..];
    }

    private static List<HeadingMarker> FindHeadingMarkers(IReadOnlyList<string> lines)
    {
        var markers = new List<HeadingMarker>();

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (line.Length == 0 || line[0] != '#')
            {
                continue;
            }

            var level = 0;
            while (level < line.Length && line[level] == '#')
            {
                level++;
            }

            var inlineTitle = line[level..].Trim();
            if (inlineTitle.Length > 0)
            {
                markers.Add(new HeadingMarker(level, inlineTitle.TrimEnd(':').TrimEnd(), index, index));
                continue;
            }

            var titleLineIndex = index + 1;
            while (titleLineIndex < lines.Count && string.IsNullOrWhiteSpace(lines[titleLineIndex]))
            {
                titleLineIndex++;
            }

            var hasTitleLine = titleLineIndex < lines.Count && !lines[titleLineIndex].StartsWith('#');
            var title = hasTitleLine
                ? lines[titleLineIndex].Trim().TrimEnd(':').TrimEnd()
                : string.Empty;
            markers.Add(new HeadingMarker(level, title, index, hasTitleLine ? titleLineIndex : index));
        }

        return markers;
    }

    private static IReadOnlyList<ParsedSection> BuildSections(
        IReadOnlyList<string> lines,
        IReadOnlyList<HeadingMarker> markers)
    {
        var sections = new List<ParsedSection>(markers.Count);
        var stack = new List<(int Level, string Title)>();

        for (var index = 0; index < markers.Count; index++)
        {
            var marker = markers[index];
            while (stack.Count > 0 && stack[^1].Level >= marker.Level)
            {
                stack.RemoveAt(stack.Count - 1);
            }

            stack.Add((marker.Level, marker.Title));
            var nextMarkerLine = index + 1 < markers.Count
                ? markers[index + 1].MarkerLineIndex
                : lines.Count;
            var bodyStartIndex = Math.Max(marker.MarkerLineIndex + 1, marker.TitleLineIndex + 1);
            var bodyEndIndex = nextMarkerLine - 1;
            var bodyLines = bodyStartIndex <= bodyEndIndex
                ? Enumerable.Range(bodyStartIndex, bodyEndIndex - bodyStartIndex + 1)
                    .Select(lineIndex => (Index: lineIndex, Text: lines[lineIndex]))
                    .ToList()
                : [];
            while (bodyLines.Count > 0 && string.IsNullOrWhiteSpace(bodyLines[0].Text))
            {
                bodyLines.RemoveAt(0);
            }

            while (bodyLines.Count > 0 && string.IsNullOrWhiteSpace(bodyLines[^1].Text))
            {
                bodyLines.RemoveAt(bodyLines.Count - 1);
            }

            sections.Add(new ParsedSection(
                marker.Level,
                marker.Title,
                stack.Select(item => item.Title).ToArray(),
                marker.MarkerLineIndex + 1,
                bodyLines.Count > 0 ? bodyLines[^1].Index + 1 : marker.TitleLineIndex + 1,
                string.Join('\n', bodyLines.Select(line => line.Text))));
        }

        return sections;
    }

    private static ParsedTextBlock? BuildIntroduction(
        IReadOnlyList<string> lines,
        HeadingMarker? documentMarker,
        IReadOnlyList<HeadingMarker> contentMarkers,
        string? originalTitle)
    {
        var startIndex = documentMarker is null
            ? 0
            : Math.Max(documentMarker.MarkerLineIndex + 1, documentMarker.TitleLineIndex + 1);
        var endIndex = contentMarkers.Count > 0
            ? contentMarkers[0].MarkerLineIndex - 1
            : lines.Count - 1;
        if (startIndex > endIndex)
        {
            return null;
        }

        var selected = Enumerable.Range(startIndex, endIndex - startIndex + 1)
            .Where(index => originalTitle is null || !TryReadOriginalTitle(lines[index], out _))
            .Select(index => (Index: index, Text: lines[index]))
            .ToList();
        while (selected.Count > 0 && string.IsNullOrWhiteSpace(selected[0].Text))
        {
            selected.RemoveAt(0);
        }

        while (selected.Count > 0 && string.IsNullOrWhiteSpace(selected[^1].Text))
        {
            selected.RemoveAt(selected.Count - 1);
        }

        return selected.Count == 0
            ? null
            : new ParsedTextBlock(
                selected[0].Index + 1,
                selected[^1].Index + 1,
                string.Join('\n', selected.Select(item => item.Text)));
    }

    private static string? FindOriginalTitle(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (TryReadOriginalTitle(line, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool TryReadOriginalTitle(string line, out string value)
    {
        var plain = line.Trim().TrimStart('-').Trim().Replace("**", string.Empty, StringComparison.Ordinal);
        const string label = "Оригинальное название:";
        if (!plain.StartsWith(label, StringComparison.OrdinalIgnoreCase))
        {
            value = string.Empty;
            return false;
        }

        value = plain[label.Length..].Trim().Trim('*', '_', ' ');
        return value.Length > 0;
    }

    private sealed record HeadingMarker(
        int Level,
        string Title,
        int MarkerLineIndex,
        int TitleLineIndex);
}
