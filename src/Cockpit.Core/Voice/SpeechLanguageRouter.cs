using System.Text.RegularExpressions;

namespace Cockpit.Core.Voice;

/// <summary>
/// Splits read-aloud text carrying inline language markers — <c>[[nl]]</c> / <c>[[en]]</c>, emitted by the
/// naturalization LLM — into per-language <see cref="SpeechSegment"/>s, each routed to the matching Piper
/// voice. Text before the first marker (and any unknown marker) speaks in the English/primary voice; text
/// with no markers at all yields a single segment in that voice, i.e. the pre-routing behaviour. Pure and
/// testable: no dependency on the TTS engine or settings.
/// </summary>
public static partial class SpeechLanguageRouter
{
    public static IReadOnlyList<SpeechSegment> Route(string text, string englishVoiceId, string dutchVoiceId)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var segments = new List<SpeechSegment>();
        foreach (var (voiceId, runText) in _SplitIntoRuns(text, englishVoiceId, dutchVoiceId))
        {
            // The extractor already strips emoji/paths and splits into sentences; markers are gone by now
            // since _SplitIntoRuns consumed them, so it only ever sees the spoken text of one language run.
            var sentences = TtsProseExtractor.Extract(runText);
            if (sentences.Count == 0)
            {
                continue;
            }

            // Adjacent runs in the same voice (unmarked lead-in before an [[en]] run, or the model
            // repeating a marker) fold into one segment so no needless silence gap splits them.
            if (segments.Count > 0 && segments[^1].VoiceId == voiceId)
            {
                var merged = segments[^1].Sentences.Concat(sentences).ToList();
                segments[^1] = segments[^1] with { Sentences = merged };
            }
            else
            {
                segments.Add(new SpeechSegment(sentences, voiceId));
            }
        }

        return segments;
    }

    private static IEnumerable<(string VoiceId, string Text)> _SplitIntoRuns(string text, string englishVoiceId, string dutchVoiceId)
    {
        var currentVoice = englishVoiceId;
        var lastIndex = 0;
        foreach (Match match in LanguageMarker().Matches(text))
        {
            if (match.Index > lastIndex)
            {
                yield return (currentVoice, text[lastIndex..match.Index]);
            }

            currentVoice = _ResolveVoice(match.Groups[1].Value, englishVoiceId, dutchVoiceId);
            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < text.Length)
        {
            yield return (currentVoice, text[lastIndex..]);
        }
    }

    private static string _ResolveVoice(string languageCode, string englishVoiceId, string dutchVoiceId) =>
        languageCode.ToLowerInvariant() switch
        {
            "nl" => dutchVoiceId,
            _ => englishVoiceId,
        };

    [GeneratedRegex(@"\[\[\s*([A-Za-z]{2})\s*\]\]")]
    private static partial Regex LanguageMarker();
}
