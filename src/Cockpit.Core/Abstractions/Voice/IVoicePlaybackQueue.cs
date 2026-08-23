using Cockpit.Core.Voice;

namespace Cockpit.Core.Abstractions.Voice;

/// <summary>
/// FIFO read-aloud playback: sentences enqueued together play back-to-back, never overlapping with
/// another queued utterance or with each other. Single shared instance for the whole (single-user)
/// cockpit, so a push-to-talk hold on any session can interrupt whichever session is currently talking.
/// </summary>
public interface IVoicePlaybackQueue
{
    /// <summary>
    /// Queues <paramref name="sentences"/> for playback with the given speaker and language, appended after whatever is already queued.
    /// </summary>
    void Enqueue(IReadOnlyList<string> sentences, int speakerId, string language, VoicePlaybackSource source = VoicePlaybackSource.Session);

    /// <summary>
    /// Queues language-routed <paramref name="segments"/> for playback: the single Supertonic voice
    /// (<paramref name="speakerId"/>) speaks each segment in its own language, back-to-back — no silence gap,
    /// since one voice reading two languages has no timbre jump to bridge.
    /// </summary>
    void Enqueue(IReadOnlyList<SpeechSegment> segments, int speakerId, VoicePlaybackSource source = VoicePlaybackSource.Session);

    /// <summary>
    /// Marks read-aloud as active before anything is queued, so the overlay shows it is working during the gap the
    /// operator otherwise sees as silence — the first synthesis, including the one-time model download, which runs
    /// before any audio plays. Raises <see cref="PlaybackActiveChanged"/> like real playback; the batch that follows clears it when it finishes, and <see cref="StopAll"/> clears it if nothing ends up queued.
    /// </summary>
    void NotifyPreparing(VoicePlaybackSource source = VoicePlaybackSource.Session);

    /// <summary>
    /// Raised when read-aloud playback becomes active (a batch starts, or <see cref="NotifyPreparing"/> is called) or
    /// goes idle (the queue drains), so open-mic dictation can pause itself while the cockpit is speaking and never
    /// transcribe its own text-to-speech. "Active" spans both preparing and speaking. Fires on the playback consumer thread.
    /// </summary>
    event EventHandler<bool>? PlaybackActiveChanged;

    /// <summary>
    /// Raised the moment the first synthesized clip actually starts playing, once per active window — the boundary
    /// between "preparing" (text-to-sound synthesis, still silent) and "speaking". Lets the overlay show a distinct
    /// status while it is getting ready rather than claiming to read aloud before a word.
    /// </summary>
    event EventHandler? SpeakingStarted;

    /// <summary>
    /// Cancels whatever is currently synthesizing/playing and discards anything still queued.
    /// </summary>
    void StopAll();

    /// <summary>
    /// A counter bumped by every <see cref="StopAll"/>. A caller preparing a batch reads it before
    /// <see cref="NotifyPreparing"/> and again before <see cref="Enqueue(IReadOnlyList{string}, int, string)"/>: if changed, a barge-in (or newer turn) cancelled read-aloud in between — <see cref="StopAll"/> is called from the hold's own thread and from <see cref="PlaybackActiveChanged"/> subscribers, so the stale batch must be dropped instead of spoken over the interrupt.
    /// </summary>
    int Generation { get; }

    /// <summary>
    /// The <see cref="VoicePlaybackSource"/> of whatever is currently preparing or playing (AC-729). Meaningless
    /// while idle; read it when <see cref="PlaybackActiveChanged"/>/<see cref="SpeakingStarted"/> fires, the same
    /// way <see cref="Generation"/> is read around a call rather than carried on the event itself.
    /// </summary>
    VoicePlaybackSource ActiveSource { get; }
}
