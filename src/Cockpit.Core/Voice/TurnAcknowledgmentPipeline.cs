using Cockpit.Core.Abstractions.Voice;

namespace Cockpit.Core.Voice;

/// <summary>
/// The one place a turn-start acknowledgement (AC-99) becomes queued speech, so the real turn path and any test
/// seam render it the same way. <see cref="TurnAckMode.InstantPhrases"/> voices a rotating preset (zero latency,
/// no model); <see cref="TurnAckMode.LocalLlm"/> asks the local model for a contextual line through the same
/// hardened, single-flight cleanup service the rest of read-aloud uses, and falls back to a preset when it is
/// slow, unavailable or returns nothing; <see cref="TurnAckMode.Off"/> is a no-op.
/// </summary>
public static class TurnAcknowledgmentPipeline
{
    /// <summary>
    /// Enqueues one acknowledgement for <paramref name="userMessage"/> in the chosen <paramref name="mode"/>, voiced by
    /// <paramref name="speakerId"/> in <paramref name="language"/>. <paramref name="phraseIndex"/> rotates the presets so
    /// back-to-back turns do not repeat; the returned value is the index to pass next (unchanged when a generated line
    /// was spoken instead of a preset).
    /// </summary>
    public static async Task<int> SpeakAsync(
        IVoicePlaybackQueue queue,
        ITranscriptCleanupService? cleanupService,
        TurnAckMode mode,
        int phraseIndex,
        string userMessage,
        int speakerId,
        string language)
    {
        if (mode == TurnAckMode.Off)
        {
            return phraseIndex;
        }

        var phrases = TurnAcknowledgmentPhrases.For(language);
        if (phrases.Count == 0)
        {
            return phraseIndex;
        }

        if (mode == TurnAckMode.LocalLlm && cleanupService is not null)
        {
            var generated = (await cleanupService.AcknowledgeForSpeechAsync(userMessage).ConfigureAwait(false)).Trim();
            if (generated.Length > 0)
            {
                queue.Enqueue([generated], speakerId, language);
                return phraseIndex;
            }
        }

        queue.Enqueue([phrases[phraseIndex % phrases.Count]], speakerId, language);
        return phraseIndex + 1;
    }
}
