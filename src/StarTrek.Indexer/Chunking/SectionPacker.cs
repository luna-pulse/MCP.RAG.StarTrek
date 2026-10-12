using System.Text;
using StarTrek.Indexer.Configuration;

namespace StarTrek.Indexer.Chunking;

internal sealed record PackedPiece(int Number, string Body, int StartLine, int EndLine);

internal sealed class SectionPacker(
    Func<int, string, int> countPieceTokens,
    Func<string, int> countTextTokens,
    ChunkingOptions options)
{
    public IReadOnlyList<PackedPiece> Pack(IReadOnlyList<ChunkUnit> units)
    {
        var queue = new LinkedList<ChunkUnit>(units);
        var pieces = new List<PieceState>();
        var current = new PieceState(1);
        while (queue.First is { } node)
        {
            var unit = node.Value;
            queue.RemoveFirst();
            if (TryAppend(current, unit))
            {
                continue;
            }

            if (current.Carry.Count > 0 && current.Units.All(item => item.IsSticky))
            {
                ClearCarry(current);
                if (TryAppend(current, unit))
                {
                    continue;
                }
            }

            var trailingSticky = CountTrailingSticky(current);
            if (trailingSticky > 0 && trailingSticky < current.Units.Count)
            {
                var moved = current.Units.GetRange(current.Units.Count - trailingSticky, trailingSticky);
                current.Units.RemoveRange(current.Units.Count - trailingSticky, trailingSticky);
                queue.AddFirst(unit);
                PushFront(queue, moved);
                pieces.Add(current);
                current = StartNext(current, moved[0]);
                continue;
            }

            var onlySticky = current.Units.Count > 0 && trailingSticky == current.Units.Count;
            var number = current.Units.Count > 0 && !onlySticky ? current.Number + 1 : current.Number;
            if (onlySticky || !FitsAlone(unit, number) || (IsUnderfilled(current) && IsSplittableProse(unit)))
            {
                var parts = Expand(unit, number);
                if (parts.Count > 0)
                {
                    PushFront(queue, parts);
                    continue;
                }

                if (!onlySticky)
                {
                    throw new InvalidOperationException(
                        $"Фрагмент строки {unit.StartLine} не помещается в {options.TargetTokenLimit} токенов.");
                }
            }

            if (current.Units.Count > 0)
            {
                pieces.Add(current);
                current = StartNext(current, unit);
            }
            else
            {
                ClearCarry(current);
            }

            if (TryAppend(current, unit))
            {
                continue;
            }

            ClearCarry(current);
            if (!TryAppend(current, unit))
            {
                throw new InvalidOperationException(
                    $"Фрагмент строки {unit.StartLine} не помещается в {options.TargetTokenLimit} токенов.");
            }
        }

        if (current.Units.Count > 0)
        {
            pieces.Add(current);
        }

        return pieces.Select(ToPackedPiece).ToList();
    }

    private static void PushFront(LinkedList<ChunkUnit> queue, IReadOnlyList<ChunkUnit> units)
    {
        for (var index = units.Count - 1; index >= 0; index--)
        {
            queue.AddFirst(units[index]);
        }
    }

    private static int CountTrailingSticky(PieceState piece)
    {
        var count = 0;
        while (count < piece.Units.Count && piece.Units[piece.Units.Count - 1 - count].IsSticky)
        {
            count++;
        }

        return count;
    }

    private static string Compose(
        IReadOnlyList<Header> headers,
        IEnumerable<ChunkUnit> carry,
        IEnumerable<ChunkUnit> units)
    {
        var text = new StringBuilder();
        foreach (var header in headers)
        {
            text.Append(header.Text).Append(header.Joiner);
        }

        var first = true;
        foreach (var unit in carry.Concat(units))
        {
            if (!first)
            {
                text.Append(unit.Separator);
            }

            text.Append(unit.Text);
            first = false;
        }

        return text.ToString();
    }

    private static IReadOnlyList<ChunkUnit> TailFragments(ChunkUnit unit) => unit.Kind switch
    {
        ChunkUnitKind.Sentence or ChunkUnitKind.Window => [unit],
        ChunkUnitKind.Line when unit.FieldLabel is not null => [unit],
        ChunkUnitKind.Line => SectionLayout.GetSentences(unit),
        _ => unit.Children.SelectMany(TailFragments).ToList()
    };

    private static List<(int Start, int End)> FindWords(string text)
    {
        var words = new List<(int Start, int End)>();
        var index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            var start = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            if (index > start)
            {
                words.Add((start, index));
            }
        }

        return words;
    }

    private static bool IsSplittableProse(ChunkUnit unit) =>
        unit.Kind == ChunkUnitKind.Paragraph || (unit.Kind == ChunkUnitKind.Line && unit.FieldLabel is null);

    private bool IsUnderfilled(PieceState piece) =>
        piece.Units.Count > 0 &&
        countTextTokens(Compose([], [], piece.Units)) < options.ShortSectionThreshold;

    private bool Fits(int number, string body) => countPieceTokens(number, body) <= options.TargetTokenLimit;

    private bool FitsAlone(ChunkUnit unit, int number) =>
        Fits(number, Compose(HeadersFor(unit), [], [unit]));

    private bool TryAppend(PieceState piece, ChunkUnit unit)
    {
        var headers = piece.IsEmpty ? HeadersFor(unit) : piece.Headers;
        if (!Fits(piece.Number, Compose(headers, piece.Carry, [.. piece.Units, unit])))
        {
            return false;
        }

        piece.Headers = headers;
        piece.Units.Add(unit);
        return true;
    }

    private void ClearCarry(PieceState piece)
    {
        piece.Carry.Clear();
        piece.Headers = piece.Units.Count > 0 ? HeadersFor(piece.Units[0]) : [];
    }

    private PieceState StartNext(PieceState previous, ChunkUnit nextUnit)
    {
        var piece = new PieceState(previous.Number + 1);
        var tail = CollectTail(previous, nextUnit);
        for (var take = tail.Count; take > 0; take--)
        {
            var carry = tail.GetRange(tail.Count - take, take);
            if (countTextTokens(Compose([], carry, [])) > options.OverlapMaxTokens)
            {
                continue;
            }

            var headers = HeadersFor(carry[0]);
            if (Fits(piece.Number, Compose(headers, carry, [nextUnit])))
            {
                piece.Carry.AddRange(carry);
                piece.Headers = headers;
                break;
            }
        }

        return piece;
    }

    private List<ChunkUnit> CollectTail(PieceState previous, ChunkUnit nextUnit)
    {
        var tail = new List<ChunkUnit>();
        if (options.OverlapMaxSentences <= 0)
        {
            return tail;
        }

        var allowed = nextUnit.GetHeaderAncestors().ToHashSet();
        foreach (var source in previous.Carry.Concat(previous.Units).Reverse())
        {
            var fragments = TailFragments(source);
            for (var index = fragments.Count - 1; index >= 0; index--)
            {
                if (!fragments[index].GetHeaderAncestors().All(allowed.Contains))
                {
                    return tail;
                }

                tail.Insert(0, fragments[index]);
                if (tail.Count >= options.OverlapMaxSentences)
                {
                    return tail;
                }
            }
        }

        return tail;
    }

    private List<Header> HeadersFor(ChunkUnit first)
    {
        var headers = new List<Header>();
        foreach (var group in first.GetHeaderAncestors())
        {
            if (!first.Begins(group) && GetHeader(group) is { } header)
            {
                headers.Add(header);
            }
        }

        return headers;
    }

    private List<Header> AllHeaders(ChunkUnit unit) =>
        unit.GetHeaderAncestors()
            .Select(GetHeader)
            .OfType<Header>()
            .ToList();

    private Header? GetHeader(ChunkUnit group)
    {
        if (!group.IsListGroup)
        {
            return new Header(group.FieldLabel!, " ");
        }

        var intro = group.Children[0];
        return new Header(intro.Text, SectionLayout.LineSeparator);
    }

    private IReadOnlyList<ChunkUnit> Expand(ChunkUnit unit, int number) => unit.Kind switch
    {
        ChunkUnitKind.Paragraph or ChunkUnitKind.ListGroup or ChunkUnitKind.ItemGroup => unit.Children,
        ChunkUnitKind.Line => SectionLayout.GetSentences(unit),
        _ => SplitByTokens(unit, number)
    };

    private IReadOnlyList<ChunkUnit> SplitByTokens(ChunkUnit unit, int number)
    {
        var text = unit.Text;
        var words = FindWords(text);
        var prefix = Compose(AllHeaders(unit), [], []);
        var windows = new List<ChunkUnit>();
        var first = 0;
        while (first < words.Count)
        {
            var low = first + 1;
            var high = words.Count;
            var last = first;
            while (low <= high)
            {
                var middle = (low + high) / 2;
                if (Fits(number + windows.Count, prefix + text[words[first].Start..words[middle - 1].End]))
                {
                    last = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            if (last == first)
            {
                windows.AddRange(SplitByCharacters(unit, words[first], prefix, number + windows.Count, windows.Count == 0));
                first++;
                continue;
            }

            windows.Add(CreateWindow(unit, text[words[first].Start..words[last - 1].End], windows.Count == 0));
            if (last == words.Count)
            {
                break;
            }

            var overlap = 0;
            while (overlap + 1 < last - first &&
                   countTextTokens(text[words[last - overlap - 1].Start..words[last - 1].End]) <=
                   options.TokenWindowOverlapTokens)
            {
                overlap++;
            }

            first = last - overlap;
        }

        if (windows.Count == 1 && windows[0].Text == text.Trim())
        {
            return [];
        }

        unit.Adopt(windows);
        return windows;
    }

    private List<ChunkUnit> SplitByCharacters(
        ChunkUnit unit,
        (int Start, int End) word,
        string prefix,
        int number,
        bool isFirst)
    {
        var text = unit.Text;
        var windows = new List<ChunkUnit>();
        var start = word.Start;
        while (start < word.End)
        {
            var low = start + 1;
            var high = word.End;
            var end = start;
            while (low <= high)
            {
                var middle = (low + high) / 2;
                if (Fits(number + windows.Count, prefix + text[start..middle]))
                {
                    end = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            if (end > start + 1 && end < word.End && char.IsHighSurrogate(text[end - 1]))
            {
                end--;
            }

            if (end == start)
            {
                throw new InvalidOperationException(
                    $"Строка {unit.StartLine} не помещается в {options.TargetTokenLimit} токенов даже по символу.");
            }

            windows.Add(CreateWindow(unit, text[start..end], isFirst && windows.Count == 0));
            start = end;
        }

        return windows;
    }

    private static ChunkUnit CreateWindow(ChunkUnit unit, string text, bool isFirst) =>
        ChunkUnit.Leaf(ChunkUnitKind.Window, text, unit.StartLine, unit.EndLine, isFirst ? unit.Separator : " ");

    private static PackedPiece ToPackedPiece(PieceState piece)
    {
        var content = piece.Carry.Concat(piece.Units).ToList();
        return new PackedPiece(
            piece.Number,
            Compose(piece.Headers, piece.Carry, piece.Units),
            content.Min(unit => unit.StartLine),
            content.Max(unit => unit.EndLine));
    }

    private readonly record struct Header(string Text, string Joiner);

    private sealed class PieceState(int number)
    {
        public int Number { get; } = number;
        public IReadOnlyList<Header> Headers { get; set; } = [];
        public List<ChunkUnit> Carry { get; } = [];
        public List<ChunkUnit> Units { get; } = [];
        public bool IsEmpty => Carry.Count == 0 && Units.Count == 0;
    }
}
