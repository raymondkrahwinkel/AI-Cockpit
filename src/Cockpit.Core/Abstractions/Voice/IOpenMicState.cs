namespace Cockpit.Core.Abstractions.Voice;

/// <summary>
/// Whether continuous open-mic dictation is listening right now, and how a push-to-talk hold takes the
/// microphone off it for the length of that hold (AC-627).
/// </summary>
public interface IOpenMicState
{
    bool IsListening { get; }

    /// <summary>
    /// Pauses open-mic until the handle is disposed, dropping the utterance it was half-way through and anything
    /// transcribed but not yet sent. A handle that does nothing when open-mic is not listening. Pausing alone leaks:
    /// an utterance closed as the key went down is already being transcribed and arrives afterwards, which is why
    /// this is one call rather than the listener's own Pause/Resume (AC-627).
    /// </summary>
    IDisposable SuspendForHold();
}
