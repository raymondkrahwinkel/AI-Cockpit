namespace Cockpit.Core.Markdown;

/// <summary>
/// One top-level markdown block. A flat record carrying the fields each kind needs (only the relevant
/// ones are populated) so the parser output stays a simple list the renderer walks with a switch —
/// deliberately not a type hierarchy for such a small, closed set.
/// </summary>
public sealed record MarkdownBlock
{
    public required MarkdownBlockKind Kind { get; init; }

    /// <summary>Heading level 1–6 (<see cref="MarkdownBlockKind.Heading"/> only).</summary>
    public int HeadingLevel { get; init; }

    /// <summary>Inline runs for a paragraph or heading.</summary>
    public IReadOnlyList<MarkdownInline> Inlines { get; init; } = [];

    /// <summary>Fenced-code language label, if any (<see cref="MarkdownBlockKind.CodeBlock"/>).</summary>
    public string? Language { get; init; }

    /// <summary>Raw code text (<see cref="MarkdownBlockKind.CodeBlock"/>).</summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>True for an ordered list (<see cref="MarkdownBlockKind.List"/>).</summary>
    public bool Ordered { get; init; }

    /// <summary>List items, each a run of inlines (<see cref="MarkdownBlockKind.List"/>); or table header cells (<see cref="MarkdownBlockKind.Table"/>).</summary>
    public IReadOnlyList<IReadOnlyList<MarkdownInline>> Items { get; init; } = [];

    /// <summary>Table body rows, each a list of cells, each cell a run of inlines (<see cref="MarkdownBlockKind.Table"/>).</summary>
    public IReadOnlyList<IReadOnlyList<IReadOnlyList<MarkdownInline>>> Rows { get; init; } = [];

    /// <summary>
    /// Structural equality, spelled out because the compiler's version is not. A record compares each property
    /// with <c>EqualityComparer&lt;T&gt;.Default</c>, and for the three list properties here that is reference
    /// equality — so two separately parsed but identical blocks came out unequal, while the record's shape
    /// promises the opposite. Any caller trusting that promise got a wrong answer silently.
    /// <para>
    /// The transcript renderer is one such caller: it re-parses a streaming reply on every repaint and rebuilds
    /// only the blocks that actually changed, which is exactly this comparison.
    /// </para>
    /// </summary>
    public bool Equals(MarkdownBlock? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return other is not null
            && Kind == other.Kind
            && HeadingLevel == other.HeadingLevel
            && Ordered == other.Ordered
            && Language == other.Language
            && Code == other.Code
            && Inlines.SequenceEqual(other.Inlines)
            && _RunsEqual(Items, other.Items)
            && _RowsEqual(Rows, other.Rows);
    }

    /// <remarks>
    /// Only the scalars, the inline runs and the collection sizes go in. Equal blocks always agree on all of
    /// those, which is all a hash has to guarantee; walking every table cell as well would cost more than the
    /// collisions it saves.
    /// </remarks>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        hash.Add(HeadingLevel);
        hash.Add(Ordered);
        hash.Add(Language);
        hash.Add(Code);
        hash.Add(Items.Count);
        hash.Add(Rows.Count);

        foreach (var inline in Inlines)
        {
            hash.Add(inline);
        }

        return hash.ToHashCode();
    }

    private static bool _RunsEqual(
        IReadOnlyList<IReadOnlyList<MarkdownInline>> left,
        IReadOnlyList<IReadOnlyList<MarkdownInline>> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!left[i].SequenceEqual(right[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool _RowsEqual(
        IReadOnlyList<IReadOnlyList<IReadOnlyList<MarkdownInline>>> left,
        IReadOnlyList<IReadOnlyList<IReadOnlyList<MarkdownInline>>> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!_RunsEqual(left[i], right[i]))
            {
                return false;
            }
        }

        return true;
    }
}
