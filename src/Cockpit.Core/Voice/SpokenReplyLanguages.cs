using System.Text.RegularExpressions;

namespace Cockpit.Core.Voice;

public static class SpokenReplyLanguages
{
    public static IReadOnlyList<SpeechSegment> Segment(IReadOnlyList<string> sentences)
    {
        var segments = new List<SpeechSegment>();
        string language = "en";
        foreach (string sentence in sentences)
        {
            string[] words = Regex.Matches(sentence.ToLowerInvariant(), @"\p{L}+").Select(match => match.Value).ToArray();
            int dutch = words.Count(word => Dutch.Contains(word));
            int german = words.Count(word => German.Contains(word));
            int english = words.Count(word => English.Contains(word));
            // shortcut: ambiguous sentences inherit the preceding language; use a detector if more languages are required.
            if (dutch > german && dutch > english)
            {
                language = "nl";
            }
            else if (german > dutch && german > english)
            {
                language = "de";
            }
            else if (english > 0 && english >= dutch && english >= german)
            {
                language = "en";
            }
            segments.Add(new SpeechSegment([sentence], language));
        }
        return segments;
    }

    private static readonly HashSet<string> Dutch = ["de", "het", "een", "en", "van", "voor", "niet", "zijn", "dit", "dat", "met", "je", "ik", "we", "wordt", "heb", "heeft", "kan", "naar", "ook"];
    private static readonly HashSet<string> German = ["der", "die", "das", "ein", "eine", "und", "von", "für", "nicht", "sind", "ist", "mit", "ich", "wir", "wird", "haben", "kann", "auch", "sie", "zu"];
    private static readonly HashSet<string> English = ["the", "a", "an", "and", "of", "for", "not", "are", "is", "this", "that", "with", "you", "i", "we", "will", "have", "has", "can", "to"];
}
