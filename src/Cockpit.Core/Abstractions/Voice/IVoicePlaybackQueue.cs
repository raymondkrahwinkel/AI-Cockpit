namespace Cockpit.Core.Abstractions.Voice;

/// <summary>
/// FIFO read-aloud playback: sentences enqueued together play back-to-back, never overlapping with
/// another queued utterance or with each other. Single shared instance for the whole (single-user)
/// cockpit, so a push-to-talk hold on any session can interrupt whichever session is currently talking.
/// </summary>
public interface IVoicePlaybackQueue
{
    /// <summary>Queues <paramref name="sentences"/> for playback in the given voice, appended after whatever is already queued.</summary>
    void Enqueue(IReadOnlyList<string> sentences, string voiceId);

    /// <summary>Cancels whatever is currently synthesizing/playing and discards anything still queued.</summary>
    void StopAll();
}
