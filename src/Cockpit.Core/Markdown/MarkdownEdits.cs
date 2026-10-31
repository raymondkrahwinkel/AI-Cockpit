using System.Text.RegularExpressions;

namespace Cockpit.Core.Markdown;

public enum MarkdownBlockStyle
{
    Text,
    Quote,
    Heading1,
    Heading2,
    Heading3,
    Bullet,
    Numbered,
    Task,
}

public enum MarkdownWrapStyle
{
    Bold,
    Italic,
    Code,
    Link,
}

// A wrapped line plus the part the caret should select afterwards (the text, or the link's address).
public readonly record struct MarkdownWrapResult(string Line, int SelectionStart, int SelectionLength);

// Enter on a list item: the line keeps Before; After (with the next item's marker) becomes a new line, or none ends the list.
public readonly record struct MarkdownLineBreak(string Before, string? After, int CaretInAfter);

// The editor toolbar's actions as pure text transforms on one line (AC-1753).
public static partial class MarkdownEdits
{
    public static readonly string[] CodeBlockLines = ["```", "", "```"];

    public static readonly string[] TableLines = ["", "| Column | Column |", "|---|---|", "|  |  |", ""];

    public static string SetBlock(string line, MarkdownBlockStyle style)
    {
        var current = MarkdownLineClassifier.Classify(line, default, out _);
        var body = _StyleOf(current.Kind) is not null ? line[current.MarkerLength..] : line;
        if (_StyleOf(current.Kind) == style)
        {
            return body;
        }

        var prefix = style switch
        {
            MarkdownBlockStyle.Quote => "> ",
            MarkdownBlockStyle.Heading1 => "# ",
            MarkdownBlockStyle.Heading2 => "## ",
            MarkdownBlockStyle.Heading3 => "### ",
            MarkdownBlockStyle.Bullet => "- ",
            MarkdownBlockStyle.Numbered => "1. ",
            MarkdownBlockStyle.Task => "- [ ] ",
            _ => "",
        };
        return prefix + body;
    }

    public static MarkdownWrapResult Wrap(string line, int start, int end, MarkdownWrapStyle style)
    {
        var text = line[start..end];
        var placeholder = text.Length == 0;
        if (placeholder)
        {
            text = style == MarkdownWrapStyle.Link ? "link text" : "text";
        }

        var (open, close) = style switch
        {
            MarkdownWrapStyle.Bold => ("**", "**"),
            MarkdownWrapStyle.Italic => ("*", "*"),
            MarkdownWrapStyle.Code => ("`", "`"),
            _ => ("[", "](https://)"),
        };
        var wrapped = line[..start] + open + text + close + line[end..];
        return style == MarkdownWrapStyle.Link && !placeholder
            ? new(wrapped, start + open.Length + text.Length + 2, "https://".Length)
            : new(wrapped, start + open.Length, text.Length);
    }

    // Null when Enter is an ordinary line break here: not a list item, or the caret sits inside the marker.
    public static MarkdownLineBreak? ContinueList(string line, int caret)
    {
        var kind = MarkdownLineClassifier.Classify(line, default, out _);
        if (kind.Kind is not (MarkdownLineKind.Bullet or MarkdownLineKind.Task or MarkdownLineKind.Numbered or MarkdownLineKind.Quote)
            || caret < kind.MarkerLength)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(line[kind.MarkerLength..]))
        {
            return new(string.Empty, null, 0);
        }

        var marker = line[..kind.MarkerLength];
        marker = kind.Kind switch
        {
            MarkdownLineKind.Task => TaskBox().Replace(marker, "[ ]", 1),
            MarkdownLineKind.Numbered => Digits().Replace(marker, m => (long.Parse(m.Value) + 1).ToString(), 1),
            _ => marker,
        };
        return new(line[..caret], marker + line[caret..], marker.Length);
    }

    private static MarkdownBlockStyle? _StyleOf(MarkdownLineKind kind) => kind switch
    {
        MarkdownLineKind.Quote => MarkdownBlockStyle.Quote,
        MarkdownLineKind.Heading1 => MarkdownBlockStyle.Heading1,
        MarkdownLineKind.Heading2 => MarkdownBlockStyle.Heading2,
        MarkdownLineKind.Heading3 => MarkdownBlockStyle.Heading3,
        MarkdownLineKind.Bullet => MarkdownBlockStyle.Bullet,
        MarkdownLineKind.Numbered => MarkdownBlockStyle.Numbered,
        MarkdownLineKind.Task => MarkdownBlockStyle.Task,
        _ => null,
    };

    [GeneratedRegex(@"\[[ xX]\]")]
    private static partial Regex TaskBox();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}
