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
    /// Raised when read-aloud playback becomes active (a batch starts) or goes idle (the queue drains),
    /// so open-mic dictation can pause itself while the cockpit is speaking and never transcribe its own
    /// text-to-speech. Fires on the playback consumer thread — subscribers marshal as needed.
    /// </summary>
    event EventHandler<bool>? PlaybackActiveChanged;

    /// <summary>Cancels whatever is currently synthesizing/playing and discards anything still queued.</summary>
    void StopAll();
}
