using System.Text.RegularExpressions;

namespace Cockpit.Core.Markdown;

public enum MarkdownLineKind
{
    Paragraph,
    Blank,
    Heading1,
    Heading2,
    Heading3,
    Bullet,
    Task,
    Numbered,
    Quote,
    Fence,
    Code,
    Rule,
    TableRow,
    TableDelimiter,
    Html,
    Footnote,
    LinkDefinition,
    FrontMatter,
}

// One classified editor line; MarkerLength is the leading Markdown sign the live view hides ("## ", "- [ ] ").
public readonly record struct MarkdownLine(MarkdownLineKind Kind, int MarkerLength = 0, bool Done = false, string? Number = null)
{
    public bool IsKeptAsText => Kind is MarkdownLineKind.Html or MarkdownLineKind.Footnote or MarkdownLineKind.LinkDefinition;
}

// What a line inherits from the lines above it: an open fence, an open HTML block, or front matter.
public readonly record struct MarkdownLineState(string? Fence = null, string? HtmlEnd = null, MarkdownFrontMatter FrontMatter = MarkdownFrontMatter.None)
{
    public static MarkdownLineState AtStart(bool hasFrontMatter) => new(FrontMatter: hasFrontMatter ? MarkdownFrontMatter.Opening : MarkdownFrontMatter.None);
}

public enum MarkdownFrontMatter
{
    None,
    Opening,
    Inside,
}

public readonly record struct MarkdownInlineSpan(int Start, int Length, MarkdownInlineKind Kind, int OpenLength, int CloseLength)
{
    public int End => Start + Length;
}

// The editor's line classification (AC-1753): one line at a time, from the state the line above left behind.
public static partial class MarkdownLineClassifier
{
    public static bool OpensFrontMatter(string firstLine) => firstLine.TrimEnd() == "---";

    public static bool ClosesFrontMatter(string line) => line.TrimEnd() is "---" or "...";

    public static MarkdownLine Classify(string line, MarkdownLineState state, out MarkdownLineState next)
    {
        next = state;
        if (state.FrontMatter != MarkdownFrontMatter.None)
        {
            next = state.FrontMatter == MarkdownFrontMatter.Opening || !ClosesFrontMatter(line)
                ? state with { FrontMatter = MarkdownFrontMatter.Inside }
                : state with { FrontMatter = MarkdownFrontMatter.None };
            return new(MarkdownLineKind.FrontMatter);
        }

        if (state.Fence is { } fence)
        {
            if (!line.TrimStart().StartsWith(fence, StringComparison.Ordinal))
            {
                return new(MarkdownLineKind.Code);
            }

            next = state with { Fence = null };
            return new(MarkdownLineKind.Fence);
        }

        if (state.HtmlEnd is { } end)
        {
            if (line.Contains(end, StringComparison.OrdinalIgnoreCase))
            {
                next = state with { HtmlEnd = null };
            }

            return new(MarkdownLineKind.Html);
        }

        if (string.IsNullOrWhiteSpace(line))
        {
            return new(MarkdownLineKind.Blank);
        }

        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
        {
            next = state with { Fence = trimmed[..3] };
            return new(MarkdownLineKind.Fence);
        }

        if (HtmlStart().Match(line) is { Success: true } html)
        {
            next = state with { HtmlEnd = _HtmlEnd(line, html.Groups["tag"].Value) };
            return new(MarkdownLineKind.Html);
        }

        if (Heading().Match(line) is { Success: true } heading)
        {
            var kind = heading.Groups[1].Length switch { 1 => MarkdownLineKind.Heading1, 2 => MarkdownLineKind.Heading2, _ => MarkdownLineKind.Heading3 };
            return new(kind, heading.Length);
        }

        if (Rule().IsMatch(line))
        {
            return new(MarkdownLineKind.Rule);
        }

        if (Task().Match(line) is { Success: true } task)
        {
            return new(MarkdownLineKind.Task, task.Length, Done: task.Groups[1].Value != " ");
        }

        if (Bullet().Match(line) is { Success: true } bullet)
        {
            return new(MarkdownLineKind.Bullet, bullet.Length);
        }

        if (Numbered().Match(line) is { Success: true } numbered)
        {
            return new(MarkdownLineKind.Numbered, numbered.Length, Number: numbered.Groups[1].Value + numbered.Groups[2].Value);
        }

        if (Quote().Match(line) is { Success: true } quote)
        {
            return new(MarkdownLineKind.Quote, quote.Length);
        }

        if (trimmed.StartsWith('|'))
        {
            return new(TableDelimiter().IsMatch(line) ? MarkdownLineKind.TableDelimiter : MarkdownLineKind.TableRow);
        }

        if (Footnote().IsMatch(line))
        {
            return new(MarkdownLineKind.Footnote);
        }

        return LinkDefinition().IsMatch(line) ? new(MarkdownLineKind.LinkDefinition) : new(MarkdownLineKind.Paragraph);
    }

