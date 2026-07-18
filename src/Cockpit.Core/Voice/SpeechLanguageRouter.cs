using System.Text.RegularExpressions;

namespace Cockpit.Core.Voice;

/// <summary>
/// Splits read-aloud text carrying inline language markers — <c>[[nl]]</c> / <c>[[en]]</c>, emitted by the
/// naturalization LLM — into per-language <see cref="SpeechSegment"/>s. The single Supertonic voice then
/// speaks each segment in its tagged language; text before the first marker (and any unknown marker) speaks
/// in <see cref="DefaultLanguage"/>, and text with no markers at all yields a single default-language
/// segment (the pre-routing behaviour). Pure and testable: no dependency on the TTS engine or settings.
/// </summary>
public static partial class SpeechLanguageRouter
{
    private const string EnglishLanguage = "en";

    private const string DutchLanguage = "nl";

    /// <summary>ISO-639-1 code spoken for unmarked lead-in text and unknown markers when a caller does not specify one — English, the assistant's primary output language.</summary>
    public const string DefaultLanguage = EnglishLanguage;

    public static IReadOnlyList<SpeechSegment> Route(string text) => Route(text, DefaultLanguage);

    /// <summary>Routes with <paramref name="defaultLanguage"/> as the base for unmarked text and unknown markers, so the operator's preferred read-aloud language leads.</summary>
    public static IReadOnlyList<SpeechSegment> Route(string text, string defaultLanguage)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var segments = new List<SpeechSegment>();
        foreach (var (language, runText) in _SplitIntoRuns(text, defaultLanguage))
        {
            // The extractor already strips emoji/paths and splits into sentences; markers are gone by now
            // since _SplitIntoRuns consumed them, so it only ever sees the spoken text of one language run.
            var sentences = TtsProseExtractor.Extract(runText);
            if (sentences.Count == 0)
            {
                continue;
            }

            // Adjacent runs in the same language (unmarked lead-in before an [[en]] run, or the model
            // repeating a marker) fold into one segment.
            if (segments.Count > 0 && segments[^1].Language == language)
            {
                var merged = segments[^1].Sentences.Concat(sentences).ToList();
                segments[^1] = segments[^1] with { Sentences = merged };
            }
            else
            {
                segments.Add(new SpeechSegment(sentences, language));
            }
        }

        return segments;
    }

    private static IEnumerable<(string Language, string Text)> _SplitIntoRuns(string text, string defaultLanguage)
    {
        var currentLanguage = defaultLanguage;
        var lastIndex = 0;
        foreach (Match match in LanguageMarker().Matches(text))
        {
            if (match.Index > lastIndex)
            {
                yield return (currentLanguage, text[lastIndex..match.Index]);
            }

            currentLanguage = _ResolveLanguage(match.Groups[1].Value, defaultLanguage);
            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < text.Length)
        {
            yield return (currentLanguage, text[lastIndex..]);
        }
    }

    private static string _ResolveLanguage(string languageCode, string defaultLanguage) =>
        languageCode.ToLowerInvariant() switch
        {
            DutchLanguage => DutchLanguage,
            EnglishLanguage => EnglishLanguage,
            _ => defaultLanguage,
        };

    [GeneratedRegex(@"\[\[\s*([A-Za-z]{2})\s*\]\]")]
    private static partial Regex LanguageMarker();
}
