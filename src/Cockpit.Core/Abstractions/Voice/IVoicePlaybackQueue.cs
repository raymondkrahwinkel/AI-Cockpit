using Cockpit.Core.Voice;

namespace Cockpit.Core.Abstractions.Voice;

/// <summary>
/// FIFO read-aloud playback: sentences enqueued together play back-to-back, never overlapping with
/// another queued utterance or with each other. Single shared instance for the whole (single-user)
/// cockpit, so a push-to-talk hold on any session can interrupt whichever session is currently talking.
/// </summary>
public interface IVoicePlaybackQueue
{
    /// <summary>Queues <paramref name="sentences"/> for playback with the given speaker and language, appended after whatever is already queued.</summary>
    void Enqueue(IReadOnlyList<string> sentences, int speakerId, string language);

    /// <summary>
    /// Queues language-routed <paramref name="segments"/> for playback: the single Supertonic voice
    /// (<paramref name="speakerId"/>) speaks each segment in its own language, back-to-back — no silence gap,
    /// since one voice reading two languages has no timbre jump to bridge.
    /// </summary>
    void Enqueue(IReadOnlyList<SpeechSegment> segments, int speakerId);

    /// <summary>
    /// Marks read-aloud as active before anything is queued, so the overlay shows it is working during the
    /// gap the operator otherwise sees as silence — the local-LLM cleanup/naturalize/summarize pass and the
    /// first synthesis (including the one-time model download) that run before any audio plays. Raises
    /// <see cref="PlaybackActiveChanged"/> the same as real playback; the batch that follows clears it when it
    /// finishes, and <see cref="StopAll"/> clears it if nothing ends up queued.
    /// </summary>
    void NotifyPreparing();

    /// <summary>
    /// Raised when read-aloud playback becomes active (a batch starts, or <see cref="NotifyPreparing"/> is called)
    /// or goes idle (the queue drains), so open-mic dictation can pause itself while the cockpit is speaking and
    /// never transcribe its own text-to-speech. "Active" spans both the preparing and speaking phases. Fires on the
    /// playback consumer thread — subscribers marshal as needed.
    /// </summary>
    event EventHandler<bool>? PlaybackActiveChanged;

    /// <summary>
    /// Raised the moment the first synthesized clip actually starts playing, once per active window — the boundary
    /// between "preparing" (the local-LLM rewrite + text-to-sound synthesis, still silent) and "speaking". Lets the
    /// overlay show a distinct status while it is getting ready rather than claiming to read aloud before a word.
    /// </summary>
    event EventHandler? SpeakingStarted;

    /// <summary>Cancels whatever is currently synthesizing/playing and discards anything still queued.</summary>
    void StopAll();

    /// <summary>
    /// A counter bumped by every <see cref="StopAll"/>. A caller that awaits a slow step (the local-LLM rewrite)
    /// between <see cref="NotifyPreparing"/> and enqueuing reads this before the await and again after: if it
    /// changed, a barge-in (or a newer turn) cancelled read-aloud while it was preparing, so the now-stale batch
    /// must be dropped instead of spoken over the interrupt.
    /// </summary>
    int Generation { get; }
}
