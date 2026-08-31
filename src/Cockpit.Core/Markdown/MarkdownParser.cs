using System.Text;
using System.Text.RegularExpressions;

namespace Cockpit.Core.Markdown;

// Pragmatic parser for transcript markdown: common blocks plus bold/italic/code/links, rendered as Cockpit's flat themed controls.
// It deliberately is not full CommonMark: the supported subset gives Cockpit ownership of appearance and links.
public static partial class MarkdownParser
{
    // AC-936: opt-in — off keeps CommonMark's default (a single newline joins its paragraph's lines with a
    // space); only the chat bubble turns it on, so a Shift+Enter there stays a visible line break instead.
    public static IReadOnlyList<MarkdownBlock> Parse(string markdown, bool preserveLineBreaks = false, bool startsInsideFence = false)
    {
        var lines = (markdown ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var blocks = new List<MarkdownBlock>();
        var i = 0;

        // AC-1265: the continuation of a fence an earlier row opened — without this the second half has no
        // opener, falls back to prose and folds its line breaks into one line. No language: the label belongs
        // to the row that opened the fence, and drawing it again on every fragment reads as several blocks.
        if (startsInsideFence)
        {
            i = _ParseFencedCodeBody(lines, 0, language: null, blocks);
        }

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
                var text = heading.Groups[2].Value;
                var anchor = HeadingAnchorRegex().Match(text);
                blocks.Add(new MarkdownBlock
                {
                    Kind = MarkdownBlockKind.Heading,
                    HeadingLevel = heading.Groups[1].Value.Length,
                    HeadingId = anchor.Success ? anchor.Groups[1].Value : null,
                    Inlines = ParseInlines(anchor.Success ? text[..anchor.Index].TrimEnd() : text),
                });
                i++;
                continue;
            }

            var image = ImageRegex().Match(line);
            if (image.Success)
            {
                blocks.Add(new MarkdownBlock
                {
                    Kind = MarkdownBlockKind.Image,
                    ImageAlt = image.Groups[1].Value.Trim(),
                    ImageSource = image.Groups[2].Value.Trim(),
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

            i = _ParseParagraph(lines, i, blocks, preserveLineBreaks);
        }

        return blocks;
    }

    private static int _ParseFencedCode(string[] lines, int start, List<MarkdownBlock> blocks) =>
        _ParseFencedCodeBody(lines, start + 1, lines[start].TrimStart()[3..].Trim(), blocks);

    // The body of a fence, from the line after its opener — or from line 0 when an earlier row opened it.
    private static int _ParseFencedCodeBody(string[] lines, int start, string? language, List<MarkdownBlock> blocks)
    {
        var body = new List<string>();
        var i = start;
        while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
        {
            body.Add(lines[i]);
            i++;
        }

        // AC-1265: an unclosed fence ends on its last code line's newline, and splitting on newlines turns
        // that into an empty final entry — a blank line drawn at the seam between two fragments. A closed
        // fence keeps its trailing blank line: there the line before the closer is deliberate.
        if (i >= lines.Length && body.Count > 0 && body[^1].Length == 0)
        {
            body.RemoveAt(body.Count - 1);
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
        var orderedStart = ordered && int.TryParse(OrderedNumberRegex().Match(lines[start]).Groups[1].ValueSpan, out var first)
            ? Math.Max(1, first)
            : 1;
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
            OrderedStart = orderedStart,
            Items = items,
        });

        return i;
    }

    private static int _ParseParagraph(string[] lines, int start, List<MarkdownBlock> blocks, bool preserveLineBreaks)
    {
        var text = new List<string>();
        var i = start;
        while (i < lines.Length
               && !string.IsNullOrWhiteSpace(lines[i])
               && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal)
               && !HeadingRegex().IsMatch(lines[i])
               && !ImageRegex().IsMatch(lines[i])
               && !ListItemRegex().IsMatch(lines[i])
               && !_IsTableHeader(lines, i))
        {
            text.Add(lines[i].Trim());
            i++;
        }

        // Joined with '\n' rather than ' ' when preserving breaks: `ParseInlines` turns that character into a
        // `LineBreak` run instead of collapsing it, so each Shift+Enter'd line stays its own line on screen.
        var joined = string.Join(preserveLineBreaks ? '\n' : ' ', text);
        blocks.Add(new MarkdownBlock
        {
            Kind = MarkdownBlockKind.Paragraph,
            Inlines = ParseInlines(joined, preserveLineBreaks),
        });

        return i;
    }

    // Splits a run of text into inline runs: `code`, [text](url), **bold**, *italic*/_italic_, and bare
    // http(s) URLs. Emphasis nests — the runs inside it keep their own kind and carry the surrounding
    // bold/italic as a flag, so the list stays flat (see `MarkdownInline` on why that matters).
    public static IReadOnlyList<MarkdownInline> ParseInlines(string text, bool preserveLineBreaks = false)
        => _ParseInlines(text ?? string.Empty, autolink: true, preserveLineBreaks);

