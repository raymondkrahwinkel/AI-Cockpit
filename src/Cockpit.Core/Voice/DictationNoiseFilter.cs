using System.Text.RegularExpressions;

namespace Cockpit.Core.Voice;

/// <summary>
/// Deterministic noise removal for a raw dictation transcript, so a normal spoken sentence reaches the agent as
/// intent rather than as everything the microphone happened to catch. Runs on every dictation path (push-to-talk
/// and open-mic, SDK and TTY) — unlike the LLM cleanup, which is SDK-only and skips short utterances, exactly the
/// cases ("um", a throat-clear) this has to catch. Two kinds of noise are dropped:
/// <list type="bullet">
/// <item><b>Whisper's non-speech tags</b> — sound events it heard but that were not speech, wrapped in asterisks,
/// square brackets or parentheses: <c>*Clears throat*</c>, <c>[BLANK_AUDIO]</c>, <c>(coughs)</c>. Whisper does not
/// use those wrappers for dictated words, so removing the wrapped spans is safe.</item>
/// <item><b>Hesitation fillers</b> — standalone "um", "uh", "uhm", "erm", "ehm", "hmm", "mmm" (and their drawn-out
/// spellings). The set is deliberately cross-language-safe: it excludes tokens that are real words in English or
/// Dutch (notably "er" and "eh"), and word boundaries keep it from touching "umbrella" or "summary".</item>
/// </list>
/// Returns the cleaned text, trimmed — empty when the utterance was nothing but noise, which the caller drops
/// instead of injecting or auto-submitting.
/// </summary>
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

    // A span wrapped in *...* or [...] (Whisper only ever uses those for sound events, never for dictated words, so
    // any content is safe to drop), or a *single-token* parenthesis like "(coughs)"/"(laughs)". The parenthesis arm
    // is deliberately narrower — a person genuinely speaks multi-word parentheticals ("the result (about ten
    // percent) is fine"), and those must survive, whereas Whisper's parenthesised cues are single words. None cross a
    // line break, so a stray bracket cannot swallow a whole paragraph.
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
