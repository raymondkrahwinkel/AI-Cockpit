namespace Cockpit.Core.Markdown;

// One inline run within a markdown block: plain text, or emphasised/code text, or a link. A flat
// record (kind + text [+ url]) rather than a type hierarchy — the rendered subset is small and this
// keeps the parser and renderer to a simple switch.
// Nesting stays flat as well: a link inside `**…**` is one run whose `Kind` is still
// `MarkdownInlineKind.Link`, carrying the surrounding emphasis on `OuterBold` /
// `OuterItalic`. That flatness is load-bearing — the renderer locates a clickable link by
// summing `Text` lengths, so a nested run list would put those offsets out of step with the
// text actually drawn and open the wrong URL. Ask `IsBold`/`IsItalic` for
// emphasis; the two fields alone miss the run that *is* the bold or italic.
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
