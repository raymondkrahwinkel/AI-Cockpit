namespace Cockpit.Core.Voice;

public static class SpokenReply
{
    public static IReadOnlyList<string> Select(string markdown, SpokenReplyMode mode)
    {
        IReadOnlyList<string> sentences = TtsProseExtractor.Extract(markdown, usePlaceholders: mode == SpokenReplyMode.Full);
        if (mode == SpokenReplyMode.Full)
        {
            return sentences;
        }

        int sentenceLimit = mode == SpokenReplyMode.Short ? 2 : 5;
        int remaining = mode == SpokenReplyMode.Short ? 220 : 450;
        var selected = new List<string>();
        foreach (string sentence in sentences.Take(sentenceLimit))
        {
            if (sentence.Length <= remaining)
            {
                selected.Add(sentence);
                remaining -= sentence.Length + 1;
                continue;
            }

            int end = Math.Min(remaining, sentence.Length);
            while (end > 0 && !char.IsWhiteSpace(sentence[end]))
            {
                end--;
            }
            if (end > 0)
            {
                selected.Add(sentence[..end].TrimEnd());
            }
            break;
        }
        return selected;
    }
}
