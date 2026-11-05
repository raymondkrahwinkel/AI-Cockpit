using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Cockpit.Core.Markdown;

namespace Cockpit.Core.Voice;

public sealed record TtsProseStream(IReadOnlyList<string> Sentences, int ConsumedLength);

// Turns an assistant transcript entry's markdown into the sentences worth reading aloud: prose from headings,
// paragraphs and list items, skipping fenced code and tables — literal syntax and pipe characters read poorly
// aloud. Pure and testable: no dependency on the TTS engine itself.
public static partial class TtsProseExtractor
{
    public static IReadOnlyList<string> Extract(string assistantMarkdown)
    {
        if (string.IsNullOrWhiteSpace(assistantMarkdown))
        {
            return [];
        }

        var prose = new StringBuilder();
        foreach (var block in MarkdownParser.Parse(assistantMarkdown))
        {
            switch (block.Kind)
            {
                case MarkdownBlockKind.CodeBlock:
                case MarkdownBlockKind.Table:
                    continue;

                case MarkdownBlockKind.List:
                    foreach (var item in block.Items)
                    {
                        _AppendAsSentence(prose, item);
                    }

                    break;

                default:
                    _AppendAsSentence(prose, block.Inlines);
                    break;
            }
        }

        return _SplitSentences(prose.ToString());
    }

    // A stream may still turn its trailing text into a code block or table, so only hand the stable prefix to TTS.
    public static TtsProseStream ExtractCompleted(string assistantMarkdown)
    {
        var consumedLength = _CompletedPrefixLength(assistantMarkdown);
        return consumedLength == 0
            ? new TtsProseStream([], 0)
            : new TtsProseStream(Extract(assistantMarkdown[..consumedLength]), consumedLength);
    }

    private static int _CompletedPrefixLength(string markdown)
    {
        var completedLength = 0;
        var lineStart = 0;
        var insideFence = false;
        var insideTable = false;
        (int Start, int End)? possibleTableHeader = null;

        while (lineStart < markdown.Length)
        {
            var newline = markdown.IndexOf('\n', lineStart);
            var lineEnd = newline < 0 ? markdown.Length : newline;
            var nextLineStart = newline < 0 ? markdown.Length : newline + 1;
            var line = markdown[lineStart..lineEnd];
            var lineIsComplete = newline >= 0 || SentenceEndingPunctuation().IsMatch(line) || line.TrimStart().StartsWith("```", StringComparison.Ordinal);
            if (!lineIsComplete)
            {
                break;
            }

            if (insideFence)
            {
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    insideFence = false;
                    completedLength = nextLineStart;
                }

                lineStart = nextLineStart;
                continue;
            }

            if (possibleTableHeader is { } header)
            {
                if (TableSeparator().IsMatch(line))
                {
                    insideTable = true;
                    possibleTableHeader = null;
                    lineStart = nextLineStart;
                    continue;
                }

                completedLength = _CompletedRegularLine(markdown, header.Start, header.End, completedLength);
                possibleTableHeader = null;
            }

            if (insideTable)
            {
                if (string.IsNullOrWhiteSpace(line) || !line.Contains('|'))
                {
                    insideTable = false;
                }
                else
                {
                    lineStart = nextLineStart;
                    continue;
                }
            }

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                insideFence = true;
            }
            else if (line.Contains('|'))
            {
                possibleTableHeader = (lineStart, nextLineStart);
            }
            else
            {
                completedLength = _CompletedRegularLine(markdown, lineStart, nextLineStart, completedLength);
            }

            lineStart = nextLineStart;
        }

        return completedLength;
    }

    private static int _CompletedRegularLine(string markdown, int lineStart, int lineEnd, int completedLength)
    {
        var line = markdown[lineStart..lineEnd];
        if (string.IsNullOrWhiteSpace(line))
        {
            return lineEnd;
        }

        if (ListItem().IsMatch(line) && line.EndsWith('\n'))
        {
            completedLength = lineEnd;
        }

        for (var index = lineStart; index < lineEnd; index++)
        {
            if (markdown[index] is '.' or '!' or '?' && (index + 1 == markdown.Length || char.IsWhiteSpace(markdown[index + 1])))
            {
                completedLength = index + 1;
            }
        }

        return completedLength;
    }

    // Appends a block's plain text, forcing a sentence boundary after it — headings and list items rarely end in punctuation, and without one the next block would run on into the same spoken sentence.
    private static void _AppendAsSentence(StringBuilder builder, IReadOnlyList<MarkdownInline> inlines)
    {
        // Inline code is kept (short identifiers like `git` read fine) — but a path or URL inside it is
        // cleaned by _StripNonSpeech just like one in plain prose, so "C:\…" does not get spelled out.
        var text = _StripNonSpeech(string.Concat(inlines.Select(inline => inline.Text)));
        if (text.Length == 0)
        {
            return;
        }

        builder.Append(text);
        if (!SentenceEndingPunctuation().IsMatch(text))
        {
            builder.Append('.');
        }

        builder.Append(' ');
    }

    private static IReadOnlyList<string> _SplitSentences(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return [];
        }

        return SentenceBoundary().Split(trimmed)
            .Select(sentence => sentence.Trim())
            .Where(sentence => sentence.Length > 0)
            .ToList();
    }

    // AC-1013: Strips characters that read poorly aloud — emoji/pictographs plus joiners/skin-tone modifiers — then collapses leftover whitespace so a stripped emoji doesn't leave a dangling space. Currency/maths symbols are kept: "€5" and "2 + 2" carry spoken meaning.
    private static string _StripNonSpeech(string text)
    {
        // Replace things that read terribly aloud with a short natural stand-in before stripping symbols:
        // URLs ("https://…") and file paths (Windows "C:\…" or POSIX "/home/…") become "a link"/"a path"
        // so a sentence stays speakable instead of the engine spelling out every slash and separator.
        text = UrlPattern().Replace(text, "a link");
        text = WindowsPathPattern().Replace(text, "a path");
        text = UnixPathPattern().Replace(text, "a path");

        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (!_IsNonSpeechSymbol(rune))
            {
                builder.Append(rune.ToString());
            }
        }

        return CollapseWhitespace().Replace(builder.ToString(), " ").Trim();
    }

    private static bool _IsNonSpeechSymbol(Rune rune) =>
        Rune.GetUnicodeCategory(rune) == UnicodeCategory.OtherSymbol   // ✅ ⚠️ 🌙 👋 😄 🪜 and the like
        || rune.Value is 0x200D or 0xFE0F or 0x20E3                    // zero-width joiner, variation selector, keycap
        || rune.Value is >= 0x1F3FB and <= 0x1F3FF                     // skin-tone modifiers
        || rune.Value is >= 0x1F1E6 and <= 0x1F1FF;                    // regional indicators (flag letters)

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceBoundary();

    [GeneratedRegex(@"[.!?]$")]
    private static partial Regex SentenceEndingPunctuation();

    [GeneratedRegex(@"^\s*(?:[-*+]|\d+[.)])\s+")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$", RegexOptions.NonBacktracking)]
    private static partial Regex TableSeparator();

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseWhitespace();

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"(?:[A-Za-z]:\\|\\\\)[^\s""']+")]
    private static partial Regex WindowsPathPattern();

    [GeneratedRegex(@"(?<![\w.])~?/[\w.\-]+(?:/[\w.\-]+)+/?")]
    private static partial Regex UnixPathPattern();
}
