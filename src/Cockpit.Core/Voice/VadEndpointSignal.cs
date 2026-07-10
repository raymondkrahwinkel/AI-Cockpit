namespace Cockpit.Core.Voice;

/// <summary>The boundary event, if any, produced by feeding one VAD observation to <see cref="VadEndpointDetector"/>.</summary>
public enum VadEndpointSignal
{
    /// <summary>No boundary crossed on this observation.</summary>
    None,

    /// <summary>Enough contiguous speech has accumulated to treat this as the start of an utterance.</summary>
    SpeechStarted,

    /// <summary>Trailing silence has reached the timeout, closing the current utterance.</summary>
    SpeechEnded,
}
