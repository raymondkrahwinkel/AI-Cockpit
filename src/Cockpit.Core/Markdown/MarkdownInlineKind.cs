namespace Cockpit.Core.Markdown;

// Kind of an inline run within a markdown block (paragraph, heading, list item or table cell).
public enum MarkdownInlineKind
{
    Text,
    Bold,
    Italic,
    Code,
    Link,

    // A preserved single newline (opt-in — see `MarkdownParser.Parse`'s `preserveLineBreaks` parameter).
    LineBreak,
}
