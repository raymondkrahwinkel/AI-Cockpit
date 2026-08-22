namespace Cockpit.Core.Markdown;

// Kind of a top-level markdown block.
public enum MarkdownBlockKind
{
    Paragraph,
    Heading,
    CodeBlock,
    List,
    Table,

    // A picture on a line of its own (AC-1033). Only the knowledge base renders one, because only it has a
    // resolver that can turn the reference into bytes without reaching the network; everywhere else the
    // renderer falls back to showing the reference as text, which is what it did before this kind existed.
    Image,
}
