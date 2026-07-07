namespace Cockpit.Core.Voice;

/// <summary>
/// Pure "when to trust the cleaned transcript" decisions, kept separate from the Ollama HTTP call so
/// they are unit-testable without a running Ollama daemon (the on-disk/off-hardware safety net asked
/// for by the voice-input spec: "bij twijfel/down → rauwe transcript").
/// </summary>
public static class TranscriptCleanupGuard
{
    /// <summary>
    /// True when the raw transcript is too short to be worth sending to the LLM at all — cleanup adds
    /// latency and hallucination risk for a one- or two-word utterance for no real benefit.
    /// </summary>
    public static bool ShouldSkipCleanup(string rawText, TranscriptCleanupOptions options)
    {
        var wordCount = rawText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return wordCount < options.MinWordCount;
    }

    /// <summary>
    /// True when the cleaned output looks like a hallucination rather than a punctuation/filler pass:
    /// empty, or grown past <c>raw.Length * MaxLengthRatio + MaxLengthPadding</c> characters.
    /// </summary>
    public static bool IsSuspicious(string rawText, string cleanedText, TranscriptCleanupOptions options)
    {
        if (string.IsNullOrWhiteSpace(cleanedText))
        {
            return true;
        }

        var maxLength = rawText.Length * options.MaxLengthRatio + options.MaxLengthPadding;
        return cleanedText.Length > maxLength;
    }
}