    // autolink is off while parsing the label of a [text](url) link: its text is already inside a link, and
    // picking a URL out of it a second time would lay one clickable range over another.
    private static List<MarkdownInline> _ParseInlines(string text, bool autolink, bool preserveLineBreaks = false)
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

            if (preserveLineBreaks && c == '\n')
            {
                Flush();
                runs.Add(MarkdownInline.LineBreak());
                i++;
                continue;
            }

            // Ahead of the markers below only because a scheme can start nowhere they can. A URL inside a code
            // span or a link is never reached: those branches consume their whole span in one step.
            if (autolink && _BareUrlAt(text, i) is { } bare)
            {
                Flush();
                runs.Add(new MarkdownInline(MarkdownInlineKind.Link, bare, bare));
                i += bare.Length;
                continue;
            }

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
                        var url = text[(close + 2)..urlEnd];
                        foreach (var inner in _ParseInlines(text[(i + 1)..close], autolink: false, preserveLineBreaks))
                        {
                            runs.Add(inner with
                            {
                                Kind = MarkdownInlineKind.Link,
                                Url = url,
                                OuterBold = inner.IsBold,
                                OuterItalic = inner.IsItalic,
                            });
                        }

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
                    runs.AddRange(_Emphasise(_ParseInlines(text[(i + 2)..end], autolink, preserveLineBreaks), MarkdownInlineKind.Bold));
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
                    runs.AddRange(_Emphasise(_ParseInlines(text[(i + 1)..end], autolink, preserveLineBreaks), MarkdownInlineKind.Italic));
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

    // Enclosing emphasis changes plain-text kind, or becomes a flag on links/code/other emphasis.
    // One run in stays one out, preserving concatenated text and therefore renderer link offsets.
    private static IEnumerable<MarkdownInline> _Emphasise(List<MarkdownInline> runs, MarkdownInlineKind emphasis)
        => runs.Select(run => run.Kind == MarkdownInlineKind.Text
            ? run with { Kind = emphasis }
            : run with
            {
                OuterBold = run.IsBold || emphasis == MarkdownInlineKind.Bold,
                OuterItalic = run.IsItalic || emphasis == MarkdownInlineKind.Italic,
            });

    // The bare http(s) URL starting at `start`, or null if none does. It runs to the next
    // whitespace minus the punctuation a sentence leaves behind — one character too many still renders fine
    // and then 404s, which is why the trailing trim exists at all.
    private static string? _BareUrlAt(string text, int start)
    {
        var rest = text.AsSpan(start);
        var scheme = rest.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? 8
            : rest.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? 7
            : 0;
        if (scheme == 0)
        {
            return null;
        }

        var end = start;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not ('<' or '>'))
        {
            end++;
        }

        while (end - start > scheme && _EndsOnSentencePunctuation(text, start, end))
        {
            end--;
        }

        // A scheme on its own is not a link, and "https://." would survive the trim above as one.
        return end - start > scheme ? text[start..end] : null;
    }

    // A closing bracket is only the sentence's while nothing inside the URL opened it — that keeps
    // "(https://x/a)" off its closer and a wiki link like "https://x/Foo_(bar)" whole.
    private static bool _EndsOnSentencePunctuation(string text, int start, int end)
    {
        var last = text[end - 1];
        if (last is '.' or ',' or ';' or ':' or '!' or '?' or '\'' or '"')
        {
            return true;
        }

        var opener = last switch { ')' => '(', ']' => '[', '}' => '{', _ => '\0' };
        if (opener == '\0')
        {
            return false;
        }

        var url = text.AsSpan(start, end - start);
        return url.Count(last) > url.Count(opener);
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex HeadingRegex();

    // The explicit anchor a knowledge-base heading ends with: `## Bot token {#bot-token}`. Only ever applied
    // to a line that already parsed as a heading, so ordinary prose mentioning braces is untouched.
    [GeneratedRegex(@"\{#([A-Za-z0-9._-]+)\}\s*$")]
    private static partial Regex HeadingAnchorRegex();

    // A picture on a line by itself. Inline images inside a sentence stay out of this on purpose: the block
    // list has no room for one, and a paragraph interrupted by a picture is not a shape the documentation
    // needs. Anything else keeps parsing exactly as it did before this kind existed.
    [GeneratedRegex(@"^\s*!\[([^\]]*)\]\(([^)]+)\)\s*$")]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"^\s*(?:[-*+]|\d+\.)\s+(.*)$")]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"^\s*\d+\.\s+")]
    private static partial Regex OrderedItemRegex();

    [GeneratedRegex(@"^\s*(\d+)\.\s+")]
    private static partial Regex OrderedNumberRegex();

    // This runs after every pipe line, including arbitrary tracker content; adjacent `\s*` made whitespace quadratic on the UI thread (AC-303).
    // `NonBacktracking` is linear and valid because this pattern needs neither lookaround nor backreferences.
    [GeneratedRegex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$", RegexOptions.NonBacktracking)]
    private static partial Regex TableSeparatorRegex();
}
