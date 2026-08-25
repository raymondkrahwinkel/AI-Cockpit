using System.Text.RegularExpressions;

namespace Cockpit.Core.Voice;

// AC-1013: Deterministic noise removal for a raw dictation transcript, run on every dictation path (unlike the LLM cleanup, which is SDK-only and skips short utterances like "um"). Drops Whisper's non-speech tags (`*Clears throat*`, `[BLANK_AUDIO]`, `(coughs)`) and cross-language-safe hesitation fillers ("um", "uh", …, excluding real words like "er"/"eh"). Returns cleaned text, empty when the utterance was nothing but noise.
public static partial class DictationNoiseFilter
{
    public static string Strip(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var stripped = NonSpeechTag().Replace(text, " ");
        stripped = Filler().Replace(stripped, " ");
        stripped = WhitespaceRun().Replace(stripped, " ").Trim();

        // A filler at the start leaves an orphan separator ("Um, so" → ", so"); drop leading punctuation/space.
        return LeadingPunctuation().Replace(stripped, string.Empty).Trim();
    }

    // AC-1013: A span wrapped in *...* or [...] (always sound events for Whisper, safe to drop), or a *single-token* parenthesis like "(coughs)" — narrower than *...*/[...] because people genuinely speak multi-word parentheticals that must survive. None cross a line break.
    [GeneratedRegex(@"\*[^*\r\n]*\*|\[[^\]\r\n]*\]|\([^)\s\r\n]+\)")]
    private static partial Regex NonSpeechTag();

    // Standalone hesitation fillers, case-insensitive, with drawn-out spellings (um/umm, uh/uhh, hmm/hmmm, …), plus
    // an optional trailing comma so "I think, um, we" collapses to "I think, we" rather than leaving a double comma.
    // No "er"/"eh": "er" is a common Dutch word and "eh" is ambiguous, and stripping them would eat real speech.
    [GeneratedRegex(@"(?i)\b(?:um+|uh+|uhm+|erm|ehm|hmm+|mmm+)\b,?")]
    private static partial Regex Filler();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    [GeneratedRegex(@"^[\s,.;:!?-]+")]
    private static partial Regex LeadingPunctuation();
}
