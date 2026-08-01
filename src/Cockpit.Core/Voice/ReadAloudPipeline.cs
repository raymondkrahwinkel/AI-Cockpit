using Cockpit.Core.Abstractions.Voice;

namespace Cockpit.Core.Voice;

/// <summary>
/// The one place read-aloud text becomes queued speech, shared by the real turn path
/// (<c>SessionPanelViewModel</c>) and the Options "Test" button (<c>CockpitViewModel</c>) so the two can never
/// drift. Extracts spoken prose, shows the preparing overlay across the (possibly multi-second) local-LLM
/// rewrite, and enqueues the result — unless a barge-in cancelled read-aloud while it was preparing, in which
/// case the now-stale batch is dropped rather than spoken over the interrupt.
/// </summary>
public static class ReadAloudPipeline
{
    /// <summary>
    /// Renders <paramref name="text"/> for read-aloud in the given <paramref name="mode"/> and enqueues it on
    /// <paramref name="queue"/> with the chosen <paramref name="speakerId"/> and base <paramref name="language"/>.
    /// Naturalized/Summarized route the prose through <paramref name="cleanupService"/> first (falling back to the
    /// plain prose when it is unavailable or returns nothing); Verbatim speaks the extracted prose as-is. A no-op
    /// when there is nothing to say. Callers that must stop current playback first (the Test button) do so before
    /// calling — this only ever appends.
    /// </summary>
    /// <param name="asOneUtterance">
    /// Speak the whole thing as a single synthesis instead of sentence by sentence.
    /// <para>
    /// The queue normally synthesises one sentence ahead while the previous plays, which is right when the reply is
    /// long: you hear the first sentence within a second instead of waiting for the whole thing. It only works
    /// while synthesis keeps up with playback, and measured on this machine it does not — four short sentences took
    /// 14.7 seconds to get through about 8 seconds of speech, so roughly half of it was silence at the sentence
    /// boundaries. Raymond, 2026-08-01: "er is echt een hele ruimte tussen qua uitspraak". Deeper look-ahead cannot
    /// close a gap that grows; only synthesising once can.
    /// </para>
    /// <para>
    /// Off for ordinary sessions, where an agent's reply can run for paragraphs and one synthesis would be a long
    /// silence before the first word. On for the assistant, whose answers are short by instruction — its system
    /// prompt tells it to put the answer in the first sentence and keep it speakable — so the wait it trades away
    /// is small and the fluency it buys is the whole point of a spoken assistant.
    /// </para>
    /// </param>
    public static async Task SpeakAsync(
        IVoicePlaybackQueue queue,
        ITranscriptCleanupService? cleanupService,
        string text,
        ReadAloudMode mode,
        int speakerId,
        string language,
        bool asOneUtterance = false)
    {
        var sentences = TtsProseExtractor.Extract(text);
        if (asOneUtterance && sentences.Count > 1)
        {
            // Joined after extraction, not instead of it: the extractor is what strips code blocks and tables out
            // of something that would otherwise be read character by character, and that job is unrelated to how
            // many clips the result is spoken in.
            sentences = [string.Join(" ", sentences)];
        }

        if (sentences.Count == 0)
        {
            return;
        }

        // Show the overlay now: the rewrite and the first synthesis (and any first-use model download) run before a
        // word is heard, and that gap otherwise reads as nothing happening.
        queue.NotifyPreparing();
        var generation = queue.Generation;

        if (cleanupService is not null && mode is ReadAloudMode.Naturalized or ReadAloudMode.Summarized)
        {
            var joined = string.Join(" ", sentences);
            var rewritten = mode == ReadAloudMode.Summarized
                ? await cleanupService.SummarizeForSpeechAsync(joined).ConfigureAwait(false)
                : await cleanupService.NaturalizeForSpeechAsync(joined).ConfigureAwait(false);

            // A barge-in (or a newer read-aloud turn) during the rewrite bumped the generation — drop this batch
            // instead of speaking over the interrupt the operator just made.
            if (queue.Generation != generation)
            {
                return;
            }

            var segments = SpeechLanguageRouter.Route(rewritten, language);
            if (segments.Count > 0)
            {
                queue.Enqueue(segments, speakerId);
                return;
            }
        }

        queue.Enqueue(sentences, speakerId, language);
    }
}
