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
        var text = string.Concat(inlines.Select(inline => inline.Text)).Trim();
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

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceBoundary();

    [GeneratedRegex(@"[.!?]$")]
    private static partial Regex SentenceEndingPunctuation();
}