    // Bold, italic, inline code and links in one line's text; code spans shadow everything inside them.
    public static IReadOnlyList<MarkdownInlineSpan> Inlines(string text)
    {
        var spans = new List<MarkdownInlineSpan>();
        foreach (Match m in InlineCode().Matches(text))
        {
            spans.Add(new(m.Index, m.Length, MarkdownInlineKind.Code, 1, 1));
        }

        _Add(spans, Link().Matches(text), MarkdownInlineKind.Link, m => 1, m => m.Length - 1 - m.Groups[1].Length);
        _Add(spans, Bold().Matches(text), MarkdownInlineKind.Bold, _ => 2, _ => 2);
        _Add(spans, Italic().Matches(text), MarkdownInlineKind.Italic, _ => 1, _ => 1);
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        return spans;
    }

    private static void _Add(List<MarkdownInlineSpan> spans, MatchCollection matches, MarkdownInlineKind kind, Func<Match, int> open, Func<Match, int> close)
    {
        foreach (Match m in matches)
        {
            if (!spans.Any(s => s.Kind == MarkdownInlineKind.Code && m.Index < s.End && s.Start < m.Index + m.Length))
            {
                spans.Add(new(m.Index, m.Length, kind, open(m), close(m)));
            }
        }
    }

    private static string? _HtmlEnd(string line, string tag)
    {
        if (tag == "!--")
        {
            return line.Contains("-->", StringComparison.Ordinal) ? null : "-->";
        }

        return OpenTagOnly().IsMatch(line) && !VoidTags.Contains(tag) ? "</" + tag : null;
    }

    private static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase)
        { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr" };

    [GeneratedRegex(@"^ {0,3}<(?<tag>!--|/?[A-Za-z][A-Za-z0-9-]*(?=[\s/>]|$))")]
    private static partial Regex HtmlStart();

    [GeneratedRegex(@"^ {0,3}<[A-Za-z][A-Za-z0-9-]*(?:\s[^>]*)?>\s*$")]
    private static partial Regex OpenTagOnly();

    [GeneratedRegex(@"^(#{1,6})(?: +|$)")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^ {0,3}([-*_])(?: *\1){2,} *$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"^\s*[-*+] \[([ xX])\](?: |$)")]
    private static partial Regex Task();

    [GeneratedRegex(@"^\s*[-*+](?: +|$)")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^\s*(\d{1,9})([.)])(?: +|$)")]
    private static partial Regex Numbered();

    [GeneratedRegex(@"^ {0,3}> ?")]
    private static partial Regex Quote();

    [GeneratedRegex(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$")]
    private static partial Regex TableDelimiter();

    [GeneratedRegex(@"^ {0,3}\[\^[^\]]+\]:")]
    private static partial Regex Footnote();

    [GeneratedRegex(@"^ {0,3}\[[^\]]+\]:\s*\S")]
    private static partial Regex LinkDefinition();

    [GeneratedRegex(@"`[^`]+`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"(?<!!)\[([^\]]+)\]\([^)\s]+(?: ""[^""]*"")?\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"(\*\*|__)(?=\S)(.+?)(?<=\S)\1")]
    private static partial Regex Bold();

    [GeneratedRegex(@"(?<![*\w])\*(?=[^\s*])([^*]+?)(?<=[^\s*])\*(?!\*)|(?<![_\w])_(?=[^\s_])([^_]+?)(?<=[^\s_])_(?![_\w])")]
    private static partial Regex Italic();
}
