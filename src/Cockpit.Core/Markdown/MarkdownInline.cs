namespace Cockpit.Core.Markdown;

// Markdown inlines remain flat records rather than a type hierarchy: the rendered subset needs only a simple parser/renderer switch.
// Nested emphasis stays flags on a link/code run because nested lists would desynchronise renderer text offsets and open the wrong URL.
// Consumers must use `IsBold`/`IsItalic`; outer flags alone miss a run whose own kind supplies the emphasis.
public sealed record MarkdownInline(
    MarkdownInlineKind Kind,
    string Text,
    string? Url = null,
    bool OuterBold = false,
    bool OuterItalic = false)
{
    // Whether this run renders bold — either it is the bold run, or one sits around it.
    public bool IsBold => OuterBold || Kind == MarkdownInlineKind.Bold;

    // Whether this run renders italic — either it is the italic run, or one sits around it.
    public bool IsItalic => OuterItalic || Kind == MarkdownInlineKind.Italic;

    public static MarkdownInline PlainText(string text) => new(MarkdownInlineKind.Text, text);

    public static MarkdownInline LineBreak() => new(MarkdownInlineKind.LineBreak, string.Empty);
}
