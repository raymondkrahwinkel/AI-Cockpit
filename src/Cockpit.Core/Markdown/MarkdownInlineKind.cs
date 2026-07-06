namespace Cockpit.Core.Markdown;

/// <summary>Kind of an inline run within a markdown block (paragraph, heading, list item or table cell).</summary>
public enum MarkdownInlineKind
{
    Text,
    Bold,
    Italic,
    Code,
    Link,
}
