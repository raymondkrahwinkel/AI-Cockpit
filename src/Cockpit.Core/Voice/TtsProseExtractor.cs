using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Cockpit.Core.Markdown;

namespace Cockpit.Core.Voice;

/// <summary>
/// Turns an assistant transcript entry's markdown into the sentences worth reading aloud: prose from
/// headings, paragraphs and list items, skipping fenced code and tables — literal syntax and pipe
/// characters read poorly aloud and carry no spoken meaning. Pure and testable: no dependency on the
/// TTS engine itself.
/// </summary>
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

    /// <summary>Appends a block's plain text, forcing a sentence boundary after it — headings and list items rarely end in punctuation, and without one the next block would run on into the same spoken sentence.</summary>
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

    /// <summary>
    /// Strips the characters that read poorly (or as nothing) aloud — emoji and pictographs (the "✅ 🌙 👋"
    /// noise the model sprinkles through prose) plus their joiners/skin-tone modifiers — then collapses the
    /// whitespace the removal leaves behind so a stripped emoji never turns "word 🌙." into "word ." with a
    /// dangling space. Currency and maths symbols are deliberately kept: "€5" and "2 + 2" carry spoken meaning.
    /// </summary>
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

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseWhitespace();

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"(?:[A-Za-z]:\\|\\\\)[^\s""']+")]
    private static partial Regex WindowsPathPattern();

    [GeneratedRegex(@"(?<![\w.])~?/[\w.\-]+(?:/[\w.\-]+)+/?")]
    private static partial Regex UnixPathPattern();
}
