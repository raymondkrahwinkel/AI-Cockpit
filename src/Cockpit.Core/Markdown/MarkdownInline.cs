namespace Cockpit.Core.Markdown;

/// <summary>
/// One inline run within a markdown block: plain text, or emphasised/code text, or a link. A flat
/// record (kind + text [+ url]) rather than a type hierarchy — the rendered subset is small and this
/// keeps the parser and renderer to a simple switch.
/// </summary>
/// <remarks>
/// Nesting stays flat as well: a link inside <c>**…**</c> is one run whose <see cref="Kind"/> is still
/// <see cref="MarkdownInlineKind.Link"/>, carrying the surrounding emphasis on <see cref="OuterBold"/> /
/// <see cref="OuterItalic"/>. That flatness is load-bearing — the renderer locates a clickable link by
/// summing <see cref="Text"/> lengths, so a nested run list would put those offsets out of step with the
/// text actually drawn and open the wrong URL. Ask <see cref="IsBold"/>/<see cref="IsItalic"/> for
/// emphasis; the two fields alone miss the run that <em>is</em> the bold or italic.
/// </remarks>
public sealed record MarkdownInline(
    MarkdownInlineKind Kind,
    string Text,
    string? Url = null,
    bool OuterBold = false,
    bool OuterItalic = false)
{
    /// <summary>Whether this run renders bold — either it is the bold run, or one sits around it.</summary>
    public bool IsBold => OuterBold || Kind == MarkdownInlineKind.Bold;

    /// <summary>Whether this run renders italic — either it is the italic run, or one sits around it.</summary>
    public bool IsItalic => OuterItalic || Kind == MarkdownInlineKind.Italic;

    public static MarkdownInline PlainText(string text) => new(MarkdownInlineKind.Text, text);
}
