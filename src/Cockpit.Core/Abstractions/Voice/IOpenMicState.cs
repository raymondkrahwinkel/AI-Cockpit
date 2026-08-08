namespace Cockpit.Core.Abstractions.Voice;

/// <summary>
/// Whether continuous open-mic dictation is listening right now, and the seam a push-to-talk hold uses to take
/// the microphone off it for the length of that hold.
/// </summary>
/// <remarks>
/// It used to be the other way around: open-mic won and the hold stood down, on the reasoning that both
/// transcribe the same speech. What that missed is that they do not send it to the same place (AC-627). Open-mic
/// hands every utterance straight to the assistant, which sends it; a hold puts the words in the selected
/// session's composer, where they can be read before they go. Standing the hold down did not merely drop a
/// duplicate — it silently changed both the recipient and whether the operator got to look at the text first.
/// So the hold wins now, and open-mic steps aside for its duration via <see cref="SuspendForHold"/>.
/// </remarks>
public interface IOpenMicState
{
    bool IsListening { get; }

    /// <summary>
    /// Takes the microphone off open-mic until the returned handle is disposed: detection is paused, whatever
    /// the voice-activity detector was half-way through is abandoned, and anything already transcribed but not
    /// yet handed to the assistant is dropped. A no-op — but still a disposable — when open-mic is not listening.
    /// </summary>
    /// <remarks>
    /// Pausing alone is not enough, which is the whole reason this is one call rather than the listener's own
    /// <c>Pause</c>/<c>Resume</c>. An utterance the detector closed just as the key went down is already being
    /// transcribed, on a loop that is not looking at the paused flag while it waits for the transcriber; it
    /// arrives afterwards and would still be sent. Without that, the assistant gets the first half of the
    /// sentence and the session gets the second.
    /// </remarks>
    IDisposable SuspendForHold();
}
