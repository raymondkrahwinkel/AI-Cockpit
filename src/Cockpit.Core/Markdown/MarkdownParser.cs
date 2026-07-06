using System.Text;
using System.Text.RegularExpressions;

namespace Cockpit.Core.Markdown;

/// <summary>
/// A small, pragmatic markdown parser for the subset Claude produces in a transcript: headings,
/// paragraphs, fenced code blocks, bullet/ordered lists, pipe tables, and inline bold/italic/code/links.
/// It is deliberately not a full CommonMark implementation — it turns the common shapes into a flat
/// block list the cockpit renders into themed controls, so the look and clickable links are fully ours.
/// </summary>
public static partial class MarkdownParser
{
    public static IReadOnlyList<MarkdownBlock> Parse(string markdown)
    {
        var lines = (markdown ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var blocks = new List<MarkdownBlock>();
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                i = _ParseFencedCode(lines, i, blocks);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                continue;
            }

            var heading = HeadingRegex().Match(line);
            if (heading.Success)
            {
                blocks.Add(new MarkdownBlock
                {
                    Kind = MarkdownBlockKind.Heading,
                    HeadingLevel = heading.Groups[1].Value.Length,
                    Inlines = ParseInlines(heading.Groups[2].Value),
                });
                i++;
                continue;
            }

            if (_IsTableHeader(lines, i))
            {
                i = _ParseTable(lines, i, blocks);
                continue;
            }

            if (ListItemRegex().IsMatch(line))
            {
                i = _ParseList(lines, i, blocks);
                continue;
            }

            i = _ParseParagraph(lines, i, blocks);
        }

        return blocks;
    }

    private static int _ParseFencedCode(string[] lines, int start, List<MarkdownBlock> blocks)
    {
        var language = lines[start].TrimStart()[3..].Trim();
        var body = new List<string>();
        var i = start + 1;
        while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
        {
            body.Add(lines[i]);
            i++;
        }

        blocks.Add(new MarkdownBlock
        {
            Kind = MarkdownBlockKind.CodeBlock,
            Language = string.IsNullOrEmpty(language) ? null : language,
            Code = string.Join('\n', body),
        });

        return i < lines.Length ? i + 1 : i; // skip the closing fence
    }

    private static bool _IsTableHeader(string[] lines, int index)
    {
        if (index + 1 >= lines.Length || !lines[index].Contains('|'))
        {
            return false;
        }

        return TableSeparatorRegex().IsMatch(lines[index + 1]);
    }

    private static int _ParseTable(string[] lines, int start, List<MarkdownBlock> blocks)
    {
        var header = _SplitTableRow(lines[start]);
        var rows = new List<IReadOnlyList<IReadOnlyList<MarkdownInline>>>();
        var i = start + 2; // skip header + separator
        while (i < lines.Length && lines[i].Contains('|') && !string.IsNullOrWhiteSpace(lines[i]))
        {
            rows.Add(_SplitTableRow(lines[i]));
            i++;
        }

        blocks.Add(new MarkdownBlock
        {
            Kind = MarkdownBlockKind.Table,
            Items = header,
            Rows = rows,
        });

        return i;
    }

    private static List<IReadOnlyList<MarkdownInline>> _SplitTableRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith('|'))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed.Split('|').Select(cell => ParseInlines(cell.Trim())).ToList();
    }

    private static int _ParseList(string[] lines, int start, List<MarkdownBlock> blocks)
    {
        var items = new List<IReadOnlyList<MarkdownInline>>();
        var ordered = OrderedItemRegex().IsMatch(lines[start]);
        var i = start;
        while (i < lines.Length)
        {
            var match = ListItemRegex().Match(lines[i]);
            if (!match.Success)
            {
                break;
            }

            items.Add(ParseInlines(match.Groups[1].Value));
            i++;
        }

        blocks.Add(new MarkdownBlock
        {
            Kind = MarkdownBlockKind.List,
            Ordered = ordered,
            Items = items,
        });

        return i;
    }

    private static int _ParseParagraph(string[] lines, int start, List<MarkdownBlock> blocks)
    {
        var text = new List<string>();
        var i = start;
        while (i < lines.Length
               && !string.IsNullOrWhiteSpace(lines[i])
               && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal)
               && !HeadingRegex().IsMatch(lines[i])
               && !ListItemRegex().IsMatch(lines[i])
               && !_IsTableHeader(lines, i))
        {
            text.Add(lines[i].Trim());
            i++;
        }

        blocks.Add(new MarkdownBlock
        {
            Kind = MarkdownBlockKind.Paragraph,
            Inlines = ParseInlines(string.Join(' ', text)),
        });

        return i;
    }

    /// <summary>Splits a run of text into inline runs: `code`, [text](url), **bold**, *italic*/_italic_.</summary>
    public static IReadOnlyList<MarkdownInline> ParseInlines(string text)
    {
        var runs = new List<MarkdownInline>();
        var buffer = new StringBuilder();
        var i = 0;

        void Flush()
        {
            if (buffer.Length > 0)
            {
                runs.Add(MarkdownInline.PlainText(buffer.ToString()));
                buffer.Clear();
            }
        }

        while (i < text.Length)
        {
            var c = text[i];

            if (c == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    Flush();
                    runs.Add(new MarkdownInline(MarkdownInlineKind.Code, text[(i + 1)..end]));
                    i = end + 1;
                    continue;
                }
            }
            else if (c == '[')
            {
                var close = text.IndexOf(']', i + 1);
                if (close > i && close + 1 < text.Length && text[close + 1] == '(')
                {
                    var urlEnd = text.IndexOf(')', close + 2);
                    if (urlEnd > close)
                    {
                        Flush();
                        runs.Add(new MarkdownInline(MarkdownInlineKind.Link, text[(i + 1)..close], text[(close + 2)..urlEnd]));
                        i = urlEnd + 1;
                        continue;
                    }
                }
            }
            else if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i)
                {
                    Flush();
                    runs.Add(new MarkdownInline(MarkdownInlineKind.Bold, text[(i + 2)..end]));
                    i = end + 2;
                    continue;
                }
            }
            else if (c is '*' or '_')
            {
                var end = text.IndexOf(c, i + 1);
                if (end > i)
                {
                    Flush();
                    runs.Add(new MarkdownInline(MarkdownInlineKind.Italic, text[(i + 1)..end]));
                    i = end + 1;
                    continue;
                }
            }

            buffer.Append(c);
            i++;
        }

        Flush();
        return runs;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\s*(?:[-*+]|\d+\.)\s+(.*)$")]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"^\s*\d+\.\s+")]
    private static partial Regex OrderedItemRegex();

    [GeneratedRegex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$")]
    private static partial Regex TableSeparatorRegex();
}
