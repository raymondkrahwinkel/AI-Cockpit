namespace Cockpit.Core.Markdown;

// One top-level markdown block. A flat record carrying the fields each kind needs (only the relevant
// ones are populated) so the parser output stays a simple list the renderer walks with a switch —
// deliberately not a type hierarchy for such a small, closed set.
public sealed record MarkdownBlock
{
    public required MarkdownBlockKind Kind { get; init; }

    // Heading level 1–6 (`MarkdownBlockKind.Heading` only).
    public int HeadingLevel { get; init; }

    // Inline runs for a paragraph or heading.
    public IReadOnlyList<MarkdownInline> Inlines { get; init; } = [];

    // Fenced-code language label, if any (`MarkdownBlockKind.CodeBlock`).
    public string? Language { get; init; }

    // Raw code text (`MarkdownBlockKind.CodeBlock`).
    public string Code { get; init; } = string.Empty;

    // True for an ordered list (`MarkdownBlockKind.List`).
    public bool Ordered { get; init; }

    // List items, each a run of inlines (`MarkdownBlockKind.List`); or table header cells (`MarkdownBlockKind.Table`).
    public IReadOnlyList<IReadOnlyList<MarkdownInline>> Items { get; init; } = [];

    // Table body rows, each a list of cells, each cell a run of inlines (`MarkdownBlockKind.Table`).
    public IReadOnlyList<IReadOnlyList<IReadOnlyList<MarkdownInline>>> Rows { get; init; } = [];

    // The `{#id}` written after a heading's text (`MarkdownBlockKind.Heading`), which is what a deep link and
    // a search hit address. Null for a heading that declares none — such a heading still reads normally, it
    // just cannot be linked to, and that is the author's choice rather than a silently generated anchor that
    // moves the next time the heading is reworded (AC-1033).
    public string? HeadingId { get; init; }

    // The reference as written (`MarkdownBlockKind.Image`), left exactly as the author typed it. Whether it
    // can be shown at all is not decided here: an `https://` reference is a request to a stranger's server at
    // the moment the page opens, and refusing it is the renderer's job, not the parser's.
    public string? ImageSource { get; init; }

    // The image's alt text (`MarkdownBlockKind.Image`) — the caption under the picture, and the whole of what
    // a reader gets when the reference could not be shown.
    public string? ImageAlt { get; init; }

    // Structural equality, spelled out because the compiler's version is not. A record compares each property
    // with `EqualityComparer&lt;T&gt;.Default`, and for the three list properties here that is reference
    // equality — so two separately parsed but identical blocks came out unequal, while the record's shape
    // promises the opposite. Any caller trusting that promise got a wrong answer silently.
    //
    // The transcript renderer is one such caller: it re-parses a streaming reply on every repaint and rebuilds
    // only the blocks that actually changed, which is exactly this comparison.
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
            && HeadingId == other.HeadingId
            && ImageSource == other.ImageSource
            && ImageAlt == other.ImageAlt
            && Inlines.SequenceEqual(other.Inlines)
            && _RunsEqual(Items, other.Items)
            && _RowsEqual(Rows, other.Rows);
    }

    // Only the scalars, the inline runs and the collection sizes go in. Equal blocks always agree on all of
    // those, which is all a hash has to guarantee; walking every table cell as well would cost more than the
    // collisions it saves.
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        hash.Add(HeadingLevel);
        hash.Add(Ordered);
        hash.Add(Language);
        hash.Add(Code);
        hash.Add(HeadingId);
        hash.Add(ImageSource);
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
