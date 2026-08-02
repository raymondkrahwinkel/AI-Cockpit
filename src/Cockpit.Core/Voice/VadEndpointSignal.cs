namespace Cockpit.Core.Voice;

// The boundary event, if any, produced by feeding one VAD observation to `VadEndpointDetector`.
public enum VadEndpointSignal
{
    // No boundary crossed on this observation.
    None,

    // Enough contiguous speech has accumulated to treat this as the start of an utterance.
    SpeechStarted,

    // Trailing silence has reached the timeout, closing the current utterance.
    SpeechEnded,
}
