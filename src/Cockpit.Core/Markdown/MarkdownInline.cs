namespace Cockpit.Core.Markdown;

/// <summary>
/// One inline run within a markdown block: plain text, or emphasised/code text, or a link. A flat
/// record (kind + text [+ url]) rather than a type hierarchy — the rendered subset is small and this
/// keeps the parser and renderer to a simple switch.
/// </summary>
public sealed record MarkdownInline(MarkdownInlineKind Kind, string Text, string? Url = null)
{
    public static MarkdownInline PlainText(string text) => new(MarkdownInlineKind.Text, text);
}
