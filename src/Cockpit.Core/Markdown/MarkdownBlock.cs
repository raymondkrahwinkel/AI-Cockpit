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
}
