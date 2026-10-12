using System.Text;

namespace StarTrek.Indexer.Chunking;

internal enum ChunkUnitKind
{
    Paragraph,
    ListGroup,
    ItemGroup,
    Line,
    Sentence,
    Window
}

internal sealed class ChunkUnit
{
    private IReadOnlyList<ChunkUnit> children = [];

    private ChunkUnit(
        ChunkUnitKind kind,
        string text,
        int startLine,
        int endLine,
        string separator,
        string? fieldLabel)
    {
        Kind = kind;
        Text = text;
        StartLine = startLine;
        EndLine = endLine;
        Separator = separator;
        FieldLabel = fieldLabel;
    }

    public ChunkUnitKind Kind { get; }
    public string Text { get; }
    public int StartLine { get; }
    public int EndLine { get; }
    public string Separator { get; }
    public string? FieldLabel { get; }
    public ChunkUnit? Parent { get; private set; }
    public IReadOnlyList<ChunkUnit> Children => children;

    public bool IsListGroup => Kind is ChunkUnitKind.ListGroup or ChunkUnitKind.ItemGroup;

    public bool IsHeaderGroup => IsListGroup || (Kind == ChunkUnitKind.Line && FieldLabel is not null);

    public bool IsSticky => Parent is { IsListGroup: true } parent && ReferenceEquals(parent.children[0], this);

    public static ChunkUnit Leaf(
        ChunkUnitKind kind,
        string text,
        int startLine,
        int endLine,
        string separator,
        string? fieldLabel = null) =>
        new(kind, text, startLine, endLine, separator, fieldLabel);

    public static ChunkUnit Composite(ChunkUnitKind kind, IReadOnlyList<ChunkUnit> parts)
    {
        var text = new StringBuilder(parts[0].Text);
        foreach (var part in parts.Skip(1))
        {
            text.Append(part.Separator).Append(part.Text);
        }

        var unit = new ChunkUnit(
            kind,
            text.ToString(),
            parts.Min(part => part.StartLine),
            parts.Max(part => part.EndLine),
            parts[0].Separator,
            fieldLabel: null);
        unit.Adopt(parts);
        return unit;
    }

    public void Adopt(IReadOnlyList<ChunkUnit> parts)
    {
        foreach (var part in parts)
        {
            part.Parent = this;
        }

        children = parts;
    }

    public bool Begins(ChunkUnit ancestor)
    {
        var current = this;
        while (!ReferenceEquals(current, ancestor))
        {
            var parent = current.Parent;
            if (parent is null || !ReferenceEquals(parent.children[0], current))
            {
                return false;
            }

            current = parent;
        }

        return true;
    }

    public IReadOnlyList<ChunkUnit> GetHeaderAncestors()
    {
        var ancestors = new List<ChunkUnit>();
        for (var current = Parent; current is not null; current = current.Parent)
        {
            if (current.IsHeaderGroup)
            {
                ancestors.Add(current);
            }
        }

        ancestors.Reverse();
        return ancestors;
    }
}
